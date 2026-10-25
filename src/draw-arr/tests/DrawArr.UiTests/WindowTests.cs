using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using DrawArr.Core;
using DrawArr.Files;
using DrawArr.ViewModels;

namespace DrawArr.UiTests;

/// <summary>The drawing window run headless, with the canvas really drawn by SkiaSharp.</summary>
public sealed class WindowTests
{
    [AvaloniaFact]
    public void A_new_drawing_opens_in_a_window_titled_as_autosaved()
    {
        using var ui = new Ui();
        Assert.Equal("Untitled (autosaved) - draw-arr", ui.Window.Title);
        Assert.Empty(ui.Vm.Drawing.Boxes);
    }

    [AvaloniaFact]
    public void The_canvas_is_black_with_the_origin_in_the_middle()
    {
        using var ui = new Ui();
        var canvas = ui.Window.DrawingCanvas;
        var middle = ui.Centre(canvas);
        Assert.Equal(middle, ui.AtMm(0, 0));
        // Away from the axes and grid lines the canvas is black.
        Assert.False(ui.BrightNear(ui.AtMm(12.5, 12.5), 1));
    }

    [AvaloniaFact]
    public void A_box_dragged_from_the_sidebar_lands_where_it_is_let_go()
    {
        using var ui = new Ui();
        var source = ui.Named<Border>("BoxSource");

        ui.Drag(ui.Centre(source), ui.AtMm(21, -9));

        var box = Assert.Single(ui.Vm.Drawing.Boxes.Values);
        Assert.Equal(new DrawArr.Core.Rect(20, -10, DrawingViewModel.NewBoxWidth, DrawingViewModel.NewBoxHeight), box.Bounds);
        Assert.True(ui.BrightNear(ui.AtMm(20, 0)));
    }

    [AvaloniaFact]
    public void A_box_can_be_taken_by_clicking_the_sidebar_then_the_canvas()
    {
        using var ui = new Ui();
        ui.Click(ui.Centre(ui.Named<Border>("BoxSource")));
        Assert.Equal(Tool.Box, ui.Vm.Tool);
        ui.Click(ui.AtMm(0, 0));
        Assert.Single(ui.Vm.Drawing.Boxes);
    }

    [AvaloniaFact]
    public void Text_is_centred_in_its_box_and_clipped_to_it()
    {
        using var ui = new Ui();
        ui.Click(ui.Centre(ui.Named<Border>("BoxSource")));
        ui.Click(ui.AtMm(0, 0));
        ui.Vm.SelectedText = new string('W', 60);
        _ = ui.Vm.CommitText();
        ui.Settle();
        // Deselect, so the selection outline doesn't light the pixels checked below.
        ui.Click(ui.AtMm(-100, -100));

        Assert.Equal(new string('W', 60), ui.Vm.Drawing.Boxes.Values.Single().Text);
        Assert.Equal(new DrawArr.Core.Rect(0, 0, 40, 20), ui.Vm.Drawing.Boxes.Values.Single().Bounds);
        Assert.True(ui.BrightNear(ui.AtMm(20, 10), 4));
        // Far beyond the box's right edge on the text's line, the long text has been cut off.
        Assert.False(ui.BrightNear(ui.AtMm(52.5, 10), 2));
    }

    [AvaloniaFact]
    public void A_line_drawn_between_two_boxes_follows_a_box_that_moves()
    {
        using var ui = new Ui();
        // Both boxes sit near the origin, so they are well inside the 780-pixel-wide canvas.
        foreach (var x in new[] { -60, 40 })
        {
            ui.Click(ui.Centre(ui.Named<Border>("BoxSource")));
            ui.Click(ui.AtMm(x, 0));
        }
        ui.Vm.Tool = Tool.Line;
        ui.Drag(ui.AtMm(-20, 10), ui.AtMm(40, 10));
        ui.Vm.Tool = Tool.Select;
        var drawn = Assert.Single(ui.Vm.Drawing.Lines.Values);
        Assert.Equal(1, Assert.IsType<DrawArr.Core.LineEnd.Attached>(drawn.Start).BoxId);
        Assert.Equal(2, Assert.IsType<DrawArr.Core.LineEnd.Attached>(drawn.End).BoxId);

        ui.Drag(ui.AtMm(50, 5), ui.AtMm(50, 55));

        var drawing = ui.Vm.Drawing;
        Assert.Equal(new DrawArr.Core.Point(40, 60), drawing.Resolve(drawing.Lines[drawn.Id].End));
        Assert.True(ui.BrightNear(ui.AtMm(10, 35)));
    }

    [AvaloniaFact]
    public void Ctrl_z_and_ctrl_y_undo_and_redo()
    {
        using var ui = new Ui();
        ui.Click(ui.Centre(ui.Named<Border>("BoxSource")));
        ui.Click(ui.AtMm(0, 0));

        ui.Window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        ui.Settle();
        Assert.Empty(ui.Vm.Drawing.Boxes);

        ui.Window.KeyPressQwerty(PhysicalKey.Y, RawInputModifiers.Control);
        ui.Settle();
        Assert.Single(ui.Vm.Drawing.Boxes);
        Assert.Equal("Add box 1 at (0, 0) 40x20 (window)", string.Concat(ui.Vm.History.Select(h => $"{h.Description} ({h.Source})")));
    }

    [AvaloniaFact]
    public void Delete_removes_the_selected_box()
    {
        using var ui = new Ui();
        ui.Click(ui.Centre(ui.Named<Border>("BoxSource")));
        ui.Click(ui.AtMm(0, 0));
        ui.Window.DrawingCanvas.Focus();
        ui.Window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        ui.Settle();
        Assert.Empty(ui.Vm.Drawing.Boxes);
    }

    [AvaloniaFact]
    public void Choosing_a_palette_colour_recolours_the_selected_box()
    {
        using var ui = new Ui();
        ui.Click(ui.Centre(ui.Named<Border>("BoxSource")));
        ui.Click(ui.AtMm(0, 0));
        ui.Click(ui.Centre(ui.Named<Button>("ColourRed")));
        Assert.Equal(Colour.Red, ui.Vm.Drawing.Boxes.Values.Single().Colour);
    }

    [AvaloniaFact]
    public void Closing_the_window_saves_the_drawing_to_the_autosave_folder()
    {
        using var ui = new Ui();
        ui.Click(ui.Centre(ui.Named<Border>("BoxSource")));
        ui.Click(ui.AtMm(0, 0));
        var path = ui.Window.Session.FilePath;

        ui.Window.Close();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (ui.Host.Sessions.Count > 0 && DateTime.UtcNow < until)
        {
            ui.Pump();
            Thread.Sleep(5);
        }

        Assert.Empty(ui.Host.Sessions);
        Assert.Equal(1, DrawingFile.Load(path).Value.Count);
    }

    [AvaloniaFact]
    public void Save_as_moves_the_drawing_out_of_the_autosave_folder()
    {
        using var ui = new Ui();
        ui.Click(ui.Centre(ui.Named<Border>("BoxSource")));
        ui.Click(ui.AtMm(0, 0));
        var autosave = ui.Window.Session.FilePath;
        ui.Window.Session.Flush().GetAwaiter().GetResult();
        var named = Path.Combine(ui.Folder, "mine.xml");

        Assert.True(ui.Window.Session.SaveAs(named).GetAwaiter().GetResult().IsSuccess);
        ui.Pump();

        Assert.Equal("mine.xml - draw-arr", ui.Window.Title);
        Assert.False(File.Exists(autosave));
        Assert.Equal(1, DrawingFile.Load(named).Value.Count);
    }
}
