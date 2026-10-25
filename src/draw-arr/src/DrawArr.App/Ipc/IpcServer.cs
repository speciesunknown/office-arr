using System.Net.Sockets;

namespace DrawArr.App.Ipc;

/// <summary>Accepts forwarded command lines on the instance socket and hands them to the <see cref="Broker"/>, each connection with its own turn in the queue.</summary>
/// <remarks>A bug while serving a connection is not caught here, so it reaches the crash log and ends the instance (CODING.md #4).</remarks>
public sealed class IpcServer : IDisposable
{
    /// <summary>The most time one connection gets from accept to reply, however slowly its bytes arrive.</summary>
    public static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Most connections handled at once, with more waiting in the listen backlog.</summary>
    public const int MaxConcurrent = 8;

    private enum ServerState { Running = 1, Stopping = 2 }

    private readonly Socket _listener;
    private readonly Broker _broker;
    private readonly SemaphoreSlim _slots = new(MaxConcurrent);
    private readonly Thread _acceptThread;
    private volatile ServerState _state = ServerState.Running;

    /// <param name="listener">The bound, listening instance socket.</param>
    /// <param name="broker">Runs each command line against the open drawings.</param>
    public IpcServer(Socket listener, Broker broker)
    {
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "draw-arr IPC" };
    }

    public void Start() => _acceptThread.Start();

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
        var reading = 1;
        using var deadline = new Timer(_ =>
        {
            // Only while still reading, so a deadline that fires just as the request arrives can't cut off a command that is about to run.
            if (Interlocked.Exchange(ref reading, 0) == 1)
                client.Dispose();
        }, null, IoTimeout, Timeout.InfiniteTimeSpan);
        client.ReceiveTimeout = client.SendTimeout = (int)IoTimeout.TotalMilliseconds;
        var request = IpcProtocol.ReadRequest(client);
        // The command itself may wait on a file being opened, so the deadline stops here and the reply has only its send timeout.
        if (Interlocked.Exchange(ref reading, 0) == 0)
            return;
        IpcReply reply;
        if (request.IsSuccess)
            reply = _broker.Handle(request.Value.Args, request.Value.WorkingDirectory);
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
    }
}
