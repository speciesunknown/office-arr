using TimeArr.Commands;
using TimeArr.Core;
using TimeArr.Data;
using TimeArr.Tests.Commands;
using TimeArr.ViewModels;

namespace TimeArr.Tests.ViewModels;

/// <summary>Viewmodels against the real handler and a temp database. No Avalonia (tester owns the headless UI tests).</summary>
public sealed class MainViewModelTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "time-arr-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(T0);
    private string DbPath => Path.Combine(_root, "time-arr", "time-arr.db");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private TimerCommandHandler Handler() => new(new SqliteTimerStorage(DbPath), _clock);

    private MainViewModel ViewModel(ITimerCommands? commands = null) => new(commands ?? Handler(), _clock);

    [Fact]
    public void Starts_with_the_default_timer()
    {
        var vm = ViewModel();

        var timer = Assert.Single(vm.Timers);
        Assert.Equal("default", timer.Name);
        Assert.Equal("0:00:00", timer.ElapsedText);
        Assert.Equal("Start", timer.ToggleText);
        Assert.False(vm.IsEmpty);
        Assert.False(vm.HasMessage);
    }

    [Fact]
    public void Toggle_starts_and_pauses_and_the_clock_moves_on_tick()
    {
        var vm = ViewModel();
        var timer = vm.Timers[0];

        timer.ToggleCommand.Execute(null);
        _clock.Advance(TimeSpan.FromSeconds(65));
        vm.Tick();

        Assert.True(timer.IsRunning);
        Assert.Equal("Pause", timer.ToggleText);
        Assert.Equal("0:01:05", timer.ElapsedText);

        timer.ToggleCommand.Execute(null);
        _clock.Advance(TimeSpan.FromMinutes(10));
        vm.Tick();

        Assert.Equal("0:01:05", timer.ElapsedText);
    }

    [Fact]
    public void Add_appends_a_timer_and_clears_the_box()
    {
        var vm = ViewModel();
        vm.NewTimerName = "work";

        vm.AddCommand.Execute(null);

        Assert.Equal(["default", "work"], vm.Timers.Select(t => t.Name));
        Assert.Equal("", vm.NewTimerName);
    }

    [Fact]
    public void Failed_add_keeps_the_text_and_shows_why()
    {
        var vm = ViewModel();
        vm.NewTimerName = "DEFAULT";

        vm.AddCommand.Execute(null);

        Assert.Equal("DEFAULT", vm.NewTimerName);
        Assert.True(vm.HasMessage);
        Assert.Contains("already exists", vm.Message, StringComparison.Ordinal);

        vm.DismissMessageCommand.Execute(null);
        Assert.False(vm.HasMessage);
    }

    [Fact]
    public void Rename_and_delete()
    {
        var vm = ViewModel();
        var timer = vm.Timers[0];

        timer.BeginRenameCommand.Execute(null);
        Assert.True(timer.IsEditing);
        timer.EditName = "main";
        timer.CommitRenameCommand.Execute(null);

        Assert.False(timer.IsEditing);
        Assert.Equal("main", timer.Name);

        timer.DeleteCommand.Execute(null);

        Assert.Empty(vm.Timers);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public void Failed_rename_stays_in_edit_mode()
    {
        var vm = ViewModel();
        vm.NewTimerName = "work";
        vm.AddCommand.Execute(null);
        var timer = vm.Timers[0];

        timer.BeginRenameCommand.Execute(null);
        timer.EditName = "WORK";
        timer.CommitRenameCommand.Execute(null);

        Assert.True(timer.IsEditing);
        Assert.True(vm.HasMessage);
    }

    [Fact]
    public void Changes_from_elsewhere_show_up_on_the_next_refresh_and_rows_are_kept()
    {
        var handler = Handler();
        var vm = ViewModel(handler);
        var row = vm.Timers[0];

        // The CLI (in M4) goes through the same handler.
        handler.Execute(new TimerCommand.Start(new TimerRef.Default()));
        handler.Execute(new TimerCommand.Add("from cli"));
        for (var i = 0; i < MainViewModel.RefreshEveryTicks; i++)
            vm.Tick();

        Assert.Same(row, vm.Timers[0]);
        Assert.True(row.IsRunning);
        Assert.Equal("from cli", vm.Timers[1].Name);
    }

    [Fact]
    public void Storage_problem_is_shown_in_plain_words_and_clears_when_fixed()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        File.WriteAllText(DbPath, "this is not a database, just some text that is long enough to have a header....");

        var vm = ViewModel();

        Assert.True(vm.HasMessage);
        Assert.StartsWith("The file where time-arr keeps its timers isn't a time-arr database", vm.Message, StringComparison.Ordinal);
        Assert.True(vm.IsEmpty);

        File.Delete(DbPath);
        vm.Refresh();

        Assert.False(vm.HasMessage);
        Assert.Single(vm.Timers);
    }

    [Theory]
    [InlineData(0, "0:00:00")]
    [InlineData(59_999, "0:00:59")]
    [InlineData(3_600_000, "1:00:00")]
    [InlineData(86_399_000, "23:59:59")]
    [InlineData(86_400_000, "1d 00:00:00")]
    [InlineData(1_000L * 86_400_000 + 3_723_000, "1000d 01:02:03")]
    public void Elapsed_is_formatted_for_people(long ms, string expected)
    {
        Assert.Equal(expected, TimerViewModel.Format(TimeSpan.FromMilliseconds(ms)));
    }
}
