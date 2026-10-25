using DrawArr.Commands;
using DrawArr.Core;
using DrawArr.ViewModels;

namespace DrawArr.Tests.ViewModels;

/// <summary>Edge cases for <see cref="DrawingViewModel"/> not covered by <see cref="DrawingViewModelTests"/>, against a real workspace and queue.</summary>
public sealed class ViewModelGapTests : IDisposable
{
    private readonly Workspace _workspace = new();
    private readonly CommandQueue _queue;
    private readonly Requester _window;
    private readonly Requester _agent;
    private long _id;
    private DrawingViewModel _vm = null!;

    public ViewModelGapTests()
    {
        _queue = new CommandQueue(_workspace);
        _window = _queue.Join(CommandSource.Window);
        _agent = _queue.Join(CommandSource.Agent);
        Open(Drawing.Empty);
    }

    public void Dispose() => _queue.Dispose();

    private void Open(Drawing drawing)
    {
        var document = new Document(drawing);
        _id = _workspace.Open(document);
        _vm = new DrawingViewModel("test", new DocumentReply(document.Drawing, document.History, document.Revision, null, DocumentChange.None),
            command => _window.Send(_id, command), () => Task.FromResult(Result.Ok()), action => action());
    }

    private Task Settle() => _vm.WhenIdle();

    private async Task<long> PlaceBox(double x, double y)
    {
        _vm.BoxToolCommand.Execute(null);
        _vm.PointerPressed(x, y);
        await Settle();
        return _vm.SelectedId!.Value;
    }

    /// <summary>Sends a command as an agent and hands the change to the window, as the app's change notice does.</summary>
    private async Task<DocumentReply> FromAgent(DrawCommand command)
    {
        var reply = await _agent.Send(_id, command);
        Assert.True(reply.IsSuccess, reply.ToString());
        _vm.Apply(reply.Value);
        return reply.Value;
    }

    private async Task<long> DrawLine(double x1, double y1, double x2, double y2)
    {
        _vm.LineToolCommand.Execute(null);
        _vm.PointerPressed(x1, y1);
        _vm.PointerMoved(x2, y2);
        await _vm.PointerReleased(x2, y2);
        _vm.SelectToolCommand.Execute(null);
        return _vm.SelectedId!.Value;
    }

    // --- an agent's change arriving while the user works ---

    [Fact]
    public async Task An_agents_change_elsewhere_keeps_text_being_typed()
    {
        await PlaceBox(0, 0);
        _vm.SelectedText = "half typed";

        await FromAgent(new DrawCommand.AddBox(100, 100, 10, 10, Colour.Blue));

        Assert.Equal("half typed", _vm.SelectedText);
    }

    [Fact]
    public async Task An_agents_change_to_another_box_keeps_a_move_in_progress()
    {
        var id = await PlaceBox(0, 0);
        _vm.PointerPressed(10, 10);
        _vm.PointerMoved(30, 10);

        await FromAgent(new DrawCommand.AddBox(100, 100, 10, 10, Colour.Blue));
        _vm.PointerMoved(40, 10);
        await _vm.PointerReleased(40, 10);
        await Settle();

        Assert.Equal(new Rect(30, 0, 40, 20), _vm.Drawing.Boxes[id].Bounds);
    }

    [Fact]
    public async Task An_agent_deleting_the_box_being_moved_ends_the_move_without_an_error()
    {
        var id = await PlaceBox(0, 0);
        _vm.PointerPressed(10, 10);
        _vm.PointerMoved(30, 10);

        await FromAgent(new DrawCommand.Delete(id));
        _vm.PointerMoved(40, 10);
        await _vm.PointerReleased(40, 10);
        await Settle();

        Assert.Empty(_vm.Drawing.Boxes);
        Assert.Null(_vm.SelectedId);
        Assert.Equal("", _vm.Status);
    }

