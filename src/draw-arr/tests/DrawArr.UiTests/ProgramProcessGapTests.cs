using System.Diagnostics;
using System.Net.Sockets;
using DrawArr.App;
using DrawArr.App.Ipc;
using DrawArr.Core;
using DrawArr.Files;

namespace DrawArr.UiTests;

/// <summary>
/// The real <c>draw-arr</c> program as a separate process, with HOME and XDG_DATA_HOME in a temp folder and no display,
/// where the test plays the running instance whenever one is needed, so the child never opens a window.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class ProgramProcessGapTests : IDisposable
{
    // Short, because the socket path must stay under 108 bytes.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dap-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly List<IDisposable> _cleanup = [];
    private AppHost? _host;

    public ProgramProcessGapTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var item in Enumerable.Reverse(_cleanup))
            item.Dispose();
        _host?.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string DataFolder => Path.Combine(_root, "data", "draw-arr");
    private string SocketPath => Path.Combine(DataFolder, InstanceChannel.SocketFileName);
    private AppHost Host => _host ??= new AppHost(TimeProvider.System, new AutosaveFolder(Path.Combine(_root, "autosave")), action => action());

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static void WriteFile(string path) => Assert.True(DrawingFile.Save(Drawing.Empty, path).IsSuccess);

    private void HoldLock()
    {
        Assert.True(InstanceChannel.PrepareFolder(DataFolder).IsSuccess);
        _cleanup.Add(InstanceChannel.TryLock(DataFolder).Value);
    }

    private void ServeAsInstance()
    {
        var server = new IpcServer(InstanceChannel.Listen(SocketPath).Value, new Broker(Host, _ => { }));
        server.Start();
        _cleanup.Add(server);
    }

    private sealed record Run(int ExitCode, string Out, string Err, TimeSpan Took);

    private async Task<Run> DrawArrIn(string? workingDirectory, params string[] args)
    {
        var program = Path.Combine(AppContext.BaseDirectory, "draw-arr.dll");
        Assert.True(File.Exists(program), $"{program} not found next to the tests.");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? _root,
        };
        start.ArgumentList.Add(program);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment["HOME"] = Path.Combine(_root, "home");
        start.Environment["XDG_DATA_HOME"] = Path.Combine(_root, "data");
        // Without a display a child that wrongly tries to open a window fails instead of showing one.
        start.Environment.Remove("DISPLAY");
        start.Environment.Remove("WAYLAND_DISPLAY");

        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"draw-arr {string.Join(' ', args)} did not exit within 60 s.");
        }
        return new Run(process.ExitCode, (await stdout).Trim(), (await stderr).Trim(), watch.Elapsed);
    }

    private Task<Run> DrawArr(params string[] args) => DrawArrIn(null, args);

    // --- no instance needed ---

    [Fact]
    public async Task Help_prints_the_usage_without_an_instance()
    {
        var run = await DrawArr("help");

        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith("usage: draw-arr", run.Out, StringComparison.Ordinal);
        Assert.False(File.Exists(SocketPath));
    }

    [Fact]
    public async Task A_bad_command_line_exits_2_with_the_message_on_stderr()
    {
        var run = await DrawArr("move-box", "plan.xml", "x", "0", "0");

        Assert.Equal(2, run.ExitCode);
        Assert.Equal("", run.Out);
        Assert.Contains("'x' isn't an id", run.Err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("add-box", "plan.xml", "0", "0", "10", "10")]
    [InlineData("save", "plan.xml")]
    public async Task Commands_with_no_instance_running_say_so_and_start_nothing(params string[] args)
    {
        var run = await DrawArr(args);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("isn't running", run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_leftover_socket_file_is_reported_as_not_running()
    {
        Assert.True(InstanceChannel.PrepareFolder(DataFolder).IsSuccess);
        File.WriteAllText(SocketPath, "");

        var run = await DrawArr("list");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("isn't running", run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_data_folder_is_created_owner_only()
    {
        await DrawArr("list");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(DataFolder));
    }

    // --- with the test as the running instance ---

    [Fact]
    public async Task Relative_names_are_resolved_in_the_folder_the_command_was_typed_in()
    {
        HoldLock();
        ServeAsInstance();
        var a = Folder("a");
        var b = Folder("b");
        WriteFile(Path.Combine(a, "plan.xml"));

        var added = await DrawArrIn(a, "add-box", "plan.xml", "0", "0", "40", "20");
        var shown = await DrawArrIn(b, "show", "../a/plan.xml");

        Assert.Equal(0, added.ExitCode);
        Assert.Equal("Add box 1 at (0, 0) 40x20", added.Out);
        Assert.Equal("box 1 at (0, 0) 40x20 white", shown.Out);
        Assert.Equal(Path.Combine(a, "plan.xml"), Assert.Single(Host.Sessions).FilePath);
    }

    [Fact]
    public async Task An_agent_edit_and_its_refusal_come_back_with_their_exit_codes()
    {
        HoldLock();
        ServeAsInstance();
        WriteFile(Path.Combine(_root, "plan.xml"));

        Assert.Equal(0, (await DrawArr("--agent", "add-box", "plan.xml", "0", "0", "10", "10")).ExitCode);
        var refused = await DrawArr("delete", "plan.xml", "99");
        var history = await DrawArr("history", "plan.xml");

        Assert.Equal(1, refused.ExitCode);
        Assert.NotEqual("", refused.Err);
        Assert.Contains("agent", history.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_is_handed_to_the_running_instance_instead_of_starting_another()
    {
        HoldLock();
        ServeAsInstance();

        var run = await DrawArr("new");

        Assert.Equal(0, run.ExitCode);
        Assert.Single(Host.Sessions);
    }

    [Fact]
    public async Task Opening_waits_for_an_instance_that_holds_the_lock_but_is_still_starting()
    {
        HoldLock();
        WriteFile(Path.Combine(_root, "plan.xml"));
        var late = Task.Run(async () =>
        {
            await Task.Delay(1500);
            ServeAsInstance();
        }, TestContext.Current.CancellationToken);

        var run = await DrawArr("open", "plan.xml");
        await late;

        Assert.Equal(0, run.ExitCode);
        Assert.Single(Host.Sessions);
    }

    [Fact]
    public async Task Two_launches_racing_an_instance_that_is_still_starting_are_both_handed_over()
    {
        HoldLock();
        var late = Task.Run(async () =>
        {
            await Task.Delay(1000);
            ServeAsInstance();
        }, TestContext.Current.CancellationToken);

        var runs = await Task.WhenAll(DrawArr("new"), DrawArr("new"));
        await late;

        Assert.All(runs, r => Assert.Equal(0, r.ExitCode));
        Assert.Equal(2, Host.Sessions.Count);
    }

    [Fact]
    public async Task A_lock_holder_that_never_answers_is_reported_after_a_few_seconds()
    {
        HoldLock();
        WriteFile(Path.Combine(_root, "plan.xml"));

        var run = await DrawArr("open", "plan.xml");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("isn't answering", run.Err, StringComparison.Ordinal);
        Assert.InRange(run.Took, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task An_edit_while_the_instance_is_still_starting_waits_for_it()
    {
        HoldLock();
        WriteFile(Path.Combine(_root, "plan.xml"));
        var late = Task.Run(async () =>
        {
            await Task.Delay(1500);
            ServeAsInstance();
        }, TestContext.Current.CancellationToken);

        // Opening waits for a starting instance, so an edit typed straight after launching should too.
        var run = await DrawArr("add-box", "plan.xml", "0", "0", "10", "10");
        await late;

        Assert.True(run.ExitCode == 0, run.Err);
    }

    [Fact]
    public async Task Many_cli_processes_editing_one_drawing_at_once_all_land()
    {
        HoldLock();
        ServeAsInstance();
        WriteFile(Path.Combine(_root, "plan.xml"));

        var runs = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            DrawArr("add-box", "plan.xml", (i * 20).ToString(System.Globalization.CultureInfo.InvariantCulture), "0", "10", "10")));

        Assert.All(runs, r => Assert.True(r.ExitCode == 0, r.Err));
        Assert.Equal(10, Assert.Single(Host.Sessions).Latest.Boxes.Count);
    }

    [Fact]
    public async Task A_reply_with_terminal_escapes_is_printed_harmlessly()
    {
        HoldLock();
        var listener = InstanceChannel.Listen(SocketPath).Value;
        _cleanup.Add(listener);
        var esc = (char)0x1B;
        // A fake instance answers with escape sequences, as a confused or hostile one might.
        var serving = Task.Run(() =>
        {
            using var client = listener.Accept();
            IpcProtocol.ReadRequest(client);
            IpcProtocol.Write(client, new IpcReply(0, $"before{esc}]0;pwned{(char)7}after"));
        }, TestContext.Current.CancellationToken);

        var run = await DrawArr("list");
        await serving;

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain(esc, run.Out);
        Assert.DoesNotContain((char)7, run.Out);
    }

    [Fact]
    public async Task A_garbled_reply_is_reported_as_lost_contact()
    {
        HoldLock();
        var listener = InstanceChannel.Listen(SocketPath).Value;
        _cleanup.Add(listener);
        var serving = Task.Run(() =>
        {
            using var client = listener.Accept();
            IpcProtocol.ReadRequest(client);
            client.Send(new byte[] { 0, 0, 0, 3, (byte)'x', (byte)'y', (byte)'z' });
        }, TestContext.Current.CancellationToken);

        var run = await DrawArr("list");
        await serving;

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Lost contact", run.Err, StringComparison.Ordinal);
    }
}
