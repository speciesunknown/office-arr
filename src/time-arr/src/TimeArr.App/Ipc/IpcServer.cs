using System.Net.Sockets;
using TimeArr.Commands;
using TimeArr.Core;

namespace TimeArr.App.Ipc;

/// <summary>Accepts forwarded command lines on the instance socket and runs them through the command queue, each connection with its own turn.</summary>
/// <remarks>A bug while serving a connection is not caught here, so it reaches the crash log and ends the instance (CODING.md #4).</remarks>
public sealed class IpcServer : IDisposable
{
    /// <summary>The most time one connection gets from accept to reply, however slowly its bytes arrive.</summary>
    public static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Most connections handled at once, with more waiting in the listen backlog.</summary>
    public const int MaxConcurrent = 8;

    private enum ServerState { Running = 1, Stopping = 2 }

    private readonly Socket _listener;
    private readonly CommandQueue _queue;
    /// <summary>Whether the server made its queue, and so stops it, or shares the instance's.</summary>
    private enum QueueOwnership { Owned = 1, Shared = 2 }

    private readonly QueueOwnership _ownership = QueueOwnership.Shared;
    private readonly Action _showWindow;
    private readonly SemaphoreSlim _slots = new(MaxConcurrent);
    private readonly Thread _acceptThread;
    private volatile ServerState _state = ServerState.Running;

    /// <summary>A server with a queue of its own over <paramref name="commands"/>, stopped along with the server.</summary>
    public IpcServer(Socket listener, ITimerCommands commands, Action showWindow)
        : this(listener, new CommandQueue(commands ?? throw new ArgumentNullException(nameof(commands))), showWindow) =>
        _ownership = QueueOwnership.Owned;

    /// <param name="queue">The instance's queue, shared with the window, which the caller stops.</param>
    public IpcServer(Socket listener, CommandQueue queue, Action showWindow)
    {
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _showWindow = showWindow ?? throw new ArgumentNullException(nameof(showWindow));
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "time-arr IPC" };
    }

    public void Start() => _acceptThread.Start();

    /// <summary>Handles one command line, the same way whether it arrived over IPC or was typed in this process.</summary>
    public static IpcReply Handle(string[] args, ITimerCommands commands, Action showWindow)
    {
        var parsed = Cli.Parse(args);
        if (parsed.IsFailure)
            return new IpcReply(2, parsed.Error.Message);

        switch (parsed.Value)
        {
            case CliRequest.Help:
                return new IpcReply(0, Cli.Usage);
            case CliRequest.ShowWindow:
                showWindow();
                return new IpcReply(0, "");
            case CliRequest.Run run:
                var reply = commands.Execute(run.Command);
                return reply.IsSuccess
                    ? new IpcReply(0, Cli.Format(run.Command, reply.Value))
                    : new IpcReply(1, ViewModels.MainViewModel.Describe(reply.Error));
            default:
                throw new InvalidOperationException($"Unhandled request {parsed.Value.GetType().Name}.");
        }
    }

    private void AcceptLoop()
    {
        while (_state == ServerState.Running)
        {
            Socket client;
            try
            {
                _slots.Wait();
                client = _listener.Accept();
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                _slots.Release();
                if (_state == ServerState.Stopping)
                    return;
                continue;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Serve(client);
                }
                finally
                {
                    client.Dispose();
                    _slots.Release();
                }
            });
        }
    }

    private void Serve(Socket client)
    {
        // Closing the socket at the deadline ends a slow request, so a client that dribbles bytes can't hold a slot.
        using var deadline = new Timer(_ => client.Dispose(), null, IoTimeout, Timeout.InfiniteTimeSpan);
        client.ReceiveTimeout = client.SendTimeout = (int)IoTimeout.TotalMilliseconds;
        var request = IpcProtocol.ReadRequest(client);
        // The command itself may wait on a busy database, so the deadline stops here and the reply has only its send timeout.
        deadline.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        IpcReply reply;
        if (request.IsSuccess)
        {
            using var requester = _queue.Join();
            reply = Handle(request.Value.Args, requester, _showWindow);
        }
        else
        {
            reply = new IpcReply(2, request.Error.Message);
        }
        // A client that went away can't be told anything, so a failed write needs no handling.
        IpcProtocol.Write(client, reply);
    }

    /// <summary>Stops accepting, letting connections already being served finish on their own.</summary>
    /// <remarks>The semaphore is deliberately not disposed, because in-flight connections may still release it and it holds no OS handle.</remarks>
    public void Dispose()
    {
        _state = ServerState.Stopping;
        _listener.Dispose();
        if (_ownership == QueueOwnership.Owned)
            _queue.Dispose();
    }
}