    [Fact]
    public async Task An_agent_deleting_the_selected_line_clears_the_selection()
    {
        var line = await DrawLine(0, 0, 50, 0);
        Assert.Equal(line, _vm.SelectedId);

        await FromAgent(new DrawCommand.Delete(line));

        Assert.Null(_vm.SelectedId);
        Assert.False(_vm.DeleteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Undoing_the_added_box_clears_the_selection_and_the_text_editor()
    {
        await PlaceBox(0, 0);
        _vm.UndoCommand.Execute(null);
        await Settle();

        Assert.Null(_vm.SelectedId);
        Assert.False(_vm.IsBoxSelected);
        Assert.Equal("", _vm.SelectedText);
    }

    [Fact]
    public async Task An_agent_changing_the_selected_boxs_text_shows_in_the_editor_when_nothing_was_typed()
    {
        var id = await PlaceBox(0, 0);

        await FromAgent(new DrawCommand.SetText(id, "agent text"));

        Assert.Equal("agent text", _vm.SelectedText);
    }

    // --- previews against the committed state ---

    [Fact]
    public async Task A_move_preview_doesnt_reach_the_document_or_the_history()
    {
        var id = await PlaceBox(0, 0);
        _vm.PointerPressed(10, 10);
        _vm.PointerMoved(60, 60);

        Assert.Equal(new Rect(50, 50, 40, 20), _vm.Drawing.Boxes[id].Bounds);
        var shown = await _agent.Send(_id, new DrawCommand.Show());
        Assert.Equal(new Rect(0, 0, 40, 20), shown.Value.Drawing.Boxes[id].Bounds);
        Assert.Single(_vm.History);
    }

    [Fact]
    public async Task Escape_during_a_move_puts_the_box_back_and_sends_nothing()
    {
        var id = await PlaceBox(0, 0);
        _vm.PointerPressed(10, 10);
        _vm.PointerMoved(60, 60);

        _vm.CancelGesture();
        await _vm.PointerReleased(60, 60);
        await Settle();

        Assert.Equal(new Rect(0, 0, 40, 20), _vm.Drawing.Boxes[id].Bounds);
        Assert.Single(_vm.History);
    }

    [Fact]
    public async Task Moving_a_box_away_and_back_adds_no_history_entry()
    {
        var id = await PlaceBox(0, 0);
        _vm.PointerPressed(10, 10);
        _vm.PointerMoved(60, 60);
        _vm.PointerMoved(10, 10);
        await _vm.PointerReleased(10, 10);
        await Settle();

        Assert.Equal(new Rect(0, 0, 40, 20), _vm.Drawing.Boxes[id].Bounds);
        Assert.Single(_vm.History);
        Assert.Equal("", _vm.Status);
    }

    [Fact]
    public async Task A_box_let_go_past_the_limit_lands_where_its_preview_stopped()
    {
        var id = await PlaceBox(0, 0);
        _vm.PointerPressed(10, 10);
        _vm.PointerMoved(Grid.Limit - 40, 10);
        var previewed = _vm.Drawing.Boxes[id].Bounds;

        _vm.PointerMoved(Grid.Limit + 1000, 10);
        Assert.Equal(previewed, _vm.Drawing.Boxes[id].Bounds);
        await _vm.PointerReleased(Grid.Limit + 1000, 10);
        await Settle();

        Assert.Equal(previewed, _vm.Drawing.Boxes[id].Bounds);
    }

    [Fact]
    public async Task Resizing_below_the_minimum_stops_at_the_minimum()
    {
        var id = await PlaceBox(0, 0);
        _vm.PointerPressed(40, 20);
        _vm.PointerMoved(-50, -50);
        await _vm.PointerReleased(-50, -50);
        await Settle();

        Assert.Equal(new Rect(0, 0, Grid.MinSize, Grid.MinSize), _vm.Drawing.Boxes[id].Bounds);
    }

    [Fact]
    public async Task Resizing_an_off_grid_box_by_its_corner_keeps_its_top_left_corner()
    {
        // The grid is virtual, so a file or an agent can give a box an off-grid corner.
        Open(Drawing.Empty.With(Make.Box(1, 3, 3, 40, 20)));
        _vm.PointerPressed(20, 10);
        await _vm.PointerReleased(20, 10);
        Assert.Equal(1, _vm.SelectedId);

        _vm.PointerPressed(43, 23);
        _vm.PointerMoved(63, 33);
        await _vm.PointerReleased(63, 33);
        await Settle();

        var bounds = _vm.Drawing.Boxes[1].Bounds;
        Assert.Equal((3, 3), (bounds.X, bounds.Y));
    }

    [Fact]
    public async Task Moving_an_off_grid_box_puts_it_on_the_grid()
    {
        Open(Drawing.Empty.With(Make.Box(1, 3, 3, 40, 20)));
        _vm.PointerPressed(20, 10);
        _vm.PointerMoved(40, 10);
        await _vm.PointerReleased(40, 10);
        await Settle();

        var bounds = _vm.Drawing.Boxes[1].Bounds;
        Assert.Equal(0, bounds.X % Grid.Step);
        Assert.Equal(0, bounds.Y % Grid.Step);
    }

    // --- line ends ---

    [Fact]
    public async Task Dragging_a_lines_start_past_the_limit_stops_it_at_the_limit()
    {
        var line = await DrawLine(0, 0, 50, 0);
        Assert.Equal(line, _vm.SelectedId);

        _vm.PointerPressed(0, 0);
        _vm.PointerMoved(Grid.Limit + 1000, 0);

        Assert.Equal(new Point(Grid.Limit, 0), _vm.Drawing.Resolve(_vm.Drawing.Lines[line].Start));
        Assert.Equal(new Point(50, 0), _vm.Drawing.Resolve(_vm.Drawing.Lines[line].End));
    }

    [Fact]
    public async Task A_line_dropped_on_a_box_corner_attaches_at_the_corner()
    {
        await PlaceBox(0, 0);
        _vm.SelectToolCommand.Execute(null);
        var line = await DrawLine(-50, -50, 40, 20);

        var end = Assert.IsType<LineEnd.Attached>(_vm.Drawing.Lines[line].End);
        Assert.Equal(new Point(40, 20), _vm.Drawing.Resolve(end));
    }

    [Fact]
    public async Task A_line_end_just_outside_a_box_within_the_grab_distance_attaches()
    {
        await PlaceBox(0, 0);
        // At the default 4 pixels per mm, the grab distance is 1.5 mm, so 41 mm is within it of the right edge at 40 mm.
        var line = await DrawLine(100, 10, 41, 10);

        var end = Assert.IsType<LineEnd.Attached>(_vm.Drawing.Lines[line].End);
        Assert.Equal(Edge.Right, end.Edge);
    }

    [Fact]
    public async Task A_line_end_two_grab_distances_from_a_box_stays_free()
    {
        await PlaceBox(0, 0);
        var line = await DrawLine(100, 10, 43.1, 10);

        Assert.IsType<LineEnd.Free>(_vm.Drawing.Lines[line].End);
    }

    [Fact]
    public async Task A_line_attached_to_a_box_follows_its_resize_and_is_freed_when_it_is_deleted()
    {
        var box = await PlaceBox(0, 0);
        var line = await DrawLine(100, 10, 40, 10);
        _vm.PointerPressed(20, 10);
        await _vm.PointerReleased(20, 10);
        Assert.Equal(box, _vm.SelectedId);

        _vm.PointerPressed(40, 20);
        _vm.PointerMoved(80, 40);
        await _vm.PointerReleased(80, 40);
        await Settle();
        var resized = _vm.Drawing.Resolve(_vm.Drawing.Lines[line].End);
        Assert.Equal(80, resized.X);

        _vm.DeleteCommand.Execute(null);
        await Settle();

        var freed = Assert.IsType<LineEnd.Free>(_vm.Drawing.Lines[line].End);
        Assert.Equal(resized, freed.At);
    }

    [Fact]
    public async Task Escape_while_drawing_a_line_drops_the_ghost_and_sends_nothing()
    {
        _vm.LineToolCommand.Execute(null);
        _vm.PointerPressed(0, 0);
        _vm.PointerMoved(50, 0);
        Assert.NotNull(_vm.GhostLine);

        _vm.CancelGesture();
        await _vm.PointerReleased(50, 0);
        await Settle();

        Assert.Null(_vm.GhostLine);
        Assert.Empty(_vm.Drawing.Lines);
    }

    [Fact]
    public void The_box_tool_shows_a_ghost_on_the_grid_that_switching_tool_removes()
    {
        _vm.BoxToolCommand.Execute(null);
        _vm.PointerMoved(12.4, 7.6);
        Assert.Equal(new Rect(10, 10, DrawingViewModel.NewBoxWidth, DrawingViewModel.NewBoxHeight), _vm.GhostBox!.Bounds);

        _vm.LineToolCommand.Execute(null);

        Assert.Null(_vm.GhostBox);
    }

    [Fact]
    public async Task A_box_placed_past_the_limit_is_refused_with_a_message()
    {
        _vm.BoxToolCommand.Execute(null);
        _vm.PointerPressed(Grid.Limit, Grid.Limit);
        await Settle();

        Assert.Empty(_vm.Drawing.Boxes);
        Assert.NotEqual("", _vm.Status);
        Assert.Equal(Tool.Select, _vm.Tool);
    }

    // --- palette, selection and history ---

    [Fact]
    public async Task Choosing_the_colour_a_selected_box_already_has_sends_nothing()
    {
        await PlaceBox(0, 0);
        _vm.Colour = Colour.White;
        await Settle();

        Assert.Single(_vm.History);
    }

    [Fact]
    public async Task Choosing_a_colour_with_nothing_selected_changes_nothing_in_the_drawing()
    {
        await PlaceBox(0, 0);
        _vm.PointerPressed(-100, -100);
        await _vm.PointerReleased(-100, -100);
        Assert.Null(_vm.SelectedId);

        _vm.Colour = Colour.Green;
        await Settle();

        Assert.Equal(Colour.White, _vm.Drawing.Boxes.Values.Single().Colour);
        Assert.Single(_vm.History);
    }

    [Fact]
    public async Task Clicking_where_a_line_crosses_a_box_selects_the_line()
    {
        await PlaceBox(0, 0);
        var line = await DrawLine(-50, 10, 100, 10);
        _vm.PointerPressed(-100, -100);
        await _vm.PointerReleased(-100, -100);

        _vm.PointerPressed(20, 10);
        await _vm.PointerReleased(20, 10);

        Assert.Equal(line, _vm.SelectedId);
    }

    [Fact]
    public async Task The_topmost_of_two_overlapping_boxes_is_the_one_grabbed()
    {
        await PlaceBox(0, 0);
        var top = await PlaceBox(20, 10);
        _vm.PointerPressed(-100, -100);
        await _vm.PointerReleased(-100, -100);

        _vm.PointerPressed(30, 15);
        await _vm.PointerReleased(30, 15);

        Assert.Equal(top, _vm.SelectedId);
    }

    [Fact]
    public async Task Undone_entries_stay_in_the_history_faded_until_a_new_change_replaces_them()
    {
        await PlaceBox(0, 0);
        await PlaceBox(50, 0);
        _vm.UndoCommand.Execute(null);
        await Settle();
        Assert.Equal([true, false], _vm.History.Select(h => h.Applied));

        await PlaceBox(100, 0);

        Assert.Equal([true, true], _vm.History.Select(h => h.Applied));
        Assert.False(_vm.RedoCommand.CanExecute(null));
    }

    [Fact]
    public async Task Undo_can_execute_follows_the_history()
    {
        Assert.False(_vm.UndoCommand.CanExecute(null));
        var raised = 0;
        _vm.UndoCommand.CanExecuteChanged += (_, _) => raised++;

        await PlaceBox(0, 0);

        Assert.True(_vm.UndoCommand.CanExecute(null));
        Assert.True(raised > 0);
    }

    // --- zoom and pan ---

    [Fact]
    public void Zoom_stops_at_its_limits_and_still_keeps_the_point_under_the_pointer()
    {
        var before = _vm.View.ToPx(30, -20, 800, 600);
        _vm.Zoom(1e6, 30, -20);
        Assert.Equal(ViewTransform.MaxScale, _vm.View.Scale);
        var (x, y) = _vm.View.ToPx(30, -20, 800, 600);
        Assert.Equal(before.X, x, 6);
        Assert.Equal(before.Y, y, 6);

        _vm.Zoom(1e-9, 30, -20);
        Assert.Equal(ViewTransform.MinScale, _vm.View.Scale);
        (x, y) = _vm.View.ToPx(30, -20, 800, 600);
        Assert.Equal(before.X, x, 6);
        Assert.Equal(before.Y, y, 6);
    }

    [Fact]
    public void Zooming_in_and_out_again_returns_to_the_same_view()
    {
        _vm.Zoom(1.15, 13, 7);
        _vm.Zoom(1 / 1.15, 13, 7);

        Assert.Equal(ViewTransform.Default.Scale, _vm.View.Scale, 9);
        Assert.Equal(0, _vm.View.CentreX, 9);
        Assert.Equal(0, _vm.View.CentreY, 9);
    }

    [Fact]
    public async Task A_box_placed_while_zoomed_and_panned_still_snaps_to_the_grid()
    {
        _vm.Zoom(3.7, 11, 11);
        _vm.Pan(1.3, -2.9);
        var (x, y) = _vm.View.ToMm(123, 77, 800, 600);

        var id = await PlaceBox(x, y);

        var bounds = _vm.Drawing.Boxes[id].Bounds;
        Assert.Equal((Grid.Snap(x), Grid.Snap(y)), (bounds.X, bounds.Y));
    }

    [Fact]
    public async Task Zoomed_out_the_grab_distance_grows_in_millimetres()
    {
        var line = await DrawLine(0, 0, 100, 0);
        _vm.PointerPressed(-100, -100);
        await _vm.PointerReleased(-100, -100);
        _vm.Zoom(ViewTransform.MinScale / ViewTransform.Default.Scale, 0, 0);

        // At 0.25 pixels per mm the 6 pixel grab distance is 24 mm.
        _vm.PointerPressed(50, 20);
        await _vm.PointerReleased(50, 20);

        Assert.Equal(line, _vm.SelectedId);
    }

    [Fact]
    public void The_view_maps_pixels_and_millimetres_both_ways()
    {
        var view = new ViewTransform(2.5, -17, 33);

        var (x, y) = view.ToMm(321, 123, 800, 600);
        var (px, py) = view.ToPx(x, y, 800, 600);

        Assert.Equal(321, px, 9);
        Assert.Equal(123, py, 9);
    }

    [Fact]
    public void A_pointer_far_beyond_the_limits_snaps_to_the_limit_rather_than_failing()
    {
        _vm.BoxToolCommand.Execute(null);
        _vm.PointerMoved(1e12, -1e12);

        Assert.Equal(new Rect(Grid.Limit, -Grid.Limit, DrawingViewModel.NewBoxWidth, DrawingViewModel.NewBoxHeight), _vm.GhostBox!.Bounds);
    }
}
