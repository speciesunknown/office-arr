using System.Net.Sockets;
using TimeArr.Commands;
using TimeArr.Core;

namespace TimeArr.App.Ipc;

/// <summary>
/// Accepts forwarded command lines on the instance socket and runs them through the same
/// <see cref="ITimerCommands"/> as the window. Runs on background threads; the handler is thread-safe.
/// </summary>
public sealed class IpcServer : IDisposable
{
    /// <summary>How long a client gets to send its request, and we get to send the reply.</summary>
    public static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Most connections handled at once; more wait in the listen backlog.</summary>
    public const int MaxConcurrent = 8;

    private readonly Socket _listener;
    private readonly ITimerCommands _commands;
    private readonly Action _showWindow;
    private readonly SemaphoreSlim _slots = new(MaxConcurrent);
    private readonly Thread _acceptThread;
    private volatile bool _stopping;

    public IpcServer(Socket listener, ITimerCommands commands, Action showWindow)
    {
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _showWindow = showWindow ?? throw new ArgumentNullException(nameof(showWindow));
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "time-arr IPC" };
    }

    public void Start() => _acceptThread.Start();

    /// <summary>Handles one parsed command line. Shared with a fresh start, so both print the same.</summary>
    public static IpcReply Handle(string[] args, ITimerCommands commands, Action showWindow)
    {
        var parsed = Cli.Parse(args);
        if (parsed.IsFailure)
            return new IpcReply(2, parsed.Error.Message);

        var request = parsed.Value;
        if (request.Help)
            return new IpcReply(0, Cli.Usage);
        if (request.ShowWindow)
        {
            showWindow();
            return new IpcReply(0, "");
        }

        var reply = commands.Execute(request.Command!);
        return reply.IsSuccess
            ? new IpcReply(0, Cli.Format(request.Command!, reply.Value))
            : new IpcReply(1, ViewModels.MainViewModel.Describe(reply.Error));
    }

    private void AcceptLoop()
    {
        while (!_stopping)
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
                if (_stopping)
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
        client.ReceiveTimeout = client.SendTimeout = (int)IoTimeout.TotalMilliseconds;
        var request = IpcProtocol.ReadRequest(client);
        var reply = request.IsSuccess
            ? Handle(request.Value.Args, _commands, _showWindow)
            : new IpcReply(2, request.Error.Message);
        // A client that went away can't be told anything; nothing else to do.
        IpcProtocol.Write(client, reply);
    }

    /// <summary>Stops accepting. Connections already being served finish on their own.</summary>
    /// <remarks>
    /// <see cref="_slots"/> is deliberately not disposed: the accept thread and in-flight
    /// connections may still release it, and a SemaphoreSlim that never had its wait handle
    /// created holds no OS resources.
    /// </remarks>
    public void Dispose()
    {
        _stopping = true;
        _listener.Dispose();
    }
}
