using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TimeArr.App;
using TimeArr.Commands;
using TimeArr.Core;
using TimeArr.Data;
using TimeArr.ViewModels;

namespace TimeArr.UiTests;

/// <summary>Layout checks on the real window (alignment, clipping, controls that move, screen edges), each failing one showing a layout bug.</summary>
public sealed class LayoutTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    private const string LongName = "A rather long timer name that goes on and on and on, to sixty-four";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "time-arr-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(T0);
    private string DbPath => Path.Combine(_root, "time-arr", "time-arr.db");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private TimerCommandHandler Handler() => new(new SqliteTimerStorage(DbPath), _clock);

    /// <summary>An open window over a fresh viewmodel and another client playing the CLI, closed on Dispose.</summary>
    private sealed class Ui : IDisposable
    {
        public Ui(MainViewModel vm, TimerCommandHandler cli)
        {
            Vm = vm;
            Cli = cli;
            Window = new MainWindow { DataContext = vm };
            Window.Show();
            Pump();
        }

        public MainViewModel Vm { get; }
        public TimerCommandHandler Cli { get; }
        public MainWindow Window { get; }

        public void Dispose() => Window.Close();

        public void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Re-reads the timers as the window does every second, then lays out.</summary>
        public void Refresh()
        {
            for (var i = 0; i < MainViewModel.RefreshEveryTicks; i++)
                Vm.Tick();
            Pump();
        }

        /// <summary>Lets the window's own 250 ms ticker run, which is what refits the mini window.</summary>
        public void RunTicker(int milliseconds = 600)
        {
            var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < until)
            {
                using var slice = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                Dispatcher.UIThread.MainLoop(slice.Token);
            }
            Pump();
        }

        public T Named<T>(string name) where T : Control =>
            Window.FindControl<T>(name) ?? throw new InvalidOperationException($"No control named {name}.");

        /// <summary>The visible controls with this class, top to bottom.</summary>
        public List<T> InRows<T>(string cssClass) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().Where(c => c.Classes.Contains(cssClass) && c.IsEffectivelyVisible).ToList();

        /// <summary>The control's bounds in window coordinates.</summary>
        public Rect InWindow(Control control) =>
            new(control.TranslatePoint(default, Window)!.Value, control.Bounds.Size);

        /// <summary>The control's top-left corner on the screen, in pixels.</summary>
        public PixelPoint OnScreen(Control control) =>
            Window.PointToScreen(control.TranslatePoint(default, Window)!.Value);

        public PixelRect ScreenArea() =>
            Window.Screens.ScreenFromWindow(Window)?.WorkingArea ?? throw new InvalidOperationException("No screen.");

        public PixelRect WindowOnScreen() =>
            new(Window.Position, PixelSize.FromSize(Window.ClientSize, Window.RenderScaling));

        public void Mini()
        {
            Vm.ToggleMiniModeCommand.Execute(null);
            Pump();
            RunTicker();
        }
    }

    private Ui Open() => new(new MainViewModel(Handler(), _clock), Handler());

    private static void Add(Ui ui, string name, bool start = false)
    {
        ui.Cli.Execute(new TimerCommand.Add(name));
        if (start)
            ui.Cli.Execute(new TimerCommand.Start(new TimerRef.ByName(name)));
    }

    // --- columns that don't line up, and controls that jump ---

    [AvaloniaFact]
    public void Elapsed_times_line_up_whatever_the_button_says()
    {
        // Each row is its own Grid with Auto columns, so the wider "Resume" button pushes that row's time left.
        using var ui = Open();
        Add(ui, "running", start: true);
        Add(ui, "paused", start: true);
        // Paused with time on it, since a timer paused at zero counts as stopped and says "Start".
        _clock.Advance(TimeSpan.FromMinutes(5));
        ui.Cli.Execute(new TimerCommand.Pause(new TimerRef.ByName("paused")));
        ui.Refresh();

        Assert.Contains("Resume", ui.Vm.Timers.Select(t => t.ToggleText));
        var rightEdges = ui.InRows<TextBlock>("elapsed").Select(t => Math.Round(ui.InWindow(t).Right)).Distinct().ToList();

        Assert.True(rightEdges.Count == 1, $"Elapsed right edges per row: {string.Join(", ", ui.InRows<TextBlock>("elapsed").Select(t => ui.InWindow(t).Right))}");
    }

    [AvaloniaFact]
    public void Pausing_a_timer_does_not_shift_its_time_sideways()
    {
        using var ui = Open();
        ui.Cli.Execute(new TimerCommand.Start(new TimerRef.Default()));
        ui.Refresh();
        _clock.Advance(TimeSpan.FromMinutes(5));
        ui.Refresh();
        var before = ui.InWindow(ui.InRows<TextBlock>("elapsed")[0]).X;

        ui.Vm.Timers[0].ToggleCommand.Execute(null);
        ui.Pump();

        Assert.Equal("Resume", ui.Vm.Timers[0].ToggleText);
        Assert.Equal(before, ui.InWindow(ui.InRows<TextBlock>("elapsed")[0]).X, 0.5);
    }

    [AvaloniaFact]
    public void A_long_running_timer_keeps_its_time_on_one_line_and_its_buttons_in_place()
    {
        using var ui = Open();
        ui.Cli.Execute(new TimerCommand.Start(new TimerRef.Default()));
        ui.Refresh();
        var deleteBefore = ui.InWindow(ui.InRows<Button>("delete")[0]).X;

        _clock.Advance(TimeSpan.FromDays(1000));
        ui.Refresh();
        var elapsed = ui.InRows<TextBlock>("elapsed")[0];

        Assert.Equal("1000d 00:00:00", elapsed.Text);
        Assert.Equal(deleteBefore, ui.InWindow(ui.InRows<Button>("delete")[0]).X, 0.5);
        Assert.True(elapsed.Bounds.Width + elapsed.Margin.Right >= elapsed.DesiredSize.Width - 0.5, "The elapsed time is clipped.");
    }

    // --- clipping and room ---

    [AvaloniaFact]
    public void At_the_minimum_window_size_every_name_is_still_readable()
    {
        // At 420 px the fixed columns leave the name almost nothing, and a row showing days leaves it nothing at all.
        using var ui = Open();
        Add(ui, "work", start: true);
        _clock.Advance(TimeSpan.FromDays(150));
        ui.Window.Width = ui.Window.MinWidth;
        ui.Window.Height = ui.Window.MinHeight;
        ui.Refresh();

        var widths = ui.InRows<TextBlock>("name").Select(n => Math.Round(n.Bounds.Width)).ToList();

        Assert.True(widths.All(w => w >= 40), $"Name widths at {ui.Window.MinWidth} px: {string.Join(", ", widths)}");
    }

    [AvaloniaFact]
    public void The_scroll_bar_does_not_cover_the_delete_buttons()
    {
        using var ui = Open();
        for (var i = 0; i < 25; i++)
            Add(ui, $"bulk {i}");
        ui.Refresh();
        var bar = ui.Window.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical && b.IsEffectivelyVisible);

        var barLeft = ui.InWindow(bar).Left;

        Assert.All(ui.InRows<Button>("delete"), d => Assert.True(ui.InWindow(d).Right <= barLeft + 0.5, $"Delete ends at {ui.InWindow(d).Right}, bar starts at {barLeft}"));
    }

    [AvaloniaFact]
    public void Starting_a_rename_does_not_change_the_row_height()
    {
        using var ui = Open();
        Add(ui, "below");
        ui.Refresh();
        var nextRowBefore = ui.InWindow(ui.InRows<Panel>("nameArea")[1]).Y;

        ui.Vm.Timers[0].BeginRenameCommand.Execute(null);
        ui.Pump();

        Assert.Equal(nextRowBefore, ui.InWindow(ui.InRows<Panel>("nameArea")[1]).Y, 0.5);
    }

    [AvaloniaFact]
    public void With_both_bars_up_at_the_minimum_size_a_whole_row_still_shows()
    {
        using var ui = Open();
        ui.Cli.Execute(new TimerCommand.Rename(new TimerRef.Default(), LongName[..TimerName.MaxLength]));
        ui.Window.Width = ui.Window.MinWidth;
        ui.Window.Height = ui.Window.MinHeight;
        ui.Refresh();
        // A failed rename leaves its two-line error in the bar, and the delete question then carries the 64-character name.
        var timer = ui.Vm.Timers[0];
        timer.BeginRenameCommand.Execute(null);
        timer.EditName = "x\u200B";
        timer.CommitRenameCommand.Execute(null);
        timer.CancelRenameCommand.Execute(null);
        ui.Vm.Timers[0].DeleteCommand.Execute(null);
        ui.Pump();
        var viewer = ui.Window.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.IsEffectivelyVisible);
        var rowHeight = ui.InRows<Panel>("nameArea")[0].Bounds.Height;

        Assert.True(ui.Named<Border>("ConfirmBar").IsVisible && ui.Named<Border>("MessageBar").IsVisible, "Both bars should be up.");
        Assert.True(viewer.Viewport.Height >= rowHeight, $"The list has {viewer.Viewport.Height} px for rows {rowHeight} px tall.");
    }

    // --- the add dialog and the message bar ---

    [AvaloniaFact]
    public void A_failed_add_shows_its_error_once()
    {
        // The dialog's error line and the message bar behind the overlay are bound to the same Message.
        using var ui = Open();
        ui.Vm.OpenAddDialogCommand.Execute(null);
        ui.Vm.NewTimerName = "DEFAULT";
        ui.Vm.AddCommand.Execute(null);
        ui.Pump();

        Assert.True(ui.Named<TextBlock>("AddError").IsEffectivelyVisible);
        Assert.False(ui.Named<Border>("MessageBar").IsEffectivelyVisible, "The same error also shows in the message bar behind the dialog.");
    }

    [AvaloniaFact]
    public void Cancelling_the_add_dialog_takes_its_error_with_it()
    {
        using var ui = Open();
        ui.Vm.OpenAddDialogCommand.Execute(null);
        ui.Vm.NewTimerName = "DEFAULT";
        ui.Vm.AddCommand.Execute(null);

        ui.Vm.CancelAddCommand.Execute(null);
        ui.Pump();

        Assert.False(ui.Named<Border>("MessageBar").IsVisible, $"Still showing after Cancel: {ui.Named<TextBlock>("MessageText").Text}");
    }

    [AvaloniaFact]
    public void Cancelling_a_rename_takes_its_error_with_it()
    {
        // The rename counterpart of sub plan 009 #10: a failed rename's error outlives the rename once Escape cancels it.
        using var ui = Open();
        var timer = ui.Vm.Timers[0];
        timer.BeginRenameCommand.Execute(null);
        timer.EditName = "-flag";
        timer.CommitRenameCommand.Execute(null);
        ui.Pump();
        Assert.True(ui.Named<Border>("MessageBar").IsVisible);

        timer.CancelRenameCommand.Execute(null);
        ui.Pump();

        Assert.False(ui.Named<Border>("MessageBar").IsVisible, $"Still showing after Escape: {ui.Named<TextBlock>("MessageText").Text}");
    }

    [AvaloniaFact]
    public void A_background_refresh_problem_is_not_shown_as_an_add_error()
    {
        using var ui = Open();
        ui.Vm.OpenAddDialogCommand.Execute(null);
        ui.Pump();
        File.Delete(DbPath + "-wal");
        File.Delete(DbPath + "-shm");
        File.WriteAllText(DbPath, "this is not a database, just some text that is long enough to have a header....");

        ui.Refresh();

        Assert.False(ui.Named<TextBlock>("AddError").IsEffectivelyVisible,
            $"The add dialog says: {ui.Named<TextBlock>("AddError").Text}");
    }

    [AvaloniaFact]
    public void The_add_dialog_fits_the_minimum_window_even_with_a_long_error()
    {
        using var ui = Open();
        ui.Window.Width = ui.Window.MinWidth;
        ui.Window.Height = ui.Window.MinHeight;
        ui.Vm.OpenAddDialogCommand.Execute(null);
        ui.Vm.NewTimerName = new string('x', 200);
        ui.Vm.AddCommand.Execute(null);
        ui.Pump();
        var card = (Border)ui.Named<Border>("AddDialog").Child!;

        var box = ui.InWindow(card);

        Assert.True(box.Top >= 0 && box.Bottom <= ui.Window.ClientSize.Height, $"Dialog {box} in a {ui.Window.ClientSize} window.");
        Assert.True(ui.InWindow(ui.Named<Button>("AddButton")).Bottom <= ui.Window.ClientSize.Height);
    }

    // --- the mini window: moving targets and screen edges ---

    [AvaloniaFact]
    public void In_mini_mode_stopping_the_widest_timer_does_not_move_the_other_buttons()
    {
        // The mini window is as wide as its widest row, so when that row goes the window narrows and the other buttons jump left.
        using var ui = Open();
        ui.Cli.Execute(new TimerCommand.Start(new TimerRef.Default()));
        Add(ui, LongName[..40], start: true);
        ui.Refresh();
        ui.Mini();
        var before = ui.OnScreen(ui.InRows<Button>("toggle")[0]);

        ui.Cli.Execute(new TimerCommand.Pause(new TimerRef.ByName(LongName[..40])));
        ui.Cli.Execute(new TimerCommand.Reset(new TimerRef.ByName(LongName[..40])));
        ui.Refresh();
        ui.RunTicker();

        Assert.Single(ui.InRows<Button>("toggle"));
        Assert.Equal(before, ui.OnScreen(ui.InRows<Button>("toggle")[0]));
    }

    [AvaloniaFact]
    public void In_mini_mode_every_name_is_still_readable()
    {
        using var ui = Open();
        ui.Cli.Execute(new TimerCommand.Start(new TimerRef.Default()));
        Add(ui, "work", start: true);
        ui.Refresh();

        ui.Mini();
        var widths = ui.InRows<TextBlock>("name").Select(n => Math.Round(n.Bounds.Width)).ToList();

        Assert.Equal(2, widths.Count);
        Assert.True(widths.All(w => w >= 30), $"Name widths in mini mode: {string.Join(", ", widths)} (window {ui.Window.Width} wide).");
    }

    [AvaloniaFact]
    public void In_mini_mode_one_long_name_does_not_hide_the_other_names()
    {
        using var ui = Open();
        ui.Cli.Execute(new TimerCommand.Start(new TimerRef.Default()));
        Add(ui, "work", start: true);
        Add(ui, LongName[..TimerName.MaxLength], start: true);
        // Days on the clock widen the time column, as in the screenshot where every name vanished.
        _clock.Advance(TimeSpan.FromDays(150));
        ui.Refresh();

        ui.Mini();
        var widths = ui.InRows<TextBlock>("name").Select(n => Math.Round(n.Bounds.Width)).ToList();

        Assert.Equal(3, widths.Count);
        Assert.True(widths.All(w => w >= 30), $"Name widths in mini mode: {string.Join(", ", widths)} (window {ui.Window.Width} wide).");
    }

    [AvaloniaFact]
    public void In_mini_mode_a_long_name_is_trimmed_not_dropped()
    {
        using var ui = Open();
        Add(ui, LongName[..TimerName.MaxLength], start: true);
        ui.Refresh();

        ui.Mini();
        var name = ui.InRows<TextBlock>("name").Single();

        var grid = name.FindAncestorOfType<Grid>()!;
        var area = ui.InRows<Panel>("nameArea").Single();
        var cols = string.Join(", ", grid.ColumnDefinitions.Select(c => Math.Round(c.ActualWidth)));
        Assert.True(name.Bounds.Width >= 60, $"Name width {name.Bounds.Width} (area {area.Bounds.Width}, row {grid.Bounds.Width}, columns {cols}, client {ui.Window.ClientSize}) in a {ui.Window.Width} wide mini window.");
        Assert.True(ui.Window.Width <= MainWindow.MiniMaxWidth);
    }

    [AvaloniaFact]
    public void In_mini_mode_pausing_does_not_change_the_window_width()
    {
        using var ui = Open();
        ui.Cli.Execute(new TimerCommand.Start(new TimerRef.Default()));
        ui.Refresh();
        _clock.Advance(TimeSpan.FromMinutes(5));
        ui.Refresh();
        ui.Mini();
        var width = ui.Window.Width;

        ui.Vm.Timers[0].ToggleCommand.Execute(null);
        ui.Pump();
        ui.RunTicker();

        Assert.Equal("Resume", ui.Vm.Timers[0].ToggleText);
        Assert.Equal(width, ui.Window.Width, 0.5);
    }

    [AvaloniaFact]
    public void In_mini_mode_low_on_the_screen_a_new_timer_moves_the_existing_rows_up_by_exactly_one_row()
    {
        // Kept as designed (user, sub plan 009 #4): growing upward keeps the bottom edge, so existing rows move up by one row and no more.
        using var ui = Open();
        ui.Cli.Execute(new TimerCommand.Start(new TimerRef.Default()));
        ui.Refresh();
        ui.Mini();
        var area = ui.ScreenArea();
        ui.Window.Position = new PixelPoint(area.X + 10, area.Bottom - 200);
        ui.Pump();
        var before = ui.OnScreen(ui.InRows<Button>("toggle")[0]);
        var bottom = ui.WindowOnScreen().Bottom;

        Add(ui, "zz later", start: true);
        ui.Refresh();
        ui.RunTicker();

        var toggles = ui.InRows<Button>("toggle");
        var pitch = ui.OnScreen(toggles[1]).Y - ui.OnScreen(toggles[0]).Y;
        var after = ui.OnScreen(toggles[0]);
        Assert.Equal(2, toggles.Count);
        Assert.InRange(ui.WindowOnScreen().Bottom, bottom - 1, bottom + 1);
        Assert.Equal(before.X, after.X);
        Assert.InRange(before.Y - after.Y, pitch - 1, pitch + 1);
    }

    [AvaloniaFact]
    public void A_mini_window_at_the_right_edge_stays_on_screen_when_it_widens()
    {
        using var ui = Open();
        ui.Cli.Execute(new TimerCommand.Start(new TimerRef.Default()));
        ui.Refresh();
        ui.Mini();
        var area = ui.ScreenArea();
        ui.Window.Position = new PixelPoint(area.Right - ui.WindowOnScreen().Width - 2, area.Y + 100);
        ui.Pump();

        Add(ui, LongName[..40], start: true);
        ui.Refresh();
        ui.RunTicker();

        Assert.True(ui.WindowOnScreen().Right <= area.Right, $"Window {ui.WindowOnScreen()} on a {area} screen.");
    }

    [AvaloniaFact]
    public void A_mini_window_with_many_running_timers_stays_within_the_screen()
    {
        using var ui = Open();
        for (var i = 0; i < 60; i++)
            Add(ui, $"t{i:00}", start: true);
        ui.Refresh();

        ui.Mini();
        var area = ui.ScreenArea();

        Assert.True(ui.WindowOnScreen().Height <= area.Height, $"Window {ui.WindowOnScreen()} on a {area} screen.");
        Assert.True(ui.WindowOnScreen().Bottom <= area.Bottom, $"Window {ui.WindowOnScreen()} on a {area} screen.");
    }

    [AvaloniaFact]
    public void Leaving_mini_mode_low_on_the_screen_keeps_the_normal_window_on_screen()
    {
        using var ui = Open();
        ui.Cli.Execute(new TimerCommand.Start(new TimerRef.Default()));
        ui.Refresh();
        var area = ui.ScreenArea();
        ui.Mini();
        ui.Window.Position = new PixelPoint(area.X + 10, area.Bottom - 80);
        ui.Pump();

        ui.Vm.ToggleMiniModeCommand.Execute(null);
        ui.Pump();

        Assert.True(ui.WindowOnScreen().Bottom <= area.Bottom, $"Normal window {ui.WindowOnScreen()} on a {area} screen.");
    }

    [AvaloniaFact]
    public void A_long_message_in_mini_mode_wraps_within_the_width_cap()
    {
        using var ui = Open();
        ui.Cli.Execute(new TimerCommand.Start(new TimerRef.Default()));
        ui.Refresh();
        ui.Mini();
        File.Delete(DbPath + "-wal");
        File.Delete(DbPath + "-shm");
        File.WriteAllText(DbPath, "this is not a database, just some text that is long enough to have a header....");

        ui.Refresh();
        ui.RunTicker();

        Assert.True(ui.Named<Border>("MessageBar").IsVisible);
        Assert.True(ui.Window.Width <= MainWindow.MiniMaxWidth, $"width {ui.Window.Width}");
        var bar = ui.InWindow(ui.Named<Border>("MessageBar"));
        Assert.True(bar.Right <= ui.Window.ClientSize.Width && bar.Bottom <= ui.Window.ClientSize.Height, $"Message bar {bar} in a {ui.Window.ClientSize} window.");
    }
}
