using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DrawArr.Commands;
using DrawArr.ViewModels;
using Mm = DrawArr.Core;

namespace DrawArr.UiTests;

/// <summary>Window, input and rendering edge cases not covered by <see cref="WindowTests"/>.</summary>
public sealed class WindowGapTests
{
    private static readonly string Smile = char.ConvertFromUtf32(0x1F600);
    private static readonly string Rocket = char.ConvertFromUtf32(0x1F680);

    private static void PlaceBox(Ui ui, double x, double y)
    {
        ui.Click(ui.Centre(ui.Named<Border>("BoxSource")));
        ui.Click(ui.AtMm(x, y));
    }

    private static void Deselect(Ui ui) => ui.Click(ui.AtMm(-150, -70));

    private static Mm.Box BoxAt(Ui ui, int x, int y) => ui.Vm.Drawing.Boxes.Values.Single(b => b.Bounds.X == x && b.Bounds.Y == y);

    /// <summary>Sends a command as an agent and pumps until the window has taken the change in.</summary>
    private static void FromAgent(Ui ui, DrawCommand command)
    {
        using var agent = ui.Host.Queue.Join(CommandSource.Agent);
        var before = ui.Vm.History.Count;
        Assert.True(agent.Send(ui.Window.Session.DocumentId, command).GetAwaiter().GetResult().IsSuccess);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (ui.Vm.History.Count == before && DateTime.UtcNow < until)
        {
            ui.Pump();
            Thread.Sleep(5);
        }
        ui.Pump();
    }

    /// <summary>The rendered pixels inside a millimetre rectangle, as 0xAARRGGBB values row by row.</summary>
    private static uint[] Pixels(Ui ui, Mm.Rect mm)
    {
        ui.Pump();
        var topLeft = ui.AtMm(mm.X, mm.Y);
        var bottomRight = ui.AtMm(mm.Right, mm.Bottom);
        using var frame = ui.Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        using var locked = frame.Lock();
        var pixels = new List<uint>();
        for (var y = (int)topLeft.Y; y < (int)bottomRight.Y; y++)
        {
            for (var x = (int)topLeft.X; x < (int)bottomRight.X; x++)
                pixels.Add((uint)Marshal.ReadInt32(locked.Address, y * locked.RowBytes + x * 4));
        }
        return [.. pixels];
    }

    private static bool Any(uint[] pixels, Func<int, int, int, bool> test) =>
        pixels.Any(p => test((int)((p >> 16) & 0xFF), (int)((p >> 8) & 0xFF), (int)(p & 0xFF)));

