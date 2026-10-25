using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using DrawArr.App;
using DrawArr.App.Ipc;
using DrawArr.Commands;
using DrawArr.Core;
using DrawArr.Files;

namespace DrawArr.UiTests;

/// <summary>The broker and the instance socket beyond <see cref="BrokerTests"/>, against a real app host without windows.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class BrokerGapTests : IDisposable
{
    // Short, because a socket path must stay under 108 bytes.
    private static readonly System.Text.Json.JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dab-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly AppHost _host;
    private readonly Broker _broker;
    private readonly List<IDisposable> _cleanup = [];
    private int _raised;

    public BrokerGapTests()
    {
        Directory.CreateDirectory(_folder);
        _host = new AppHost(TimeProvider.System, new AutosaveFolder(Path.Combine(_folder, "autosave")), action => action());
        _broker = new Broker(_host, _ => Interlocked.Increment(ref _raised));
    }

    public void Dispose()
    {
        foreach (var item in Enumerable.Reverse(_cleanup))
            item.Dispose();
        _host.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private string SocketPath => Path.Combine(_folder, InstanceChannel.SocketFileName);

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_folder, name)).FullName;

    private static string WriteFile(string path, int boxes = 0, int textLength = 0)
    {
        var drawing = Drawing.Empty;
        for (var id = 1; id <= boxes; id++)
            drawing = drawing.With(Box.Create(id, new Rect(0, 0, 10, 10), Colour.White, new string('x', textLength)).Value);
        Assert.True(DrawingFile.Save(drawing, path).IsSuccess);
        return path;
    }

    private IpcReply RunIn(string folder, params string[] args) => _broker.Handle(args, folder);

    private IpcReply Run(params string[] args) => RunIn(_folder, args);

    private static void Ok(IpcReply reply) => Assert.True(reply.ExitCode == 0, $"exit {reply.ExitCode}: {reply.Output}");

    private void Serve()
    {
        var listening = InstanceChannel.Listen(SocketPath);
        Assert.True(listening.IsSuccess, listening.ToString());
        var server = new IpcServer(listening.Value, _broker);
        server.Start();
        _cleanup.Add(server);
    }

    private Socket Connect()
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _cleanup.Add(socket);
        socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
        socket.ReceiveTimeout = 15000;
        return socket;
    }

    private static void SendFrame(Socket socket, byte[] body, int? length = null)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length ?? body.Length);
        socket.Send(header);
        if (body.Length > 0)
            socket.Send(body);
    }

    private Result<IpcReply> List() => InstanceChannel.Send(SocketPath, ["list"], _folder, TimeSpan.FromSeconds(10));

    // --- files and folders ---

    [Fact]
    public void Relative_names_from_different_folders_reach_the_same_drawing()
    {
        var a = Folder("a");
        var b = Folder("b");
        WriteFile(Path.Combine(a, "plan.xml"));

        Ok(RunIn(a, "add-box", "plan.xml", "0", "0", "10", "10"));
        Ok(RunIn(b, "add-box", "../a/plan.xml", "20", "0", "10", "10"));
        Ok(RunIn(_folder, "add-box", "./a/../a/plan.xml", "40", "0", "10", "10"));

        var session = Assert.Single(_host.Sessions);
        Assert.Equal(3, session.Latest.Count);
    }

    [Fact]
    public void The_same_name_in_two_folders_is_two_drawings()
    {
        var a = Folder("a");
        var b = Folder("b");
        WriteFile(Path.Combine(a, "plan.xml"));
        WriteFile(Path.Combine(b, "plan.xml"));

        Ok(RunIn(a, "add-box", "plan.xml", "0", "0", "10", "10"));
        Ok(RunIn(b, "show", "plan.xml"));

        Assert.Equal(2, _host.Sessions.Count);
        Assert.Equal("The drawing is empty.", RunIn(b, "show", "plan.xml").Output);
    }

    [Fact]
    public void Opening_several_files_opens_the_good_ones_and_reports_the_bad_one()
    {
        WriteFile(Path.Combine(_folder, "one.xml"));
        WriteFile(Path.Combine(_folder, "two.xml"));

        var reply = Run("open", "one.xml", "missing.xml", "two.xml");

        Assert.Equal(1, reply.ExitCode);
        Assert.Equal(2, _host.Sessions.Count);
        var lines = reply.Output.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Opened ", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Opened", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("Opened ", lines[2], StringComparison.Ordinal);
        Assert.Equal(2, _raised);
    }

    [Fact]
    public void An_edit_to_a_missing_file_is_refused_and_creates_nothing()
    {
        var reply = Run("add-box", "nope.xml", "0", "0", "10", "10");

        Assert.Equal(1, reply.ExitCode);
        Assert.False(File.Exists(Path.Combine(_folder, "nope.xml")));
        Assert.Empty(_host.Sessions);
    }

    [Fact]
    public void A_refused_edit_to_an_unopened_file_still_leaves_it_open()
    {
        // Pins that opening comes before the edit, so even a refused edit opens a window.
        WriteFile(Path.Combine(_folder, "plan.xml"));

        Assert.Equal(1, Run("delete", "plan.xml", "5").ExitCode);

        Assert.Single(_host.Sessions);
    }

    [Fact]
    public void Save_finds_an_unnamed_drawing_by_its_autosave_path()
    {
        var created = _host.New().Value;
        Ok(Run("add-box", created.FilePath, "0", "0", "10", "10"));

        Ok(Run("save", created.FilePath));

        Assert.Equal(1, DrawingFile.Load(created.FilePath).Value.Count);
    }

    [Fact]
    public void List_with_nothing_open_says_so_and_new_names_its_autosave_file()
    {
        Assert.Equal("No drawings are open.", Run("list").Output);

        var reply = Run("new");

        Ok(reply);
        Assert.Contains(Assert.Single(_host.Sessions).FilePath, reply.Output, StringComparison.Ordinal);
        Assert.Contains("unnamed, autosaved", Run("list").Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_text_with_a_line_break_is_shown_escaped()
    {
        WriteFile(Path.Combine(_folder, "plan.xml"));
        Ok(Run("add-box", "plan.xml", "0", "0", "40", "20"));

        Ok(Run("set-text", "plan.xml", "1", @"top\nbottom"));

        Assert.Equal("top\nbottom", _host.Sessions[0].Latest.Boxes[1].Text);
        Assert.Contains(@"text ""top\nbottom""", Run("show", "plan.xml").Output, StringComparison.Ordinal);
    }

    // --- sources and undo ---

    [Fact]
    public async Task History_names_the_window_the_cli_and_the_agent()
    {
        var path = WriteFile(Path.Combine(_folder, "plan.xml"));
        Ok(Run("add-box", "plan.xml", "0", "0", "10", "10"));
        Ok(Run("--agent", "add-box", "plan.xml", "20", "0", "10", "10"));
        using var window = _host.Queue.Join(CommandSource.Window);
        Assert.True((await window.Send(_host.Find(path)!.DocumentId, new DrawCommand.AddBox(40, 0, 10, 10, Colour.Red))).IsSuccess);

        var lines = Run("history", "plan.xml").Output.Split('\n');

        Assert.Equal(3, lines.Length);
        Assert.Contains("cli", lines[0], StringComparison.Ordinal);
        Assert.Contains("agent", lines[1], StringComparison.Ordinal);
        Assert.Contains("window", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undo_from_the_cli_takes_back_a_windows_change_and_the_window_can_undo_the_cli()
    {
        var path = WriteFile(Path.Combine(_folder, "plan.xml"));
        Ok(Run("show", "plan.xml"));
        var id = _host.Find(path)!.DocumentId;
        using var window = _host.Queue.Join(CommandSource.Window);
        Assert.True((await window.Send(id, new DrawCommand.AddBox(0, 0, 10, 10, Colour.Red))).IsSuccess);

        Ok(Run("undo", "plan.xml"));
        Assert.Equal("The drawing is empty.", Run("show", "plan.xml").Output);

        Ok(Run("redo", "plan.xml"));
        Ok(Run("add-box", "plan.xml", "50", "0", "10", "10"));
        Assert.True((await window.Send(id, new DrawCommand.Undo())).IsSuccess);

        Assert.Equal(1, _host.Find(path)!.Latest.Count);
        Assert.Contains("undone", Run("history", "plan.xml").Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Undo_with_nothing_to_undo_says_so()
    {
        WriteFile(Path.Combine(_folder, "plan.xml"));

        var reply = Run("undo", "plan.xml");

        Assert.Contains("undo", reply.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Now at", reply.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Redo_with_nothing_to_redo_doesnt_claim_to_have_moved()
    {
        WriteFile(Path.Combine(_folder, "plan.xml"));
        Ok(Run("add-box", "plan.xml", "0", "0", "10", "10"));

        var reply = Run("redo", "plan.xml");

        Assert.DoesNotContain("Now at", reply.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Undo_after_undoing_everything_says_nothing_is_left()
    {
        WriteFile(Path.Combine(_folder, "plan.xml"));
        Ok(Run("add-box", "plan.xml", "0", "0", "10", "10"));

        Assert.Equal("Nothing left to undo.", Run("undo", "plan.xml").Output);
    }

    // --- concurrency ---

    [Fact]
    public async Task Many_cli_calls_at_once_on_one_drawing_all_land_once()
    {
        WriteFile(Path.Combine(_folder, "plan.xml"));

        var replies = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            Task.Run(() => Run("add-box", "plan.xml", (i * 20).ToString(System.Globalization.CultureInfo.InvariantCulture), "0", "10", "10"),
                TestContext.Current.CancellationToken)));

        Assert.All(replies, Ok);
        var session = Assert.Single(_host.Sessions);
        Assert.Equal(40, session.Latest.Boxes.Count);
        Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i), session.Latest.Boxes.Keys.Order());
        Assert.Equal(40, Run("history", "plan.xml").Output.Split('\n').Length);
    }

    [Fact]
    public async Task Calls_racing_over_the_socket_to_open_one_file_share_one_session()
    {
        WriteFile(Path.Combine(_folder, "plan.xml"), boxes: 300);
        Serve();

        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            InstanceChannel.Send(SocketPath, ["move-box", "plan.xml", (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "100", "100"],
                _folder, TimeSpan.FromSeconds(20)), TestContext.Current.CancellationToken)));

        Assert.All(replies, r => Ok(r.Value));
        Assert.Single(_host.Sessions);
    }

    // --- the socket ---

    [Theory]
    [InlineData("{\"args\":[\"list\"],\"workingDirectory\":\"/tmp\",\"extra\":1}")]
    [InlineData("{\"args\":null,\"workingDirectory\":\"/tmp\"}")]
    [InlineData("{\"args\":[null],\"workingDirectory\":\"/tmp\"}")]
    [InlineData("{\"args\":[\"list\"]}")]
    [InlineData("[\"list\"]")]
    [InlineData("not json")]
    [InlineData("{\"args\":[[\"deep\"]],\"workingDirectory\":\"/tmp\"}")]
    public void Malformed_requests_get_exit_code_2_and_the_server_keeps_serving(string json)
    {
        Serve();
        var socket = Connect();

        SendFrame(socket, Encoding.UTF8.GetBytes(json));
        var reply = IpcProtocol.ReadReply(socket);

        Assert.True(reply.IsSuccess, reply.ToString());
        Assert.Equal(2, reply.Value.ExitCode);
        Assert.True(List().IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Frame_sizes_out_of_range_are_refused_without_reading_the_body(int length)
    {
        Serve();
        var socket = Connect();

        SendFrame(socket, [], length);
        var reply = IpcProtocol.ReadReply(socket);

        Assert.Equal(2, reply.Value.ExitCode);
        Assert.True(List().IsSuccess);
    }

    [Fact]
    public void A_client_that_dribbles_its_request_is_cut_off_and_others_are_served_meanwhile()
    {
        Serve();
        var slow = Connect();
        SendFrame(slow, [], 100);
        var watch = Stopwatch.StartNew();

        Assert.True(List().IsSuccess);
        var closed = false;
        while (!closed && watch.Elapsed < IpcServer.IoTimeout * 3)
        {
            try
            {
                slow.Send("{"u8.ToArray());
                // Readable here means the server has closed its end.
                closed = slow.Poll(500_000, SelectMode.SelectRead);
            }
            catch (SocketException)
            {
                closed = true;
            }
        }

        Assert.True(closed, $"The dribbling client was still connected after {watch.Elapsed}.");
        Assert.True(watch.Elapsed < IpcServer.IoTimeout + TimeSpan.FromSeconds(2), $"Cut off only after {watch.Elapsed}.");
    }

    [Fact]
    public void Clients_reading_a_big_reply_slowly_dont_hold_every_slot()
    {
        WriteFile(Path.Combine(_folder, "big.xml"), boxes: 3000, textLength: BoxText.MaxLength);
        Ok(Run("show", "big.xml"));
        Serve();
        using var stop = new CancellationTokenSource();
        var request = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new IpcRequest(["show", "big.xml"], _folder), CamelCase);
        var readers = Enumerable.Range(0, IpcServer.MaxConcurrent).Select(_ =>
        {
            var socket = Connect();
            SendFrame(socket, request);
            // Each reader takes 64 KB every 2 s, so a reply of megabytes keeps it busy for over a minute.
            return Task.Run(async () =>
            {
                var chunk = new byte[64 * 1024];
                try
                {
                    while (!stop.IsCancellationRequested && socket.Receive(chunk) > 0)
                        await Task.Delay(2000, stop.Token);
                }
                catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // The test is over or the server cut this reader off.
                }
            }, TestContext.Current.CancellationToken);
        }).ToList();
        Thread.Sleep(1500);

        var watch = Stopwatch.StartNew();
        var listed = InstanceChannel.Send(SocketPath, ["list"], _folder, IpcServer.IoTimeout * 3);
        var took = watch.Elapsed;
        stop.Cancel();

        Assert.True(listed.IsSuccess, $"A plain list wasn't answered while {IpcServer.MaxConcurrent} slow readers were connected: {listed}");
        Assert.True(took < IpcServer.IoTimeout * 2, $"A plain list took {took}.");
    }

    [Fact]
    public void A_leftover_socket_file_means_nobody_is_running_and_can_be_replaced()
    {
        // .NET removes a socket's file when the socket is disposed, so a hard link keeps the dead socket file the way a crash would.
        var temporary = Path.Combine(_folder, "t.sock");
        using (var stale = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            stale.Bind(new UnixDomainSocketEndPoint(temporary));
            stale.Listen(1);
            using var link = Process.Start(new ProcessStartInfo("ln", [temporary, SocketPath]))!;
            link.WaitForExit();
            Assert.Equal(0, link.ExitCode);
        }
        Assert.True(File.Exists(SocketPath));

        Assert.Equal(ErrorCode.NotFound, List().Error.Code);

        Serve();
        Assert.True(List().IsSuccess);
    }

    [Fact]
    public void A_plain_file_at_the_socket_path_means_nobody_is_running_and_can_be_replaced()
    {
        File.WriteAllText(SocketPath, "junk");

        Assert.Equal(ErrorCode.NotFound, List().Error.Code);

        Serve();
        Assert.True(List().IsSuccess);
    }

    [Fact]
    public async Task Only_one_of_many_racing_lockers_becomes_the_owner()
    {
        var folder = Folder("data");
        using var start = new Barrier(16);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait(TestContext.Current.CancellationToken);
            return InstanceChannel.TryLock(folder);
        }, TestContext.Current.CancellationToken)));

        try
        {
            Assert.Single(results, r => r.IsSuccess);
            Assert.All(results.Where(r => r.IsFailure), r => Assert.Equal(ErrorCode.Conflict, r.Error.Code));
        }
        finally
        {
            foreach (var r in results.Where(r => r.IsSuccess))
                r.Value.Dispose();
        }
        using var again = InstanceChannel.TryLock(folder).Value;
    }

    [Fact]
    public void Prepare_folder_refuses_a_symlink_and_tightens_a_loose_folder()
    {
        var real = Folder("real");
        var link = Path.Combine(_folder, "link");
        Directory.CreateSymbolicLink(link, real);
        Assert.Equal(ErrorCode.Ipc, InstanceChannel.PrepareFolder(link).Error.Code);

        File.SetUnixFileMode(real, (UnixFileMode)0b111_111_111);
        Assert.True(InstanceChannel.PrepareFolder(real).IsSuccess);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(real));
    }

    [Fact]
    public void A_socket_path_too_long_for_the_system_is_refused()
    {
        Assert.Equal(ErrorCode.Ipc, InstanceChannel.SocketPath("/" + new string('d', 100)).Error.Code);
    }
}
