using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using TimeArr.App.Ipc;
using TimeArr.Commands;
using TimeArr.Core;
using TimeArr.Data;
using TimeArr.Tests.Commands;

namespace TimeArr.Tests.App;

/// <summary>The single-instance channel, end to end over a real Unix socket in a temp folder.</summary>
public sealed class InstanceChannelTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // Short root: Unix socket paths are limited to ~107 bytes.
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ta-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly FakeClock _clock = new(T0);
    private readonly List<IDisposable> _cleanup = [];
    private int _shows;

    public InstanceChannelTests() => Assert.True(InstanceChannel.PrepareFolder(_folder).IsSuccess);

    public void Dispose()
    {
        foreach (var item in _cleanup)
            item.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private string SocketPath => InstanceChannel.SocketPath(_folder).Value;

    private TimerCommandHandler StartServer()
    {
        var handler = new TimerCommandHandler(new SqliteTimerStorage(Path.Combine(_folder, "time-arr.db")), _clock);
        var server = new IpcServer(InstanceChannel.Listen(SocketPath).Value, handler, () => Interlocked.Increment(ref _shows));
        server.Start();
        _cleanup.Add(server);
        return handler;
    }

    [Fact]
    public void Folder_and_socket_are_owner_only()
    {
        if (OperatingSystem.IsWindows())
            return;
        StartServer();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_folder));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SocketPath));
    }

    [Fact]
    public void Nobody_listening_is_not_found()
    {
        Assert.Equal(ErrorCode.NotFound, InstanceChannel.Send(SocketPath, ["list"], Timeout).Error.Code);
    }

    [Fact]
    public void Forwarded_commands_run_in_the_running_instance()
    {
        var handler = StartServer();

        var started = InstanceChannel.Send(SocketPath, ["start"], Timeout).Value;
        _clock.Advance(TimeSpan.FromSeconds(90));
        var status = InstanceChannel.Send(SocketPath, ["status"], Timeout).Value;

        Assert.Equal(0, started.ExitCode);
        Assert.Equal(0, status.ExitCode);
        Assert.Contains("running", status.Output, StringComparison.Ordinal);
        Assert.Contains("0:01:30", status.Output, StringComparison.Ordinal);
        Assert.True(handler.Execute(new TimerCommand.Get(new TimerRef.Default())).Value.Timers[0].State.IsRunning);
    }

    [Fact]
    public void Errors_and_bad_arguments_come_back_with_exit_codes()
    {
        StartServer();

        var unknown = InstanceChannel.Send(SocketPath, ["start", "nope"], Timeout).Value;
        var bad = InstanceChannel.Send(SocketPath, ["frobnicate"], Timeout).Value;

        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("nope", unknown.Output, StringComparison.Ordinal);
        Assert.Equal(2, bad.ExitCode);
    }

    [Fact]
    public void Show_and_no_arguments_bring_the_window_forward()
    {
        StartServer();

        InstanceChannel.Send(SocketPath, [], Timeout);
        InstanceChannel.Send(SocketPath, ["show"], Timeout);

        Assert.Equal(2, _shows);
    }

    [Fact]
    public void Only_one_process_holds_the_lock()
    {
        var first = InstanceChannel.TryLock(_folder);
        Assert.True(first.IsSuccess);
        _cleanup.Add(first.Value);

        Assert.Equal(ErrorCode.Conflict, InstanceChannel.TryLock(_folder).Error.Code);

        first.Value.Dispose();
        var second = InstanceChannel.TryLock(_folder);
        Assert.True(second.IsSuccess);
        _cleanup.Add(second.Value);
    }

    [Fact]
    public void Stale_socket_file_from_a_dead_instance_is_replaced()
    {
        File.WriteAllText(SocketPath, "left over");

        StartServer();

        Assert.Equal(0, InstanceChannel.Send(SocketPath, ["list"], Timeout).Value.ExitCode);
    }

    // --- malformed input over the socket ---

    private byte[] RawExchange(byte[] payload)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.ReceiveTimeout = 10_000;
        socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
        socket.Send(payload);
        socket.Shutdown(SocketShutdown.Send);
        var buffer = new byte[64 * 1024];
        var total = 0;
        int n;
        while ((n = socket.Receive(buffer, total, buffer.Length - total, SocketFlags.None)) > 0)
            total += n;
        return buffer[..total];
    }

    private static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    private static string ReplyText(byte[] raw) => raw.Length > 4 ? Encoding.UTF8.GetString(raw, 4, raw.Length - 4) : "";

    [Theory]
    [InlineData("""{"args":["start"],"extra":1}""")]
    [InlineData("""{"args":"start"}""")]
    [InlineData("""{"args":[1,2]}""")]
    [InlineData("""{"args":[null]}""")]
    [InlineData("""{}""")]
    [InlineData("""not json""")]
    [InlineData("""[[[[[[[[[[["deep"]]]]]]]]]]]""")]
    public void Malformed_requests_get_an_error_reply_and_change_nothing(string json)
    {
        var handler = StartServer();

        var reply = ReplyText(RawExchange(Frame(json)));

        Assert.Contains("\"exitCode\":2", reply, StringComparison.Ordinal);
        Assert.False(handler.Execute(new TimerCommand.Get(new TimerRef.Default())).Value.Timers[0].State.IsRunning);
    }

    [Fact]
    public void Oversized_frame_is_refused_without_reading_it()
    {
        StartServer();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, IpcProtocol.MaxRequestBytes + 1);

        Assert.Contains("out of range", ReplyText(RawExchange(header)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_client_that_sends_nothing_times_out_and_the_server_keeps_working()
    {
        StartServer();
        using var idle = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        idle.Connect(new UnixDomainSocketEndPoint(SocketPath));

        // Other clients aren't blocked by the idle one.
        Assert.Equal(0, InstanceChannel.Send(SocketPath, ["list"], Timeout).Value.ExitCode);
    }

    [Fact]
    public void Many_clients_at_once_all_get_answers()
    {
        StartServer();
        var exitCodes = new int[40];

        Parallel.For(0, exitCodes.Length, i =>
            exitCodes[i] = InstanceChannel.Send(SocketPath, [i % 2 == 0 ? "start" : "pause"], TimeSpan.FromSeconds(30)).Value.ExitCode);

        Assert.All(exitCodes, code => Assert.Equal(0, code));
    }
}
