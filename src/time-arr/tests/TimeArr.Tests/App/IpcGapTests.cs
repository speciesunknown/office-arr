using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using TimeArr.App.Ipc;
using TimeArr.Commands;
using TimeArr.Core;
using TimeArr.Data;
using TimeArr.Tests.Commands;

namespace TimeArr.Tests.App;

/// <summary>ITimerCommands that records what it was asked and answers from a function.</summary>
internal sealed class RecordingCommands(Func<TimerCommand, Result<CommandReply>> answer) : ITimerCommands
{
    public List<TimerCommand> Seen { get; } = [];
    public Error? StorageProblem => null;

    public Result<CommandReply> Execute(TimerCommand command)
    {
        lock (Seen)
            Seen.Add(command);
        return answer(command);
    }
}

/// <summary>
/// Edge cases for <see cref="IpcServer"/>, <see cref="IpcProtocol"/> and <see cref="InstanceChannel"/> not covered by
/// <see cref="InstanceChannelTests"/>, over real Unix sockets in a short temp folder (socket paths are limited to ~107 bytes).
/// </summary>
public sealed class IpcGapTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ta-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly FakeClock _clock = new(T0);
    private readonly List<IDisposable> _cleanup = [];

    public IpcGapTests() => Assert.True(InstanceChannel.PrepareFolder(_folder).IsSuccess);

    public void Dispose()
    {
        foreach (var item in Enumerable.Reverse(_cleanup))
            item.Dispose();
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private string SocketPath => InstanceChannel.SocketPath(_folder).Value;

    private TimerCommandHandler StartServer(Action? showWindow = null)
    {
        var handler = new TimerCommandHandler(new SqliteTimerStorage(Path.Combine(_folder, "time-arr.db")), _clock);
        var server = new IpcServer(InstanceChannel.Listen(SocketPath).Value, handler, showWindow ?? (() => { }));
        server.Start();
        _cleanup.Add(server);
        return handler;
    }

    private Socket Connect()
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
        {
            ReceiveTimeout = (int)Timeout.TotalMilliseconds,
            SendTimeout = (int)Timeout.TotalMilliseconds,
        };
        socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
        _cleanup.Add(socket);
        return socket;
    }

    private static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    private static byte[] Header(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        return header;
    }

    private IpcReply Ask(params string[] args)
    {
        var reply = InstanceChannel.Send(SocketPath, args, Timeout);
        Assert.True(reply.IsSuccess, reply.ToString());
        return reply.Value;
    }

    // --- IpcServer.Handle ---

    [Theory]
    [InlineData]
    [InlineData("show")]
    public void Show_brings_the_window_forward_without_running_a_command(params string[] args)
    {
        var commands = new RecordingCommands(_ => throw new InvalidOperationException("should not run"));
        var shown = 0;

        var reply = IpcServer.Handle(args, commands, () => shown++);

        Assert.Equal(new IpcReply(0, ""), reply);
        Assert.Equal(1, shown);
        Assert.Empty(commands.Seen);
    }

    [Fact]
    public void Help_and_bad_command_lines_never_reach_the_commands()
    {
        var commands = new RecordingCommands(_ => throw new InvalidOperationException("should not run"));

        Assert.Equal(new IpcReply(0, Cli.Usage), IpcServer.Handle(["help"], commands, () => { }));
        Assert.Equal(2, IpcServer.Handle(["frobnicate"], commands, () => { }).ExitCode);
        Assert.Equal(2, IpcServer.Handle(["delete"], commands, () => { }).ExitCode);
        Assert.Empty(commands.Seen);
    }

    [Fact]
    public void A_failed_command_is_exit_code_1_with_the_user_facing_message()
    {
        var commands = new RecordingCommands(_ => new Error(ErrorCode.StorageForeign, "detail"));

        var reply = IpcServer.Handle(["list"], commands, () => { });

        Assert.Equal(1, reply.ExitCode);
        Assert.Equal(TimeArr.ViewModels.MainViewModel.Describe(new Error(ErrorCode.StorageForeign, "detail")), reply.Output);
    }

    [Fact]
    public void Bad_names_on_the_command_line_are_exit_code_2_and_never_reach_the_commands()
    {
        var commands = new RecordingCommands(_ => throw new InvalidOperationException("should not run"));

        Assert.Equal(2, IpcServer.Handle(["start", ""], commands, () => { }).ExitCode);
        Assert.Equal(2, IpcServer.Handle(["add", "-x"], commands, () => { }).ExitCode);
        Assert.Equal(2, IpcServer.Handle(["rename", "default", "a\tb"], commands, () => { }).ExitCode);
        Assert.Empty(commands.Seen);
    }

    [Fact]
    public void An_unknown_name_is_exit_code_1()
    {
        var handler = new TimerCommandHandler(new SqliteTimerStorage(Path.Combine(_folder, "time-arr.db")), _clock);

        Assert.Equal(1, IpcServer.Handle(["start", "nobody"], handler, () => { }).ExitCode);
    }

    // --- framing and robustness ---

    [Fact]
    public void A_request_sent_one_byte_at_a_time_is_understood()
    {
        StartServer();
        var socket = Connect();

        foreach (var b in Frame("""{"args":["add","slow"]}"""))
        {
            socket.Send([b]);
            Thread.Sleep(5);
        }
        var reply = IpcProtocol.ReadReply(socket);

        Assert.True(reply.IsSuccess, reply.ToString());
        Assert.Equal(0, reply.Value.ExitCode);
        Assert.Contains("slow", Ask("list").Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Non_positive_frame_lengths_get_exit_code_2(int length)
    {
        StartServer();
        var socket = Connect();

        socket.Send(Header(length));
        var reply = IpcProtocol.ReadReply(socket);

        Assert.Equal(2, reply.Value.ExitCode);
        Assert.Equal(0, Ask("list").ExitCode);
    }

    [Fact]
    public void Header_then_hang_up_leaves_the_server_working()
    {
        StartServer();
        var socket = Connect();
        socket.Send(Header(50));
        socket.Close();

        Assert.Equal(0, Ask("list").ExitCode);
    }

    [Fact]
    public void Half_a_body_then_hang_up_runs_nothing()
    {
        var handler = StartServer();
        var frame = Frame("""{"args":["add","half"]}""");
        var socket = Connect();
        socket.Send(frame, 0, frame.Length - 5, SocketFlags.None);
        socket.Close();

        Assert.Equal(0, Ask("list").ExitCode);
        Assert.Single(handler.Execute(new TimerCommand.List()).Value.Timers);
    }

    [Fact]
    public void Only_one_request_per_connection_is_served()
    {
        var handler = StartServer();
        var socket = Connect();

        socket.Send([.. Frame("""{"args":["add","first"]}"""), .. Frame("""{"args":["add","second"]}""")]);
        var reply = IpcProtocol.ReadReply(socket);
        // Closed after the one reply: a clean EOF, or a reset because the second frame was never read.
        int after;
        try { after = socket.Receive(new byte[16]); }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) { after = 0; }

        Assert.Equal(0, reply.Value.ExitCode);
        Assert.Equal(0, after);
        Assert.DoesNotContain(handler.Execute(new TimerCommand.List()).Value.Timers, t => t.Name == "second");
    }

    [Fact]
    public void A_client_that_leaves_before_the_reply_does_not_hurt_the_server()
    {
        StartServer();
        for (var i = 0; i < 20; i++)
        {
            var socket = Connect();
            socket.Send(Frame("""{"args":["list"]}"""));
            socket.Close();
        }

        Assert.Equal(0, Ask("list").ExitCode);
    }

    [Fact]
    public async Task Clients_dripping_bytes_cannot_lock_out_everyone_else()
    {
        // IoTimeout applies per Receive call, so MaxConcurrent clients sending a byte a second
        // hold every slot and lock the real CLI out for as long as they keep dripping.
        StartServer();
        var drippers = Enumerable.Range(0, IpcServer.MaxConcurrent).Select(_ => Connect()).ToList();
        foreach (var d in drippers)
            d.Send(Header(IpcProtocol.MaxRequestBytes));

        using var stop = new CancellationTokenSource();
        var drip = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var d in drippers)
                {
                    try { d.Send([(byte)' ']); } catch (SocketException) { }
                }
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        });

        // Wait past IoTimeout, so a per-request deadline would have dropped the drippers by now.
        await Task.Delay(IpcServer.IoTimeout + TimeSpan.FromSeconds(1.5));
        var reply = InstanceChannel.Send(SocketPath, ["list"], TimeSpan.FromSeconds(3));
        await stop.CancelAsync();
        await drip;

        Assert.True(reply.IsSuccess, $"A normal client got no answer while {drippers.Count} slow clients were connected: {reply}");
    }

    [Fact]
    public void After_dispose_clients_are_turned_away_quickly()
    {
        StartServer();
        Assert.Equal(0, Ask("list").ExitCode);
        _cleanup.OfType<IpcServer>().Single().Dispose();

        var watch = Stopwatch.StartNew();
        var reply = InstanceChannel.Send(SocketPath, ["list"], Timeout);

        Assert.True(reply.IsFailure);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Many_more_clients_than_slots_all_get_answers()
    {
        StartServer();
        var results = new System.Collections.Concurrent.ConcurrentBag<Result<IpcReply>>();

        Parallel.For(0, IpcServer.MaxConcurrent * 6, i =>
            results.Add(InstanceChannel.Send(SocketPath, ["add", $"t{i}"], TimeSpan.FromSeconds(20))));

        Assert.All(results, r => Assert.Equal(0, r.Value.ExitCode));
        Assert.Equal(IpcServer.MaxConcurrent * 6 + 1, Ask("list").Output.Split('\n').Length);
    }

    // --- the client side (Send) ---

    /// <summary>A bare listener at the socket path that does <paramref name="serve"/> with each connection.</summary>
    private void FakeInstance(Action<Socket> serve)
    {
        var listener = InstanceChannel.Listen(SocketPath).Value;
        _cleanup.Add(listener);
        var thread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var client = listener.Accept();
                    try { serve(client); } finally { client.Dispose(); }
                }
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        }) { IsBackground = true };
        thread.Start();
    }

    [Fact]
    public void An_instance_that_never_answers_times_out()
    {
        FakeInstance(c => Thread.Sleep(TimeSpan.FromSeconds(10)));
        var watch = Stopwatch.StartNew();

        var reply = InstanceChannel.Send(SocketPath, ["list"], TimeSpan.FromSeconds(1));

        Assert.Equal(ErrorCode.Ipc, reply.Error.Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Theory]
    [InlineData("""{"exitCode":0}""")]
    [InlineData("""{"exitCode":300,"output":"x"}""")]
    [InlineData("""{"exitCode":-1,"output":"x"}""")]
    [InlineData("""{"exitCode":0,"output":"x","extra":true}""")]
    [InlineData("""not json""")]
    public void Junk_replies_are_an_ipc_error(string json)
    {
        FakeInstance(c =>
        {
            IpcProtocol.ReadRequest(c);
            c.Send(Frame(json));
        });

        Assert.Equal(ErrorCode.Ipc, InstanceChannel.Send(SocketPath, ["list"], Timeout).Error.Code);
    }

    [Fact]
    public void An_oversized_reply_header_is_refused()
    {
        FakeInstance(c =>
        {
            IpcProtocol.ReadRequest(c);
            c.Send(Header(IpcProtocol.MaxReplyBytes + 1));
        });

        Assert.Equal(ErrorCode.Ipc, InstanceChannel.Send(SocketPath, ["list"], Timeout).Error.Code);
    }

    [Fact]
    public void A_regular_file_at_the_socket_path_is_not_a_running_instance()
    {
        File.WriteAllText(SocketPath, "not a socket");

        Result<IpcReply>? reply = null;
        Assert.Null(Record.Exception(() => reply = InstanceChannel.Send(SocketPath, ["list"], Timeout)));
        Assert.True(reply!.IsFailure);
    }

    [Fact]
    public void A_missing_folder_means_nobody_is_running()
    {
        Assert.Equal(ErrorCode.NotFound, InstanceChannel.Send(Path.Combine(_folder, "nope", "time-arr.sock"), ["list"], Timeout).Error.Code);
    }

    // --- InstanceChannel housekeeping ---

    [Fact]
    public void Lock_is_exclusive_and_free_again_once_released()
    {
        var first = InstanceChannel.TryLock(_folder).Value;

        Assert.Equal(ErrorCode.Conflict, InstanceChannel.TryLock(_folder).Error.Code);
        first.Dispose();
        using var second = InstanceChannel.TryLock(_folder).Value;
    }

    [Fact]
    public void Lock_file_is_owner_only()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var held = InstanceChannel.TryLock(_folder).Value;

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_folder, InstanceChannel.LockFileName)));
    }

    [Fact]
    public void Lock_in_a_missing_folder_is_an_ipc_error_not_a_conflict()
    {
        Assert.Equal(ErrorCode.Ipc, InstanceChannel.TryLock(Path.Combine(_folder, "missing")).Error.Code);
    }

    [Fact]
    public void Loose_folder_permissions_are_tightened()
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(_folder, (UnixFileMode)0b111_101_101); // 0755

        Assert.True(InstanceChannel.PrepareFolder(_folder).IsSuccess);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_folder));
    }

    [Fact]
    public void Symlinked_folder_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;

        var link = _folder + "-link";
        Directory.CreateSymbolicLink(link, _folder);
        try
        {
            Assert.Equal(ErrorCode.Ipc, InstanceChannel.PrepareFolder(link).Error.Code);
        }
        finally
        {
            File.Delete(link);
        }
    }

    [Fact]
    public void Socket_path_limit_counts_utf8_bytes()
    {
        var suffix = "/" + InstanceChannel.SocketFileName;
        var fits = "/" + new string('a', 107 - suffix.Length - 1);
        var tooLong = fits + "a";
        var multiByte = "/" + new string('a', 107 - suffix.Length - 2) + "é"; // 108 bytes, 107 chars

        Assert.True(InstanceChannel.SocketPath(fits).IsSuccess);
        Assert.Equal(ErrorCode.Ipc, InstanceChannel.SocketPath(tooLong).Error.Code);
        Assert.Equal(ErrorCode.Ipc, InstanceChannel.SocketPath(multiByte).Error.Code);
    }

    [Fact]
    public void Listen_on_a_folder_path_is_an_ipc_error()
    {
        Directory.CreateDirectory(SocketPath);

        Result<Socket>? listening = null;
        Assert.Null(Record.Exception(() => listening = InstanceChannel.Listen(SocketPath)));
        Assert.Equal(ErrorCode.Ipc, listening!.Error.Code);
    }

    [Fact]
    public void Listen_replaces_a_leftover_regular_file()
    {
        File.WriteAllText(SocketPath, "leftover");

        using var listening = InstanceChannel.Listen(SocketPath).Value;

        Assert.True(listening.IsBound);
    }
}
