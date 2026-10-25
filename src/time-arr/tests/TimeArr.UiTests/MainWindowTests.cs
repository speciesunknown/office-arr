using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TimeArr.App;
using TimeArr.Commands;
using TimeArr.Core;
using TimeArr.Data;
using TimeArr.ViewModels;

namespace TimeArr.UiTests;

/// <summary>
/// The real <see cref="MainWindow"/> on the headless platform, driven by simulated mouse and
/// keyboard input, over the real handler, a temp database and a <see cref="FakeClock"/>.
/// </summary>
public sealed class MainWindowTests : IDisposable
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

    /// <summary>An open window over a fresh viewmodel, closed (which stops its ticker) on Dispose.</summary>
    private sealed class Ui : IDisposable
    {
        public Ui(MainViewModel vm)
        {
            Vm = vm;
            Window = new MainWindow { DataContext = vm };
            Window.Show();
            Pump();
        }

        public MainViewModel Vm { get; }
        public MainWindow Window { get; }

        public void Dispose() => Window.Close();

        public void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public T Named<T>(string name) where T : Control =>
            Window.FindControl<T>(name) ?? throw new InvalidOperationException($"No control named {name}.");

        /// <summary>The controls with this class, top to bottom (one per timer row).</summary>
        public List<T> InRows<T>(string cssClass) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().Where(c => c.Classes.Contains(cssClass)).ToList();

        public List<string> Names() => InRows<TextBlock>("name").Select(t => t.Text ?? "").ToList();

        /// <summary>The names of the rows actually on screen, leaving out rows hidden in mini mode.</summary>
        public List<string> VisibleNames() =>
            InRows<TextBlock>("name").Where(t => t.FindAncestorOfType<Grid>()!.IsVisible).Select(t => t.Text ?? "").ToList();

        public void Click(Control control, MouseButton button = MouseButton.Left) =>
            ClickAt(control, new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), button);

        /// <summary>Clicks at <paramref name="local"/>, a point in <paramref name="control"/>'s own coordinates.</summary>
        public void ClickAt(Control control, Point local, MouseButton button = MouseButton.Left)
        {
            Assert.True(control.IsEffectivelyVisible, $"{control} is not visible.");
            var at = ToWindow(control, local);
            Window.MouseMove(at);
            Window.MouseDown(at, button);
            Window.MouseUp(at, button);
            Pump();
        }

        /// <summary>Double-clicks a corner of the window, which is empty space in both modes.</summary>
        public void DoubleClickEmptySpace()
        {
            var at = new Point(2, 2);
            Window.MouseMove(at);
            Window.MouseDown(at, MouseButton.Left);
            Window.MouseUp(at, MouseButton.Left);
            Window.MouseDown(at, MouseButton.Left);
            Window.MouseUp(at, MouseButton.Left);
            Pump();
        }

        public Point ToWindow(Control control, Point local) =>
            control.TranslatePoint(local, Window) ?? throw new InvalidOperationException("Control is not in the window.");

        public void Type(TextBox box, string text)
        {
            box.Focus();
            box.SelectAll();
            Pump();
            Window.KeyTextInput(text);
            Pump();
        }

        public void Press(Key key, PhysicalKey physical)
        {
            Window.KeyPress(key, RawInputModifiers.None, physical, null);
            Window.KeyRelease(key, RawInputModifiers.None, physical, null);
            Pump();
        }

        public void Tick(int times = 1)
        {
            for (var i = 0; i < times; i++)
                Vm.Tick();
            Pump();
        }
    }

    private Ui Open(ITimerCommands? commands = null) => new(new MainViewModel(commands ?? Handler(), _clock));

    private static string Text(Control c) => c switch
    {
        Button b => b.Content?.ToString() ?? "",
        TextBlock t => t.Text ?? "",
        TextBox t => t.Text ?? "",
        _ => throw new ArgumentException(c.GetType().Name),
    };

    // --- the timer row ---

    [AvaloniaFact]
    public void Window_shows_the_default_timer_at_zero()
    {
        using var ui = Open();

        Assert.Equal(["default"], ui.Names());
        Assert.Equal("0:00:00", Text(ui.InRows<TextBlock>("elapsed")[0]));
        Assert.Equal("Start", Text(ui.InRows<Button>("toggle")[0]));
        Assert.False(ui.Named<TextBlock>("EmptyText").IsVisible);
        Assert.False(ui.Named<Border>("MessageBar").IsVisible);
        Assert.False(ui.InRows<TextBox>("rename")[0].IsVisible);
    }

    [AvaloniaFact]
    public void Start_button_starts_the_timer_and_ticks_move_the_clock()
    {
        using var ui = Open();

        ui.Click(ui.InRows<Button>("toggle")[0]);
        _clock.Advance(TimeSpan.FromSeconds(65));
        ui.Tick();

        Assert.Equal("Pause", Text(ui.InRows<Button>("toggle")[0]));
        Assert.Equal("0:01:05", Text(ui.InRows<TextBlock>("elapsed")[0]));
    }

    [AvaloniaFact]
    public void Pause_button_stops_the_clock_and_offers_resume()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("toggle")[0]);
        _clock.Advance(TimeSpan.FromSeconds(10));

        ui.Click(ui.InRows<Button>("toggle")[0]);
        _clock.Advance(TimeSpan.FromHours(1));
        ui.Tick(MainViewModel.RefreshEveryTicks + 1);

        Assert.Equal("Resume", Text(ui.InRows<Button>("toggle")[0]));
        Assert.Equal("0:00:10", Text(ui.InRows<TextBlock>("elapsed")[0]));
    }

    [AvaloniaFact]
    public void Resume_continues_from_the_banked_time()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("toggle")[0]);
        _clock.Advance(TimeSpan.FromSeconds(10));
        ui.Click(ui.InRows<Button>("toggle")[0]);
        _clock.Advance(TimeSpan.FromHours(1));

        ui.Click(ui.InRows<Button>("toggle")[0]);
        _clock.Advance(TimeSpan.FromSeconds(5));
        ui.Tick();

        Assert.Equal("Pause", Text(ui.InRows<Button>("toggle")[0]));
        Assert.Equal("0:00:15", Text(ui.InRows<TextBlock>("elapsed")[0]));
    }

    [AvaloniaFact]
    public void Reset_under_a_minute_happens_without_asking()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("toggle")[0]);
        _clock.Advance(MainViewModel.ResetConfirmAbove);

        ui.Click(ui.InRows<Button>("reset")[0]);

        Assert.False(ui.Named<Border>("ConfirmBar").IsVisible);
        Assert.Equal("0:00:00", Text(ui.InRows<TextBlock>("elapsed")[0]));
        Assert.Equal("Pause", Text(ui.InRows<Button>("toggle")[0]));
    }

    [AvaloniaFact]
    public void Reset_over_a_minute_asks_first_and_confirming_zeroes_a_running_timer()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("toggle")[0]);
        _clock.Advance(TimeSpan.FromMinutes(5));

        ui.Click(ui.InRows<Button>("reset")[0]);

        Assert.True(ui.Named<Border>("ConfirmBar").IsVisible);
        Assert.Contains("default", Text(ui.Named<TextBlock>("ConfirmText")), StringComparison.Ordinal);
        Assert.Contains("0:05:00", Text(ui.Named<TextBlock>("ConfirmText")), StringComparison.Ordinal);
        Assert.Equal("Reset", Text(ui.Named<Button>("ConfirmButton")));

        ui.Click(ui.Named<Button>("ConfirmButton"));
        _clock.Advance(TimeSpan.FromSeconds(3));
        ui.Tick();

        Assert.False(ui.Named<Border>("ConfirmBar").IsVisible);
        Assert.Equal("0:00:03", Text(ui.InRows<TextBlock>("elapsed")[0]));
        Assert.Equal("Pause", Text(ui.InRows<Button>("toggle")[0]));
    }

    [AvaloniaFact]
    public void Confirming_a_reset_of_a_paused_timer_stops_it_at_zero()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("toggle")[0]);
        _clock.Advance(TimeSpan.FromMinutes(5));
        ui.Click(ui.InRows<Button>("toggle")[0]);

        ui.Click(ui.InRows<Button>("reset")[0]);
        ui.Click(ui.Named<Button>("ConfirmButton"));

        Assert.Equal("0:00:00", Text(ui.InRows<TextBlock>("elapsed")[0]));
        Assert.Equal("Start", Text(ui.InRows<Button>("toggle")[0]));
    }

    [AvaloniaFact]
    public void Cancelling_a_reset_keeps_the_time()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("toggle")[0]);
        _clock.Advance(TimeSpan.FromMinutes(5));

        ui.Click(ui.InRows<Button>("reset")[0]);
        ui.Click(ui.Named<Button>("CancelConfirmButton"));
        ui.Tick();

        Assert.False(ui.Named<Border>("ConfirmBar").IsVisible);
        Assert.Equal("0:05:00", Text(ui.InRows<TextBlock>("elapsed")[0]));
    }

    [AvaloniaFact]
    public void Delete_asks_first_and_confirming_removes_the_row()
    {
        using var ui = Open();

        ui.Click(ui.InRows<Button>("delete")[0]);

        Assert.True(ui.Named<Border>("ConfirmBar").IsVisible);
        Assert.Contains("default", Text(ui.Named<TextBlock>("ConfirmText")), StringComparison.Ordinal);
        Assert.Equal("Delete", Text(ui.Named<Button>("ConfirmButton")));
        Assert.Equal(["default"], ui.Names());

        ui.Click(ui.Named<Button>("ConfirmButton"));

        Assert.Empty(ui.Names());
        Assert.True(ui.Named<TextBlock>("EmptyText").IsVisible);
        Assert.Empty(Handler().Execute(new TimerCommand.List()).Value.Timers);
    }

    [AvaloniaFact]
    public void Delete_of_a_stopped_timer_at_zero_still_asks()
    {
        using var ui = Open();

        ui.Click(ui.InRows<Button>("delete")[0]);

        Assert.True(ui.Named<Border>("ConfirmBar").IsVisible);
    }

    [AvaloniaFact]
    public void Cancelling_a_delete_keeps_the_timer()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("delete")[0]);

        ui.Click(ui.Named<Button>("CancelConfirmButton"));

        Assert.False(ui.Named<Border>("ConfirmBar").IsVisible);
        Assert.Equal(["default"], ui.Names());
        Assert.Single(Handler().Execute(new TimerCommand.List()).Value.Timers);
    }

    [AvaloniaFact]
    public void Escape_cancels_a_pending_delete()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("delete")[0]);

        ui.Press(Key.Escape, PhysicalKey.Escape);

        Assert.False(ui.Named<Border>("ConfirmBar").IsVisible);
        Assert.Single(Handler().Execute(new TimerCommand.List()).Value.Timers);
    }

    [AvaloniaFact]
    public void Confirm_deletes_the_timer_that_was_asked_about_even_if_others_changed()
    {
        using var ui = Open();
        ui.Click(ui.Named<Button>("OpenAddButton"));
        ui.Type(ui.Named<TextBox>("NewTimerName"), "second");
        ui.Click(ui.Named<Button>("AddButton"));
        ui.Click(ui.InRows<Button>("delete")[1]);

        // Another client deletes the first timer while the question is up, so row indexes shift.
        Handler().Execute(new TimerCommand.Delete(new TimerRef.Default()));
        ui.Tick(MainViewModel.RefreshEveryTicks);
        ui.Click(ui.Named<Button>("ConfirmButton"));

        Assert.Empty(Handler().Execute(new TimerCommand.List()).Value.Timers);
    }

    [AvaloniaFact]
    public void Buttons_act_on_their_own_row()
    {
        using var ui = Open();
        ui.Click(ui.Named<Button>("OpenAddButton"));
        ui.Type(ui.Named<TextBox>("NewTimerName"), "second");
        ui.Click(ui.Named<Button>("AddButton"));

        ui.Click(ui.InRows<Button>("toggle")[1]);

        Assert.Equal(["Start", "Pause"], ui.InRows<Button>("toggle").Select(Text));
    }

    [AvaloniaFact]
    public void Elapsed_over_a_day_shows_days()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("toggle")[0]);

        _clock.Advance(new TimeSpan(1, 2, 3, 4));
        ui.Tick();

        Assert.Equal("1d 02:03:04", Text(ui.InRows<TextBlock>("elapsed")[0]));
    }

    [AvaloniaFact]
    public void The_windows_own_ticker_moves_the_clock()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("toggle")[0]);
        _clock.Advance(TimeSpan.FromSeconds(42));

        // Let real time pass so the window's DispatcherTimer (250 ms) fires; no manual Tick().
        var elapsed = ui.InRows<TextBlock>("elapsed")[0];
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Text(elapsed) != "0:00:42" && DateTime.UtcNow < deadline)
        {
            using var slice = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            Dispatcher.UIThread.MainLoop(slice.Token);
            ui.Pump();
        }

        Assert.Equal("0:00:42", Text(elapsed));
    }

    // --- adding ---

    [AvaloniaFact]
    public void Typing_a_name_and_pressing_enter_adds_a_row_and_clears_the_box()
    {
        using var ui = Open();
        ui.Click(ui.Named<Button>("OpenAddButton"));
        var box = ui.Named<TextBox>("NewTimerName");

        ui.Type(box, "Deep work");
        ui.Press(Key.Enter, PhysicalKey.Enter);

        Assert.Equal(["default", "Deep work"], ui.Names());
        Assert.Equal("", Text(box));
    }

    [AvaloniaFact]
    public void Add_button_adds_a_row()
    {
        using var ui = Open();

        ui.Click(ui.Named<Button>("OpenAddButton"));
        ui.Type(ui.Named<TextBox>("NewTimerName"), "reading");
        ui.Click(ui.Named<Button>("AddButton"));

        Assert.Equal(["default", "reading"], ui.Names());
        Assert.Equal("reading", Handler().Execute(new TimerCommand.Get(new TimerRef.ByName("reading"))).Value.Timers[0].Name);
    }

    [AvaloniaFact]
    public void Add_is_disabled_while_the_box_is_blank()
    {
        using var ui = Open();
        ui.Click(ui.Named<Button>("OpenAddButton"));
        var add = ui.Named<Button>("AddButton");
        var box = ui.Named<TextBox>("NewTimerName");

        Assert.False(add.IsEffectivelyEnabled);
        ui.Type(box, "   ");
        Assert.False(add.IsEffectivelyEnabled);
        ui.Type(box, "x");
        Assert.True(add.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Enter_in_a_blank_box_does_nothing_and_shows_no_error()
    {
        using var ui = Open();

        ui.Click(ui.Named<Button>("OpenAddButton"));
        ui.Type(ui.Named<TextBox>("NewTimerName"), "  ");
        ui.Press(Key.Enter, PhysicalKey.Enter);

        Assert.Equal(["default"], ui.Names());
        Assert.False(ui.Named<Border>("MessageBar").IsVisible);
    }

    [AvaloniaFact]
    public void An_error_shows_the_message_bar_and_dismiss_hides_it()
    {
        using var ui = Open();
        ShowAnError(ui);

        var bar = ui.Named<Border>("MessageBar");
        Assert.True(bar.IsVisible);

        ui.Click(bar.GetVisualDescendants().OfType<Button>().Single());

        Assert.False(bar.IsVisible);
    }

    // A failed rename puts an error in the message bar, since Add's errors now stay in the add dialog (sub plan 009 #10).
    private static void ShowAnError(Ui ui)
    {
        var timer = ui.Vm.Timers[0];
        timer.BeginRenameCommand.Execute(null);
        timer.EditName = "-flag";
        // Left in edit mode, since cancelling the rename would take its error down (sub plan 010).
        timer.CommitRenameCommand.Execute(null);
        ui.Pump();
    }

    [AvaloniaFact]
    public void Duplicate_name_shows_why_and_keeps_the_typed_text()
    {
        using var ui = Open();
        ui.Click(ui.Named<Button>("OpenAddButton"));
        var box = ui.Named<TextBox>("NewTimerName");

        ui.Type(box, "DEFAULT");
        ui.Press(Key.Enter, PhysicalKey.Enter);

        Assert.Equal(["default"], ui.Names());
        Assert.True(ui.Named<TextBlock>("AddError").IsEffectivelyVisible);
        Assert.Contains("already exists", Text(ui.Named<TextBlock>("AddError")), StringComparison.Ordinal);
        Assert.Equal("DEFAULT", Text(box));
    }

    [AvaloniaFact]
    public void Invalid_name_is_explained()
    {
        using var ui = Open();

        ui.Click(ui.Named<Button>("OpenAddButton"));
        ui.Type(ui.Named<TextBox>("NewTimerName"), "-flag");
        ui.Click(ui.Named<Button>("AddButton"));

        Assert.Contains("'-'", Text(ui.Named<TextBlock>("AddError")), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void A_successful_command_clears_an_earlier_error()
    {
        using var ui = Open();
        ShowAnError(ui);
        Assert.True(ui.Named<Border>("MessageBar").IsVisible);

        ui.Click(ui.InRows<Button>("toggle")[0]);

        Assert.False(ui.Named<Border>("MessageBar").IsVisible);
    }

    [AvaloniaFact]
    public void An_error_message_survives_refresh_ticks()
    {
        using var ui = Open();
        ShowAnError(ui);

        ui.Tick(MainViewModel.RefreshEveryTicks * 2);

        Assert.True(ui.Named<Border>("MessageBar").IsVisible);
    }

    // --- rename ---

    private static Panel NameArea(Ui ui, int row) => ui.InRows<Panel>("nameArea")[row];

    /// <summary>Right-clicks near the right edge of a row's name area (well past a short name) and picks Rename.</summary>
    private static void BeginRename(Ui ui, int row)
    {
        var area = NameArea(ui, row);
        ui.ClickAt(area, new Point(area.Bounds.Width - 4, area.Bounds.Height / 2), MouseButton.Right);

        var menu = area.ContextMenu!;
        Assert.True(menu.IsOpen, "Right-click did not open the context menu.");
        var rename = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Rename"));
        rename.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        ui.Pump();
    }

    [AvaloniaFact]
    public void Right_click_on_the_empty_part_of_the_name_cell_opens_the_menu()
    {
        using var ui = Open();
        var area = NameArea(ui, 0);
        var name = ui.InRows<TextBlock>("name")[0];
        Assert.True(area.Bounds.Width - 4 > name.DesiredSize.Width, "The click point should be past the text.");

        ui.ClickAt(area, new Point(area.Bounds.Width - 4, area.Bounds.Height / 2), MouseButton.Right);

        Assert.True(area.ContextMenu!.IsOpen);
        area.ContextMenu.Close();
    }

    [AvaloniaFact]
    public void Double_click_on_the_name_area_starts_a_rename()
    {
        using var ui = Open();
        var area = NameArea(ui, 0);
        var at = ui.ToWindow(area, new Point(area.Bounds.Width - 4, area.Bounds.Height / 2));

        ui.Window.MouseMove(at);
        ui.Window.MouseDown(at, MouseButton.Left);
        ui.Window.MouseUp(at, MouseButton.Left);
        ui.Window.MouseDown(at, MouseButton.Left);
        ui.Window.MouseUp(at, MouseButton.Left);
        ui.Pump();

        Assert.True(ui.InRows<TextBox>("rename")[0].IsVisible);
    }

    [AvaloniaFact]
    public void The_rename_box_gets_focus_with_the_name_selected_so_typing_replaces_it()
    {
        using var ui = Open();

        BeginRename(ui, 0);
        var box = ui.InRows<TextBox>("rename")[0];

        Assert.True(box.IsFocused);
        Assert.Equal("default", box.SelectedText);

        // No Focus() or SelectAll() here, because the window must have done both.
        ui.Window.KeyTextInput("main");
        ui.Press(Key.Enter, PhysicalKey.Enter);

        Assert.Equal(["main"], ui.Names());
    }

    [AvaloniaFact]
    public void Rename_from_the_context_menu_shows_the_box_with_the_current_name()
    {
        using var ui = Open();

        BeginRename(ui, 0);

        var box = ui.InRows<TextBox>("rename")[0];
        Assert.True(box.IsVisible);
        Assert.False(ui.InRows<TextBlock>("name")[0].IsVisible);
        Assert.Equal("default", Text(box));
    }

    [AvaloniaFact]
    public void Enter_commits_the_rename()
    {
        using var ui = Open();
        BeginRename(ui, 0);

        ui.Type(ui.InRows<TextBox>("rename")[0], "main");
        ui.Press(Key.Enter, PhysicalKey.Enter);

        Assert.Equal(["main"], ui.Names());
        Assert.False(ui.InRows<TextBox>("rename")[0].IsVisible);
        Assert.Equal("main", Handler().Execute(new TimerCommand.List()).Value.Timers[0].Name);
    }

    [AvaloniaFact]
    public void Escape_cancels_the_rename()
    {
        using var ui = Open();
        BeginRename(ui, 0);

        ui.Type(ui.InRows<TextBox>("rename")[0], "never saved");
        ui.Press(Key.Escape, PhysicalKey.Escape);

        Assert.Equal(["default"], ui.Names());
        Assert.False(ui.InRows<TextBox>("rename")[0].IsVisible);
        Assert.Equal("default", Handler().Execute(new TimerCommand.List()).Value.Timers[0].Name);
    }

    [AvaloniaFact]
    public void Renaming_again_after_escape_starts_from_the_current_name()
    {
        using var ui = Open();
        BeginRename(ui, 0);
        ui.Type(ui.InRows<TextBox>("rename")[0], "abandoned");
        ui.Press(Key.Escape, PhysicalKey.Escape);

        BeginRename(ui, 0);

        Assert.Equal("default", Text(ui.InRows<TextBox>("rename")[0]));
    }

    [AvaloniaFact]
    public void Rename_to_a_taken_name_explains_and_stays_in_edit_mode()
    {
        using var ui = Open();
        ui.Click(ui.Named<Button>("OpenAddButton"));
        ui.Type(ui.Named<TextBox>("NewTimerName"), "other");
        ui.Click(ui.Named<Button>("AddButton"));
        BeginRename(ui, 1);

        ui.Type(ui.InRows<TextBox>("rename")[1], "Default");
        ui.Press(Key.Enter, PhysicalKey.Enter);

        Assert.True(ui.InRows<TextBox>("rename")[1].IsVisible);
        Assert.Equal("Default", Text(ui.InRows<TextBox>("rename")[1]));
        Assert.Contains("already exists", Text(ui.Named<TextBlock>("MessageText")), StringComparison.Ordinal);
        Assert.Equal(["default", "other"], ui.Names());
    }

    [AvaloniaFact]
    public void An_edit_in_progress_survives_refresh_ticks()
    {
        using var ui = Open();
        BeginRename(ui, 0);
        ui.Type(ui.InRows<TextBox>("rename")[0], "half typed");

        ui.Tick(MainViewModel.RefreshEveryTicks * 2);

        Assert.True(ui.InRows<TextBox>("rename")[0].IsVisible);
        Assert.Equal("half typed", Text(ui.InRows<TextBox>("rename")[0]));
    }

    // --- changes made elsewhere (the CLI) ---

    [AvaloniaFact]
    public void Changes_from_another_client_show_up_within_a_refresh()
    {
        using var ui = Open();
        var cli = Handler();

        cli.Execute(new TimerCommand.Add("from cli"));
        cli.Execute(new TimerCommand.Rename(new TimerRef.Default(), "renamed"));
        cli.Execute(new TimerCommand.Start(new TimerRef.ByName("from cli")));
        ui.Tick(MainViewModel.RefreshEveryTicks);

        Assert.Equal(["renamed", "from cli"], ui.Names());
        Assert.Equal(["Start", "Pause"], ui.InRows<Button>("toggle").Select(Text));
    }

    [AvaloniaFact]
    public void A_forwarded_cli_command_shows_up_in_the_window_within_a_second()
    {
        using var ui = Open();

        // What the IPC server runs for `time-arr add "from cli"` and `time-arr start "from cli"`.
        var cli = Handler();
        Assert.Equal(0, TimeArr.App.Ipc.IpcServer.Handle(["add", "from cli"], cli, () => { }).ExitCode);
        Assert.Equal(0, TimeArr.App.Ipc.IpcServer.Handle(["start", "from cli"], cli, () => { }).ExitCode);
        ui.Tick(MainViewModel.RefreshEveryTicks); // 4 ticks at 250 ms: one second

        Assert.Equal(["default", "from cli"], ui.Names());
        Assert.Equal("Pause", Text(ui.InRows<Button>("toggle")[1]));
    }

    [AvaloniaFact]
    public void Timer_deleted_elsewhere_disappears()
    {
        using var ui = Open();

        Handler().Execute(new TimerCommand.Delete(new TimerRef.Default()));
        ui.Tick(MainViewModel.RefreshEveryTicks);

        Assert.Empty(ui.Names());
        Assert.True(ui.Named<TextBlock>("EmptyText").IsVisible);
    }

    // --- storage problems ---

    private void PutTextFileAtDbPath()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        File.WriteAllText(DbPath, "this is not a database, just some text that is long enough to have a header....");
    }

    [AvaloniaFact]
    public void Foreign_file_at_the_db_path_shows_the_problem()
    {
        PutTextFileAtDbPath();

        using var ui = Open();

        Assert.True(ui.Named<Border>("MessageBar").IsVisible);
        Assert.Contains("isn't a time-arr database", Text(ui.Named<TextBlock>("MessageText")), StringComparison.Ordinal);
        Assert.Empty(ui.Names());
    }

    [AvaloniaFact]
    public void Empty_text_is_hidden_while_storage_is_broken()
    {
        PutTextFileAtDbPath();

        using var ui = Open();

        Assert.False(ui.Named<TextBlock>("EmptyText").IsVisible);
        Assert.Equal(StorageStatus.Unavailable, ui.Vm.Storage);
    }

    [AvaloniaFact]
    public void Rows_are_disabled_and_clocks_freeze_while_storage_is_broken()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("toggle")[0]);
        _clock.Advance(TimeSpan.FromSeconds(10));
        ui.Tick();
        BreakDatabase();

        ui.Click(ui.InRows<Button>("toggle")[0]);
        _clock.Advance(TimeSpan.FromMinutes(5));
        ui.Tick(3);

        Assert.True(ui.Named<Border>("MessageBar").IsVisible);
        Assert.False(ui.InRows<Button>("toggle")[0].IsEffectivelyEnabled);
        Assert.False(ui.InRows<Button>("delete")[0].IsEffectivelyEnabled);
        Assert.Equal("0:00:10", Text(ui.InRows<TextBlock>("elapsed")[0]));
    }

    [AvaloniaFact]
    public void While_storage_is_broken_the_refresh_slows_down()
    {
        PutTextFileAtDbPath();
        using var ui = Open();
        File.Delete(DbPath);

        ui.Tick(MainViewModel.RefreshEveryTicks);
        Assert.True(ui.Named<Border>("MessageBar").IsVisible);

        ui.Tick(MainViewModel.RefreshEveryTicksWhileUnavailable - MainViewModel.RefreshEveryTicks);
        Assert.False(ui.Named<Border>("MessageBar").IsVisible);
    }

    private void BreakDatabase()
    {
        File.Delete(DbPath + "-wal");
        File.Delete(DbPath + "-shm");
        File.WriteAllText(DbPath, "this is not a database, just some text that is long enough to have a header....");
    }

    [AvaloniaFact]
    public void Storage_message_clears_by_itself_once_the_problem_is_fixed()
    {
        PutTextFileAtDbPath();
        using var ui = Open();

        File.Delete(DbPath);
        ui.Tick(MainViewModel.RefreshEveryTicksWhileUnavailable);

        Assert.False(ui.Named<Border>("MessageBar").IsVisible);
        Assert.Equal(["default"], ui.Names());
        Assert.True(ui.InRows<Button>("toggle")[0].IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Buttons_on_a_database_that_breaks_while_open_show_the_problem()
    {
        using var ui = Open();
        BreakDatabase();

        ui.Click(ui.InRows<Button>("toggle")[0]);

        Assert.True(ui.Named<Border>("MessageBar").IsVisible);
        Assert.StartsWith("The ", Text(ui.Named<TextBlock>("MessageText")), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Busy_database_shows_the_busy_message_and_recovers()
    {
        using var ui = Open(new TimerCommandHandler(new SqliteTimerStorage(DbPath, busyTimeoutSeconds: 1), _clock));

        using (var other = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            other.Open();
            using var hold = other.BeginTransaction(deferred: false);
            ui.Click(ui.InRows<Button>("toggle")[0]);

            Assert.Contains("busy", Text(ui.Named<TextBlock>("MessageText")), StringComparison.Ordinal);
        }

        ui.Click(ui.InRows<Button>("toggle")[0]);
        Assert.False(ui.Named<Border>("MessageBar").IsVisible);
        Assert.Equal("Pause", Text(ui.InRows<Button>("toggle")[0]));
    }

    // --- mini mode ---

    private static void AddTimer(Ui ui, string name)
    {
        ui.Click(ui.Named<Button>("OpenAddButton"));
        ui.Type(ui.Named<TextBox>("NewTimerName"), name);
        ui.Click(ui.Named<Button>("AddButton"));
    }

    [AvaloniaFact]
    public void Mini_mode_shows_only_running_and_paused_timers()
    {
        using var ui = Open();
        AddTimer(ui, "paused one");
        AddTimer(ui, "stopped one");
        ui.Click(ui.InRows<Button>("toggle")[0]);
        ui.Click(ui.InRows<Button>("toggle")[1]);
        _clock.Advance(TimeSpan.FromSeconds(5));
        ui.Click(ui.InRows<Button>("toggle")[1]);

        ui.Click(ui.Named<Button>("MiniModeButton"));

        Assert.Equal(["default", "paused one"], ui.VisibleNames());
        Assert.Equal(WindowMode.Mini, ui.Vm.Mode);
    }

    [AvaloniaFact]
    public void Mini_mode_hides_reset_delete_and_the_add_bar_and_normal_mode_brings_them_back()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("toggle")[0]);

        ui.Click(ui.Named<Button>("MiniModeButton"));

        Assert.False(ui.InRows<Button>("reset")[0].IsEffectivelyVisible);
        Assert.False(ui.InRows<Button>("delete")[0].IsEffectivelyVisible);
        Assert.False(ui.Named<Button>("OpenAddButton").IsEffectivelyVisible);
        Assert.True(ui.InRows<Button>("toggle")[0].IsEffectivelyVisible);

        ui.DoubleClickEmptySpace();

        Assert.Equal(WindowMode.Normal, ui.Vm.Mode);
        Assert.True(ui.InRows<Button>("reset")[0].IsEffectivelyVisible);
        Assert.True(ui.Named<Button>("OpenAddButton").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Mini_mode_with_nothing_running_says_so()
    {
        using var ui = Open();

        ui.Click(ui.Named<Button>("MiniModeButton"));

        Assert.Empty(ui.VisibleNames());
        Assert.True(ui.Named<TextBlock>("MiniEmptyText").IsVisible);
        Assert.False(ui.Named<TextBlock>("EmptyText").IsVisible);
    }

    [AvaloniaFact]
    public void A_timer_started_from_the_cli_appears_in_mini_mode()
    {
        using var ui = Open();
        ui.Click(ui.Named<Button>("MiniModeButton"));

        Handler().Execute(new TimerCommand.Start(new TimerRef.Default()));
        ui.Tick(MainViewModel.RefreshEveryTicks);

        Assert.Equal(["default"], ui.VisibleNames());
        Assert.False(ui.Named<TextBlock>("MiniEmptyText").IsVisible);
    }

    [AvaloniaFact]
    public void Rename_is_not_offered_in_mini_mode()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("toggle")[0]);
        ui.Click(ui.Named<Button>("MiniModeButton"));
        var area = NameArea(ui, 0);
        var at = new Point(area.Bounds.Width - 4, area.Bounds.Height / 2);

        ui.ClickAt(area, at, MouseButton.Right);
        var rename = area.ContextMenu!.Items.OfType<MenuItem>().Single();
        if (area.ContextMenu.IsOpen)
        {
            Assert.False(rename.IsEffectivelyEnabled);
            area.ContextMenu.Close();
        }
        ui.ClickAt(area, at);
        ui.ClickAt(area, at);

        Assert.False(ui.InRows<TextBox>("rename")[0].IsVisible);
    }

    [AvaloniaFact]
    public void Mini_window_is_at_most_360_wide_and_normal_mode_restores_the_size()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("toggle")[0]);
        AddTimer(ui, new string('w', TimerName.MaxLength));
        ui.Click(ui.InRows<Button>("toggle")[1]);
        var normalWidth = ui.Window.Width;

        ui.Click(ui.Named<Button>("MiniModeButton"));

        Assert.True(ui.Window.Bounds.Width <= MainWindow.MiniMaxWidth, $"mini width {ui.Window.Bounds.Width}");

        ui.DoubleClickEmptySpace();

        Assert.Equal(normalWidth, ui.Window.Width);
    }

    [AvaloniaFact]
    public void A_maximized_window_comes_down_for_mini_mode_and_goes_back_up_after()
    {
        using var ui = Open();
        ui.Window.WindowState = WindowState.Maximized;
        ui.Pump();

        ui.Click(ui.Named<Button>("MiniModeButton"));
        Assert.Equal(WindowState.Normal, ui.Window.WindowState);

        ui.DoubleClickEmptySpace();
        Assert.Equal(WindowState.Maximized, ui.Window.WindowState);
    }

    // --- mini window follows its rows, and the name cursor ---

    /// <summary>Lets the window's own 250 ms ticker run for a while, which is what refits the mini window.</summary>
    private static void RunTicker(Ui ui, int milliseconds = 600)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            using var slice = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
        ui.Pump();
    }

    /// <summary>Starts a timer from another client and lets the window pick it up and refit.</summary>
    private void StartFromCli(Ui ui, string name)
    {
        var cli = Handler();
        cli.Execute(new TimerCommand.Add(name));
        cli.Execute(new TimerCommand.Start(new TimerRef.ByName(name)));
        ui.Tick(MainViewModel.RefreshEveryTicks);
        RunTicker(ui);
    }

    /// <summary>Stops a timer from another client (pause, then reset to zero) and lets the window pick it up and refit.</summary>
    private void StopFromCli(Ui ui, string name)
    {
        var cli = Handler();
        cli.Execute(new TimerCommand.Pause(new TimerRef.ByName(name)));
        cli.Execute(new TimerCommand.Reset(new TimerRef.ByName(name)));
        ui.Tick(MainViewModel.RefreshEveryTicks);
        RunTicker(ui);
    }

    private static Ui EnterMini(Ui ui)
    {
        ui.Click(ui.Named<Button>("MiniModeButton"));
        RunTicker(ui);
        return ui;
    }

    [AvaloniaFact]
    public void Mini_window_grows_when_a_timer_starts_and_shrinks_when_it_stops()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("toggle")[0]);
        EnterMini(ui);
        var oneRow = ui.Window.Height;

        StartFromCli(ui, "second");
        var twoRows = ui.Window.Height;
        StopFromCli(ui, "second");

        Assert.Equal(["default"], ui.VisibleNames());
        Assert.True(twoRows > oneRow, $"one row {oneRow}, two rows {twoRows}");
        Assert.Equal(oneRow, ui.Window.Height, 0.5);
        Assert.True(ui.Window.Width <= MainWindow.MiniMaxWidth, $"width {ui.Window.Width}");
    }

    [AvaloniaFact]
    public void Mini_window_still_fits_after_the_window_manager_turns_size_to_content_off()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("toggle")[0]);
        EnterMini(ui);
        var oneRow = ui.Window.Height;

        // What a drag does on X11, after which only the window's own refit can follow the rows.
        ui.Window.SizeToContent = SizeToContent.Manual;
        StartFromCli(ui, "second");
        StartFromCli(ui, "third");

        Assert.True(ui.Window.Height > oneRow, $"one row {oneRow}, three rows {ui.Window.Height}");
        Assert.True(ui.Window.Width <= MainWindow.MiniMaxWidth, $"width {ui.Window.Width}");
    }

    [AvaloniaFact]
    public void Mini_window_with_nothing_running_shrinks_to_the_empty_text()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("toggle")[0]);
        StartFromCli(ui, "second");
        EnterMini(ui);
        var twoRows = ui.Window.Height;

        StopFromCli(ui, "second");
        StopFromCli(ui, "default");

        Assert.True(ui.Named<TextBlock>("MiniEmptyText").IsVisible);
        Assert.True(ui.Window.Height < twoRows, $"two rows {twoRows}, none {ui.Window.Height}");
    }

    [AvaloniaFact]
    public void Normal_mode_is_not_refitted_when_rows_come_and_go()
    {
        using var ui = Open();
        RunTicker(ui);
        var size = (ui.Window.Width, ui.Window.Height);

        StartFromCli(ui, "second");
        StartFromCli(ui, "third");

        Assert.Equal(size, (ui.Window.Width, ui.Window.Height));
    }

    [AvaloniaFact]
    public void Mini_window_in_the_lower_half_grows_upward_keeping_its_bottom_edge()
    {
        using var ui = Open();
        var area = ui.Window.Screens.ScreenFromWindow(ui.Window)?.WorkingArea;
        Assert.SkipWhen(area is null, "The headless platform reports no screen, so there's no working area to anchor to.");
        ui.Click(ui.InRows<Button>("toggle")[0]);
        EnterMini(ui);
        ui.Window.Position = new PixelPoint(area!.Value.X + 10, area.Value.Bottom - 200);
        ui.Pump();
        var bottom = Bottom(ui);

        StartFromCli(ui, "second");

        Assert.True(ui.Window.Position.Y < area.Value.Bottom - 200, "The top edge should have moved up.");
        Assert.InRange(Bottom(ui), bottom - 1, bottom + 1);
    }

    [AvaloniaFact]
    public void After_a_drag_a_mini_window_in_the_lower_half_grows_upward_keeping_its_bottom_edge()
    {
        using var ui = Open();
        var area = ui.Window.Screens.ScreenFromWindow(ui.Window)?.WorkingArea;
        Assert.SkipWhen(area is null, "The headless platform reports no screen, so there's no working area to anchor to.");
        ui.Click(ui.InRows<Button>("toggle")[0]);
        EnterMini(ui);
        // What a drag does on X11, so only the window's own refit moves it.
        ui.Window.SizeToContent = SizeToContent.Manual;
        ui.Window.Position = new PixelPoint(area!.Value.X + 10, area.Value.Bottom - 200);
        ui.Pump();
        var bottom = Bottom(ui);

        StartFromCli(ui, "second");

        Assert.True(ui.Window.Position.Y < area.Value.Bottom - 200, "The top edge should have moved up.");
        Assert.InRange(Bottom(ui), bottom - 1, bottom + 1);
    }

    [AvaloniaFact]
    public void Mini_window_in_the_upper_half_grows_downward_keeping_its_top_edge()
    {
        using var ui = Open();
        var area = ui.Window.Screens.ScreenFromWindow(ui.Window)?.WorkingArea;
        Assert.SkipWhen(area is null, "The headless platform reports no screen, so there's no working area to anchor to.");
        ui.Click(ui.InRows<Button>("toggle")[0]);
        EnterMini(ui);
        var top = area!.Value.Y + 20;
        ui.Window.Position = new PixelPoint(area.Value.X + 10, top);
        ui.Pump();
        var before = Bottom(ui);

        StartFromCli(ui, "second");

        Assert.Equal(top, ui.Window.Position.Y);
        Assert.True(Bottom(ui) > before, "The bottom edge should have moved down.");
    }

    private static int Bottom(Ui ui) =>
        ui.Window.Position.Y + (int)Math.Round(ui.Window.ClientSize.Height * ui.Window.RenderScaling);

    [AvaloniaFact]
    public void Names_show_a_text_cursor_in_normal_mode_only()
    {
        using var ui = Open();
        ui.Click(ui.InRows<Button>("toggle")[0]);
        var area = NameArea(ui, 0);

        Assert.Contains("renamable", area.Classes);
        Assert.NotNull(area.Cursor);

        ui.Click(ui.Named<Button>("MiniModeButton"));
        Assert.DoesNotContain("renamable", area.Classes);
        Assert.Null(area.Cursor);

        ui.DoubleClickEmptySpace();
        Assert.Contains("renamable", area.Classes);
        Assert.NotNull(area.Cursor);
    }

    [AvaloniaFact]
    public void Rows_added_later_get_the_text_cursor_too()
    {
        using var ui = Open();

        AddTimer(ui, "later");

        Assert.All(ui.InRows<Panel>("nameArea"), a => Assert.Contains("renamable", a.Classes));
        Assert.All(ui.InRows<Panel>("nameArea"), a => Assert.NotNull(a.Cursor));
    }
}