    private static void Pump(Ui ui, Func<bool> until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!until() && DateTime.UtcNow < deadline)
        {
            ui.Pump();
            Thread.Sleep(5);
        }
    }

    // --- keys while typing in the text box ---

    [AvaloniaFact]
    public void Ctrl_z_while_typing_in_the_text_box_doesnt_undo_the_drawing()
    {
        using var ui = new Ui();
        PlaceBox(ui, 0, 0);
        var text = ui.Named<TextBox>("BoxText");
        text.Focus();
        ui.Window.KeyTextInput("abc");
        ui.Pump();

        ui.Window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        ui.Settle();

        Assert.True(ui.Vm.Drawing.Boxes.Count == 1, $"Ctrl+Z in the text box undid the box; the text box holds '{text.Text}'.");
    }

    [AvaloniaFact]
    public void Backspace_and_delete_in_the_text_box_edit_the_text_not_the_drawing()
    {
        using var ui = new Ui();
        PlaceBox(ui, 0, 0);
        ui.Named<TextBox>("BoxText").Focus();
        ui.Window.KeyTextInput("abc");
        ui.Window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        ui.Window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        ui.Settle();

        Assert.Single(ui.Vm.Drawing.Boxes);
        Assert.Equal("ab", ui.Vm.SelectedText);
    }

    [AvaloniaFact]
    public void Ctrl_enter_applies_the_typed_text()
    {
        using var ui = new Ui();
        PlaceBox(ui, 0, 0);
        ui.Named<TextBox>("BoxText").Focus();
        ui.Window.KeyTextInput("hello");
        ui.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        ui.Settle();

        Assert.True("hello" == ui.Vm.Drawing.Boxes.Values.Single().Text,
            $"The box's text is '{ui.Vm.Drawing.Boxes.Values.Single().Text}' while the text box holds '{ui.Named<TextBox>("BoxText").Text}'.");
    }

    [AvaloniaFact]
    public void Text_typed_for_one_box_goes_to_it_when_another_box_is_clicked()
    {
        using var ui = new Ui();
        PlaceBox(ui, -60, 0);
        PlaceBox(ui, 40, 0);
        ui.Click(ui.AtMm(-40, 10));
        ui.Named<TextBox>("BoxText").Focus();
        ui.Window.KeyTextInput("for the left box");

        ui.Click(ui.AtMm(60, 10));
        ui.Settle();

        Assert.Equal("for the left box", BoxAt(ui, -60, 0).Text);
        Assert.Equal("", BoxAt(ui, 40, 0).Text);
    }

    [AvaloniaFact]
    public void Text_being_typed_survives_an_agents_change_elsewhere()
    {
        using var ui = new Ui();
        PlaceBox(ui, 0, 0);
        ui.Named<TextBox>("BoxText").Focus();
        ui.Window.KeyTextInput("half typed");

        FromAgent(ui, new DrawCommand.AddBox(100, 50, 10, 10, Mm.Colour.Blue));

        Assert.Equal("half typed", ui.Named<TextBox>("BoxText").Text);
    }

    // --- drags, pan and zoom ---

    [AvaloniaFact]
    public void Escape_during_a_drag_puts_the_box_back()
    {
        using var ui = new Ui();
        PlaceBox(ui, 0, 0);
        ui.Window.MouseDown(ui.AtMm(10, 10), MouseButton.Left);
        ui.Window.MouseMove(ui.AtMm(60, 40));
        ui.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        ui.Window.MouseUp(ui.AtMm(60, 40), MouseButton.Left);
        ui.Settle();

        Assert.Equal(new Mm.Rect(0, 0, 40, 20), ui.Vm.Drawing.Boxes.Values.Single().Bounds);
        Assert.Single(ui.Vm.History);
    }

    [AvaloniaFact]
    public void An_agents_change_during_a_drag_doesnt_drop_the_drag()
    {
        using var ui = new Ui();
        PlaceBox(ui, 0, 0);
        ui.Window.MouseDown(ui.AtMm(10, 10), MouseButton.Left);
        ui.Window.MouseMove(ui.AtMm(30, 10));

        FromAgent(ui, new DrawCommand.AddBox(100, 50, 10, 10, Mm.Colour.Blue));
        ui.Window.MouseMove(ui.AtMm(50, 10));
        ui.Window.MouseUp(ui.AtMm(50, 10), MouseButton.Left);
        ui.Settle();

        // The window's own box is box 1, and the agent's is box 2.
        Assert.Equal(new Mm.Rect(40, 0, 40, 20), ui.Vm.Drawing.Boxes[1].Bounds);
    }

    [AvaloniaFact]
    public void A_middle_drag_pans_the_drawing_with_the_pointer()
    {
        using var ui = new Ui();
        var from = ui.AtMm(0, 0);
        var to = from + new Vector(80, 40);

        ui.Window.MouseDown(from, MouseButton.Middle);
        ui.Window.MouseMove(to);
        ui.Window.MouseUp(to, MouseButton.Middle);
        ui.Pump();

        Assert.Equal(to, ui.AtMm(0, 0));
        Assert.Empty(ui.Vm.History);
    }

    [AvaloniaFact]
    public void A_right_drag_over_a_box_pans_and_leaves_the_box_alone()
    {
        using var ui = new Ui();
        PlaceBox(ui, 0, 0);
        var from = ui.AtMm(10, 10);
        var to = from + new Vector(40, 0);

        ui.Window.MouseDown(from, MouseButton.Right);
        ui.Window.MouseMove(to);
        ui.Window.MouseUp(to, MouseButton.Right);
        ui.Settle();

        Assert.Equal(new Mm.Rect(0, 0, 40, 20), ui.Vm.Drawing.Boxes.Values.Single().Bounds);
        Assert.Equal(-10, ui.Vm.View.CentreX, 9);
        Assert.Single(ui.Vm.History);
    }

    [AvaloniaFact]
    public void The_wheel_zooms_around_the_pointer()
    {
        using var ui = new Ui();
        var at = ui.AtMm(30, -20);

        ui.Window.MouseWheel(at, new Vector(0, 1));
        ui.Pump();

        Assert.Equal(ViewTransform.Default.Scale * 1.15, ui.Vm.View.Scale, 9);
        var after = ui.AtMm(30, -20);
        Assert.Equal(at.X, after.X, 6);
        Assert.Equal(at.Y, after.Y, 6);
    }

    [AvaloniaFact]
    public void After_zooming_and_panning_a_placed_box_lands_on_the_grid_under_the_pointer()
    {
        using var ui = new Ui();
        ui.Window.MouseWheel(ui.AtMm(7, 3), new Vector(0, 3));
        ui.Window.MouseDown(ui.AtMm(0, 0), MouseButton.Middle);
        ui.Window.MouseMove(ui.AtMm(0, 0) + new Vector(33, 17));
        ui.Window.MouseUp(ui.AtMm(0, 0) + new Vector(33, 17), MouseButton.Middle);
        ui.Pump();

        PlaceBox(ui, 21, 11);

        Assert.Equal(new Mm.Rect(20, 10, 40, 20), ui.Vm.Drawing.Boxes.Values.Single().Bounds);
    }

    // --- tools ---

    [AvaloniaFact]
    public void The_line_button_toggles_the_line_tool_and_escape_goes_back_to_select()
    {
        using var ui = new Ui();
        var button = ui.Named<Avalonia.Controls.Primitives.ToggleButton>("LineTool");

        ui.Click(ui.Centre(button));
        Assert.Equal(Tool.Line, ui.Vm.Tool);
        Assert.True(button.IsChecked);
        ui.Click(ui.Centre(button));
        Assert.Equal(Tool.Select, ui.Vm.Tool);
        Assert.False(button.IsChecked);

        ui.Click(ui.Centre(button));
        ui.Window.DrawingCanvas.Focus();
        ui.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        ui.Pump();
        Assert.Equal(Tool.Select, ui.Vm.Tool);
        Assert.False(button.IsChecked);
    }

    [AvaloniaFact]
    public void The_box_tool_shows_a_faint_ghost_under_the_pointer()
    {
        using var ui = new Ui();
        ui.Click(ui.Centre(ui.Named<Border>("BoxSource")));
        ui.Window.MouseMove(ui.AtMm(0, 0));
        ui.Pump();

        var edge = Pixels(ui, new Mm.Rect(-1, 5, 2, 10));

        Assert.True(Any(edge, (r, g, b) => r > 0x50 && g > 0x50 && b > 0x50), "No ghost outline where the box would go.");
        Assert.False(Any(edge, (r, g, b) => r > 0xE0 && g > 0xE0 && b > 0xE0), "The ghost is drawn as brightly as a real box.");
    }

    // --- text rendering ---

    [AvaloniaFact]
    public void Two_lines_of_text_sit_either_side_of_the_middle()
    {
        using var ui = new Ui();
        FromAgent(ui, new DrawCommand.AddBox(0, 0, 40, 20, Mm.Colour.White, "W\nW"));

        Assert.True(ui.BrightNear(ui.AtMm(20, 10 - 2.75), 2));
        Assert.True(ui.BrightNear(ui.AtMm(20, 10 + 2.75), 2));
        Assert.False(ui.BrightNear(ui.AtMm(20, 10), 1));
    }

    [AvaloniaFact]
    public void Lines_that_dont_fit_are_cut_off_at_the_top_and_bottom_edges()
    {
        using var ui = new Ui();
        FromAgent(ui, new DrawCommand.AddBox(0, 0, 40, 20, Mm.Colour.White, "1\n2\n3\n4\n5\n6\n7\n8"));

        Assert.False(Any(Pixels(ui, new Mm.Rect(5, -8, 30, 7)), (r, g, b) => r > 0x80 && g > 0x80 && b > 0x80), "Text shows above the box.");
        Assert.False(Any(Pixels(ui, new Mm.Rect(5, 21, 30, 7)), (r, g, b) => r > 0x80 && g > 0x80 && b > 0x80), "Text shows below the box.");
    }

    [AvaloniaFact]
    public void Different_emoji_draw_differently()
    {
        using var ui = new Ui();
        FromAgent(ui, new DrawCommand.AddBox(-60, 0, 40, 20, Mm.Colour.White, Smile));
        FromAgent(ui, new DrawCommand.AddBox(0, 0, 40, 20, Mm.Colour.White, Rocket));

        var smile = Pixels(ui, new Mm.Rect(-55, 3, 30, 14));
        var rocket = Pixels(ui, new Mm.Rect(5, 3, 30, 14));

        // Without a fallback font each emoji draws as the same empty box.
        Assert.NotEqual(smile, rocket);
    }

    [AvaloniaFact]
    public void Different_chinese_characters_draw_differently()
    {
        using var ui = new Ui();
        FromAgent(ui, new DrawCommand.AddBox(-60, 0, 40, 20, Mm.Colour.White, ((char)0x4E2D).ToString()));
        FromAgent(ui, new DrawCommand.AddBox(0, 0, 40, 20, Mm.Colour.White, ((char)0x6587).ToString()));

        Assert.NotEqual(Pixels(ui, new Mm.Rect(-55, 3, 30, 14)), Pixels(ui, new Mm.Rect(5, 3, 30, 14)));
    }

    [AvaloniaFact]
    public void Text_scales_with_the_zoom()
    {
        using var ui = new Ui();
        FromAgent(ui, new DrawCommand.AddBox(0, 0, 40, 20, Mm.Colour.White, "WWW"));
        Deselect(ui);
        var near = new Mm.Rect(5, 3, 30, 14);
        var lit = Pixels(ui, near).Count(p => ((p >> 16) & 0xFF) > 0x80);

        ui.Window.MouseWheel(ui.AtMm(20, 10), new Vector(0, 5));
        ui.Pump();
        var zoomed = Pixels(ui, near).Count(p => ((p >> 16) & 0xFF) > 0x80);

        // Zooming by 1.15^5 (about 2x) scales lit area by about 4x.
        Assert.InRange(zoomed / (double)lit, 3, 5.5);
    }

    // --- the history list ---

    [AvaloniaFact]
    public void Undone_history_entries_are_faded_and_sources_are_shown()
    {
        using var ui = new Ui();
        PlaceBox(ui, 0, 0);
        FromAgent(ui, new DrawCommand.AddBox(60, 0, 10, 10, Mm.Colour.Blue));
        ui.Window.DrawingCanvas.Focus();
        ui.Window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        ui.Settle();

        var rows = ui.Named<ListBox>("HistoryList").GetVisualDescendants().OfType<TextBlock>().ToList();

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain("undone", rows[0].Classes);
        Assert.Contains("undone", rows[1].Classes);
        Assert.Equal(0.4, rows[1].Opacity, 6);
        Assert.Contains("(window)", rows[0].Inlines!.Text, StringComparison.Ordinal);
        Assert.Contains("(agent)", rows[1].Inlines!.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void An_agents_box_appears_on_the_canvas_without_any_input()
    {
        using var ui = new Ui();
        FromAgent(ui, new DrawCommand.AddBox(-50, 30, 20, 10, Mm.Colour.White));

        Assert.True(ui.BrightNear(ui.AtMm(-50, 35)));
    }

    // --- saving from the window ---

    [AvaloniaFact]
    public void Ctrl_s_on_an_unnamed_drawing_with_no_file_chosen_says_it_wasnt_saved()
    {
        using var ui = new Ui();
        PlaceBox(ui, 0, 0);
        ui.Window.DrawingCanvas.Focus();

        ui.Window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
        Pump(ui, () => ui.Vm.Status.Length > 0);

        Assert.Contains("Not saved", ui.Vm.Status, StringComparison.Ordinal);
        Assert.Single(ui.Host.Sessions);
    }

    [AvaloniaFact]
    public void Closing_an_untouched_new_drawing_leaves_no_autosave_file()
    {
        using var ui = new Ui();
        var path = ui.Window.Session.FilePath;

        ui.Window.Close();
        Pump(ui, () => ui.Host.Sessions.Count == 0);

        Assert.Empty(ui.Host.Sessions);
        Assert.False(File.Exists(path));
    }

    [AvaloniaFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void A_window_whose_changes_cant_be_saved_stays_open_and_says_why()
    {
        using var ui = new Ui();
        var folder = Path.Combine(ui.Folder, "locked");
        Directory.CreateDirectory(folder);
        Assert.True(ui.Window.Session.SaveAs(Path.Combine(folder, "a.xml")).GetAwaiter().GetResult().IsSuccess);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            PlaceBox(ui, 0, 0);

            ui.Window.Close();
            Pump(ui, () => ui.Vm.Status.StartsWith("Not closed", StringComparison.Ordinal));

            Assert.Single(ui.Host.Sessions);
            Assert.True(ui.Window.IsVisible);
            Assert.StartsWith("Not closed", ui.Vm.Status, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        ui.Window.Close();
        Pump(ui, () => ui.Host.Sessions.Count == 0);
        Assert.Empty(ui.Host.Sessions);
        Assert.Equal(1, DrawArr.Files.DrawingFile.Load(Path.Combine(folder, "a.xml")).Value.Count);
    }
}
