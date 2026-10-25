using TimeArr.Commands;
using TimeArr.Core;
using TimeArr.Data;
using TimeArr.Tests.Commands;
using TimeArr.ViewModels;

namespace TimeArr.Tests.ViewModels;

/// <summary>Viewmodels against the real handler and a temp database, without Avalonia, since tester owns the headless UI tests.</summary>
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
        Assert.True(vm.HasConfirmation);
        Assert.Single(vm.Timers);
        vm.ConfirmCommand.Execute(null);

        Assert.Empty(vm.Timers);
        Assert.True(vm.IsEmpty);
        Assert.True(vm.ShowEmptyHint);
    }

    // --- confirmations (sub plan 001 A) ---

    [Fact]
    public void Delete_asks_first_and_cancel_keeps_the_timer()
    {
        var vm = ViewModel();

        vm.Timers[0].DeleteCommand.Execute(null);

        Assert.Equal("Delete", vm.Confirmation!.ConfirmText);
        Assert.Contains("'default'", vm.Confirmation.Question, StringComparison.Ordinal);
        vm.CancelConfirmationCommand.Execute(null);
        Assert.False(vm.HasConfirmation);
        Assert.Single(vm.Timers);
    }

    [Fact]
    public void Reset_with_little_time_on_it_doesnt_ask()
    {
        var vm = ViewModel();
        var timer = vm.Timers[0];
        timer.ToggleCommand.Execute(null);
        _clock.Advance(TimeSpan.FromSeconds(30));

        timer.ResetCommand.Execute(null);

        Assert.False(vm.HasConfirmation);
        Assert.Equal("0:00:00", timer.ElapsedText);
    }

    [Fact]
    public void Reset_with_more_than_a_minute_asks_first()
    {
        var vm = ViewModel();
        var timer = vm.Timers[0];
        timer.ToggleCommand.Execute(null);
        _clock.Advance(TimeSpan.FromMinutes(5));

        timer.ResetCommand.Execute(null);

        Assert.True(vm.HasConfirmation);
        Assert.Contains("0:05:00", vm.Confirmation!.Question, StringComparison.Ordinal);
        vm.ConfirmCommand.Execute(null);
        Assert.Equal("0:00:00", timer.ElapsedText);
        Assert.True(timer.IsRunning);
    }

    // --- add box (sub plan 001 #5) ---

    [Fact]
    public void Add_is_disabled_while_the_box_is_blank()
    {
        var vm = ViewModel();
        var changes = 0;
        vm.AddCommand.CanExecuteChanged += (_, _) => changes++;

        Assert.False(vm.AddCommand.CanExecute(null));
        vm.NewTimerName = "work";
        Assert.True(vm.AddCommand.CanExecute(null));
        vm.NewTimerName = "  ";
        Assert.False(vm.AddCommand.CanExecute(null));
        Assert.Equal(2, changes);
    }

    // --- storage problems (sub plan 001 #2, 002 #5) ---

    [Fact]
    public async Task Rows_grey_out_and_freeze_while_the_database_is_unusable_and_come_back_after()
    {
        var vm = ViewModel();
        var timer = vm.Timers[0];
        timer.ToggleCommand.Execute(null);
        _clock.Advance(TimeSpan.FromSeconds(10));
        vm.Tick();
        Assert.Equal("0:00:10", timer.ElapsedText);

        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(DbPath)!))
            File.Delete(file);
        File.WriteAllText(DbPath, "this is not a database, just some text that is long enough to have a header....");
        await vm.Refresh();

        Assert.Equal(StorageStatus.Unavailable, vm.Storage);
        Assert.False(timer.IsEnabled);
        Assert.False(vm.ShowEmptyHint);
        _clock.Advance(TimeSpan.FromSeconds(10));
        vm.Tick();
        Assert.Equal("0:00:10", timer.ElapsedText);

        File.Delete(DbPath);
        for (var i = 0; i < MainViewModel.RefreshEveryTicksWhileUnavailable; i++)
            vm.Tick();

        Assert.Equal(StorageStatus.Available, vm.Storage);
        Assert.False(vm.HasMessage);
    }

    [Fact]
    public void No_timers_hint_is_not_shown_under_a_database_error()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        File.WriteAllText(DbPath, "this is not a database, just some text that is long enough to have a header....");

        var vm = ViewModel();

        Assert.True(vm.IsEmpty);
        Assert.False(vm.ShowEmptyHint);
    }

    // --- running off the UI thread (sub plan 001 #1) ---

    [Fact]
    public async Task Commands_run_on_the_queue_thread_not_the_caller()
    {
        var caller = Environment.CurrentManagedThreadId;
        var commands = new BreakableCommands(Handler());
        using var queue = new CommandQueue(commands);
        var vm = new MainViewModel(queue.Join().Send, _clock);

        await vm.Refresh();

        Assert.Single(vm.Timers);
        Assert.DoesNotContain(caller, commands.Threads);
    }

    [Fact]
    public async Task A_refresh_asked_for_while_one_runs_happens_once_afterwards()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var handler = Handler();
        var vm = new MainViewModel(async command =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return handler.Execute(command);
        }, _clock);

        var first = vm.Refresh();
        var second = vm.Refresh();
        var third = vm.Refresh();
        gate.SetResult();
        await Task.WhenAll(first, second, third);

        // All three calls join the constructor's read, which then runs once more for them.
        Assert.Equal(2, calls);
    }

    // --- rename in mini mode (sub plan 003 #2) ---

    [Fact]
    public void Rename_cant_be_started_in_mini_mode()
    {
        var vm = ViewModel();
        var timer = vm.Timers[0];

        vm.Mode = WindowMode.Mini;

        Assert.False(timer.BeginRenameCommand.CanExecute(null));
        timer.BeginRenameCommand.Execute(null);
        Assert.False(timer.IsEditing);
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

    // --- mini mode ---

    private MainViewModel WithTimers(TimerCommandHandler handler, params string[] names)
    {
        foreach (var name in names)
            handler.Execute(new TimerCommand.Add(name));
        return ViewModel(handler);
    }

    private static string[] Shown(MainViewModel vm) => vm.Timers.Where(t => t.IsShown).Select(t => t.Name).ToArray();

    private static void RefreshNow(MainViewModel vm)
    {
        for (var i = 0; i < MainViewModel.RefreshEveryTicks; i++)
            vm.Tick();
    }

    [Fact]
    public void Mini_mode_shows_only_running_timers_with_pause_only()
    {
        var handler = Handler();
        var vm = WithTimers(handler, "work", "break");
        vm.Timers.Single(t => t.Name == "work").ToggleCommand.Execute(null);

        vm.ToggleMiniModeCommand.Execute(null);

        Assert.True(vm.IsMiniMode);
        Assert.Equal(["work"], Shown(vm));
        var work = vm.Timers.Single(t => t.Name == "work");
        Assert.True(work.IsMini);
        Assert.Equal("Pause", work.ToggleText);
        Assert.False(vm.IsMiniEmpty);
    }

    [Fact]
    public void Paused_timers_show_in_mini_mode_with_resume_and_stopped_ones_dont()
    {
        var handler = Handler();
        var vm = WithTimers(handler, "work", "break", "idle");
        vm.Timers.Single(t => t.Name == "work").ToggleCommand.Execute(null);
        vm.Timers.Single(t => t.Name == "break").ToggleCommand.Execute(null);
        _clock.Advance(TimeSpan.FromMinutes(5));
        vm.Timers.Single(t => t.Name == "break").ToggleCommand.Execute(null);

        vm.Mode = WindowMode.Mini;

        Assert.Equal(["work", "break"], Shown(vm));
        var paused = vm.Timers.Single(t => t.Name == "break");
        Assert.True(paused.IsPaused);
        Assert.Equal("Resume", paused.ToggleText);

        paused.ToggleCommand.Execute(null);
        Assert.True(paused.IsRunning);
        Assert.Equal("Pause", paused.ToggleText);
    }

    [Fact]
    public void A_timer_reset_in_mini_mode_is_stopped_and_drops_out()
    {
        var handler = Handler();
        var vm = WithTimers(handler, "work");
        var work = vm.Timers.Single(t => t.Name == "work");
        work.ToggleCommand.Execute(null);
        _clock.Advance(TimeSpan.FromMinutes(1));
        work.ToggleCommand.Execute(null);
        vm.Mode = WindowMode.Mini;
        Assert.True(work.IsShown);

        handler.Execute(new TimerCommand.Reset(new TimerRef.ByName("work")));
        RefreshNow(vm);

        Assert.False(work.IsShown);
        Assert.Equal("Start", work.ToggleText);
        Assert.True(vm.IsMiniEmpty);
    }

    [Fact]
    public void A_timer_started_elsewhere_appears_in_mini_mode()
    {
        var handler = Handler();
        var vm = WithTimers(handler, "work");
        vm.Mode = WindowMode.Mini;
        Assert.True(vm.IsMiniEmpty);
        Assert.Empty(Shown(vm));

        handler.Execute(new TimerCommand.Start(new TimerRef.ByName("work")));
        RefreshNow(vm);

        Assert.Equal(["work"], Shown(vm));
        Assert.False(vm.IsMiniEmpty);
    }

    [Fact]
    public void Leaving_mini_mode_shows_everything()
    {
        var handler = Handler();
        var vm = WithTimers(handler, "work");
        vm.Timers.Single(t => t.Name == "work").ToggleCommand.Execute(null);
        vm.Mode = WindowMode.Mini;
        Assert.Equal(["work"], Shown(vm));

        vm.ToggleMiniModeCommand.Execute(null);

        Assert.False(vm.IsMiniMode);
        Assert.Equal(["default", "work"], Shown(vm));
        Assert.All(vm.Timers, t => Assert.False(t.IsMini));
        Assert.False(vm.IsMiniEmpty);
    }

    [Fact]
    public void Entering_mini_mode_ends_a_rename()
    {
        var vm = ViewModel();
        var timer = vm.Timers[0];
        timer.BeginRenameCommand.Execute(null);

        vm.Mode = WindowMode.Mini;

        Assert.False(timer.IsEditing);
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

    // --- the add dialog ---

    [Fact]
    public void A_successful_add_closes_the_dialog_and_clears_the_name()
    {
        var vm = ViewModel();
        vm.OpenAddDialogCommand.Execute(null);
        vm.NewTimerName = "work";

        vm.AddCommand.Execute(null);

        Assert.Equal(AddDialogState.Closed, vm.AddDialog);
        Assert.Equal("", vm.NewTimerName);
        Assert.Equal(["default", "work"], vm.Timers.Select(t => t.Name));
    }

    [Fact]
    public void A_failed_add_keeps_the_dialog_open_with_the_name_and_the_reason()
    {
        var vm = ViewModel();
        vm.OpenAddDialogCommand.Execute(null);
        vm.NewTimerName = "DEFAULT";

        vm.AddCommand.Execute(null);

        Assert.Equal(AddDialogState.Open, vm.AddDialog);
        Assert.Equal("DEFAULT", vm.NewTimerName);
        Assert.True(vm.HasMessage);
    }

    [Fact]
    public void Cancel_closes_the_dialog_and_forgets_the_name()
    {
        var vm = ViewModel();
        vm.OpenAddDialogCommand.Execute(null);
        vm.NewTimerName = "half typed";

        vm.CancelAddCommand.Execute(null);

        Assert.Equal(AddDialogState.Closed, vm.AddDialog);
        Assert.Equal("", vm.NewTimerName);
        Assert.Single(vm.Timers);
    }

    [Fact]
    public void Mini_mode_closes_the_dialog_and_cant_open_it()
    {
        var vm = ViewModel();
        vm.OpenAddDialogCommand.Execute(null);

        vm.Mode = WindowMode.Mini;

        Assert.Equal(AddDialogState.Closed, vm.AddDialog);
        Assert.False(vm.OpenAddDialogCommand.CanExecute(null));
    }

    // --- bugs in command work (sub plan 004 #1) ---

    [Fact]
    public void A_bug_in_a_button_command_is_not_swallowed()
    {
        var commands = new BreakableCommands(Handler());
        var vm = ViewModel(commands);
        commands.Broken = true;

        Assert.Throws<InvalidOperationException>(() => vm.Timers[0].ToggleCommand.Execute(null));
        Assert.Throws<InvalidOperationException>(() => vm.Timers[0].ResetCommand.Execute(null));
        vm.NewTimerName = "work";
        Assert.Throws<InvalidOperationException>(() => vm.AddCommand.Execute(null));
    }

    [Fact]
    public void A_bug_in_the_refresh_is_not_swallowed()
    {
        var commands = new BreakableCommands(Handler());
        var vm = ViewModel(commands);
        commands.Broken = true;

        Assert.Throws<InvalidOperationException>(() =>
        {
            for (var i = 0; i < MainViewModel.RefreshEveryTicks; i++)
                vm.Tick();
        });
    }

    [Fact]
    public void A_bug_in_a_confirmed_delete_is_not_swallowed()
    {
        var commands = new BreakableCommands(Handler());
        var vm = ViewModel(commands);
        vm.Timers[0].DeleteCommand.Execute(null);
        commands.Broken = true;

        Assert.Throws<InvalidOperationException>(() => vm.ConfirmCommand.Execute(null));
    }

    /// <summary>The real handler, noting the threads it runs on, until a test makes every command hit a bug.</summary>
    private sealed class BreakableCommands(ITimerCommands inner) : ITimerCommands
    {
        private readonly List<int> _threads = [];

        public bool Broken { get; set; }

        public IReadOnlyList<int> Threads
        {
            get
            {
                lock (_threads)
                    return [.. _threads];
            }
        }

        public Error? StorageProblem => inner.StorageProblem;

        public Result<CommandReply> Execute(TimerCommand command)
        {
            lock (_threads)
                _threads.Add(Environment.CurrentManagedThreadId);
            return Broken ? throw new InvalidOperationException("Test bug.") : inner.Execute(command);
        }
    }
}
