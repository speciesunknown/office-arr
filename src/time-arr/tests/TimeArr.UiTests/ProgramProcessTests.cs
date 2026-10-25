using System.Diagnostics;
using TimeArr.App.Ipc;
using TimeArr.Commands;
using TimeArr.Data;

namespace TimeArr.UiTests;

/// <summary>
/// The real <c>time-arr</c> program as a separate process, with HOME and XDG_DATA_HOME in a temp
/// folder and no display, where the test holds the lock whenever it runs <c>show</c> so the child never opens a window.
/// </summary>
public sealed class ProgramProcessTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    // Short: the socket path must stay under ~107 bytes.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ta-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<IDisposable> _cleanup = [];
    private int _shown;

    private string DataFolder => Path.Combine(_root, "data", "time-arr");
    private string SocketPath => Path.Combine(DataFolder, InstanceChannel.SocketFileName);

    public void Dispose()
    {
        foreach (var item in Enumerable.Reverse(_cleanup))
            item.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private TimerCommandHandler Handler() =>
        new(new SqliteTimerStorage(Path.Combine(DataFolder, "time-arr.db")), new FakeClock(T0));

    private void HoldLock()
    {
        Assert.True(InstanceChannel.PrepareFolder(DataFolder).IsSuccess);
        _cleanup.Add(InstanceChannel.TryLock(DataFolder).Value);
    }

    private TimerCommandHandler ServeAsInstance()
    {
        var handler = Handler();
        var server = new IpcServer(InstanceChannel.Listen(SocketPath).Value, handler, () => Interlocked.Increment(ref _shown));
        server.Start();
        _cleanup.Add(server);
        return handler;
    }

    private sealed record Run(int ExitCode, string Out, string Err, TimeSpan Took);

    private async Task<Run> TimeArr(params string[] args)
    {
        var program = Path.Combine(AppContext.BaseDirectory, "time-arr.dll");
        Assert.True(File.Exists(program), $"{program} not found next to the tests.");

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(program);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment["HOME"] = Path.Combine(_root, "home");
        start.Environment["XDG_DATA_HOME"] = Path.Combine(_root, "data");
        start.Environment.Remove("DISPLAY");
        start.Environment.Remove("WAYLAND_DISPLAY");

        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"time-arr {string.Join(' ', args)} did not exit within 30 s.");
        }
        return new Run(process.ExitCode, (await stdout).Trim(), (await stderr).Trim(), watch.Elapsed);
    }

    // --- no instance needed ---

    [Fact]
    public async Task Bad_command_line_exits_2_without_touching_the_data_folder()
    {
        var run = await TimeArr("frobnicate");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("Unknown command", run.Err, StringComparison.Ordinal);
        Assert.Equal("", run.Out);
        Assert.False(Directory.Exists(DataFolder));
    }

    [Fact]
    public async Task Help_exits_0_with_usage_on_stdout()
    {
        var run = await TimeArr("--help");

        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith("usage: time-arr", run.Out, StringComparison.Ordinal);
        Assert.False(Directory.Exists(DataFolder));
    }

    // --- forwarding to a running instance ---

    [Fact]
    public async Task Commands_are_forwarded_and_exit_codes_come_back()
    {
        HoldLock();
        var handler = ServeAsInstance();

        var added = await TimeArr("add", "work");
        var missing = await TimeArr("start", "--id", "999");
        var status = await TimeArr("status");

        Assert.Equal((0, "Added 'work' (id 2)."), (added.ExitCode, added.Out));
        Assert.Equal(1, missing.ExitCode);
        Assert.NotEqual("", missing.Err);
        Assert.Equal(0, status.ExitCode);
        Assert.EndsWith("default", status.Out, StringComparison.Ordinal);
        Assert.Contains(handler.Execute(new TimerCommand.List()).Value.Timers, t => t.Name == "work");
    }

    [Fact]
    public async Task Bad_command_line_is_rejected_before_forwarding()
    {
        HoldLock();
        var handler = ServeAsInstance();

        var run = await TimeArr("delete");

        Assert.Equal(2, run.ExitCode);
        Assert.Single(handler.Execute(new TimerCommand.List()).Value.Timers);
    }

    [Fact]
    public async Task Show_and_no_arguments_ask_the_instance_to_show_its_window()
    {
        HoldLock();
        ServeAsInstance();

        Assert.Equal(0, (await TimeArr("show")).ExitCode);
        Assert.Equal(0, (await TimeArr()).ExitCode);

        Assert.Equal(2, Volatile.Read(ref _shown));
    }

    [Fact]
    public async Task Many_clis_at_once_all_get_through()
    {
        HoldLock();
        var handler = ServeAsInstance();

        var runs = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => TimeArr("add", $"t{i}")));

        Assert.All(runs, r => Assert.Equal(0, r.ExitCode));
        Assert.Equal(11, handler.Execute(new TimerCommand.List()).Value.Timers.Count);
    }

    // --- startup races ---

    [Fact]
    public async Task Show_waits_for_an_instance_still_starting_up()
    {
        HoldLock();
        var cli = TimeArr("show");

        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
        ServeAsInstance();
        var run = await cli;

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(1, Volatile.Read(ref _shown));
    }

    [Fact]
    public async Task Show_gives_up_on_a_lock_holder_that_never_listens_after_about_five_seconds()
    {
        HoldLock();

        var run = await TimeArr("show");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("isn't answering", run.Err, StringComparison.Ordinal);
        Assert.InRange(run.Took, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(20));
    }

    // --- commands with no running instance ---

    [Fact]
    public async Task Commands_with_no_instance_run_against_the_database_and_exit()
    {
        var added = await TimeArr("add", "solo");
        var listed = await TimeArr("list");

        Assert.Equal((0, "Added 'solo' (id 2)."), (added.ExitCode, added.Out));
        Assert.Equal(0, listed.ExitCode);
        Assert.Contains("stopped", listed.Out, StringComparison.Ordinal);
        Assert.EndsWith("solo", listed.Out, StringComparison.Ordinal);
        Assert.False(File.Exists(SocketPath), "A command should not start listening like an instance.");
        using var nobodyOwnsIt = InstanceChannel.TryLock(DataFolder).Value;
    }

    [Fact]
    public async Task A_command_does_not_wait_for_a_lock_holder_that_isnt_listening()
    {
        HoldLock();

        var run = await TimeArr("add", "quick");

        Assert.Equal(0, run.ExitCode);
        Assert.True(run.Took < TimeSpan.FromSeconds(4), $"took {run.Took}");
        Assert.Contains(Handler().Execute(new TimerCommand.List()).Value.Timers, t => t.Name == "quick");
    }

    [Fact]
    public async Task A_failed_command_with_no_instance_is_exit_code_1()
    {
        var run = await TimeArr("start", "--id", "999");

        Assert.Equal(1, run.ExitCode);
        Assert.NotEqual("", run.Err);
    }

    [Fact]
    public async Task Bad_names_are_exit_code_2_and_are_never_forwarded()
    {
        HoldLock();
        var handler = ServeAsInstance();

        var dash = await TimeArr("add", "-x");
        var blank = await TimeArr("rename", "default", " ");

        Assert.Equal(2, dash.ExitCode);
        Assert.Equal(2, blank.ExitCode);
        Assert.Single(handler.Execute(new TimerCommand.List()).Value.Timers);
    }

    [Fact]
    public async Task Escape_sequences_in_an_unknown_command_do_not_reach_the_terminal()
    {
        var run = await TimeArr("\u001b[2J\u001b]0;pwned\u0007");

        Assert.Equal(2, run.ExitCode);
        Assert.DoesNotContain('\u001b', run.Err);
        Assert.DoesNotContain('\u0007', run.Err);
    }

    [Fact]
    public async Task A_live_instance_keeps_its_socket()
    {
        HoldLock();
        var handler = ServeAsInstance();
        var before = File.GetLastWriteTimeUtc(SocketPath);

        await TimeArr("list");
        await TimeArr("add", "x");

        Assert.Equal(before, File.GetLastWriteTimeUtc(SocketPath));
        Assert.Equal(2, handler.Execute(new TimerCommand.List()).Value.Timers.Count);
    }
}
