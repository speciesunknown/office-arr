using DrawArr.Commands;
using DrawArr.Core;
using DrawArr.ViewModels;

namespace DrawArr.Tests.ViewModels;

/// <summary>The window's viewmodel against a real workspace and queue, with UI work run straight away.</summary>
public sealed class DrawingViewModelTests : IDisposable
{
    private readonly Workspace _workspace = new();
    private readonly CommandQueue _queue;
    private readonly Requester _window;
    private readonly long _id;
    private readonly DrawingViewModel _vm;
    private int _saves;

    public DrawingViewModelTests()
    {
        _queue = new CommandQueue(_workspace);
        _window = _queue.Join(CommandSource.Window);
        var document = new Document(Drawing.Empty);
        _id = _workspace.Open(document);
        _vm = new DrawingViewModel("test", new DocumentReply(document.Drawing, document.History, 0, null, DocumentChange.None),
            command => _window.Send(_id, command), () => { _saves++; return Task.FromResult(Result.Ok()); }, action => action());
    }

    public void Dispose() => _queue.Dispose();

    private async Task Settle()
    {
        await _vm.WhenIdle();
    }

    private async Task<long> PlaceBox(double x, double y)
    {
        _vm.BoxToolCommand.Execute(null);
        _vm.PointerPressed(x, y);
        await Settle();
        return _vm.SelectedId!.Value;
    }

    [Fact]
    public async Task A_box_taken_from_the_sidebar_lands_on_the_grid_and_is_selected()
    {
        var id = await PlaceBox(12.4, -7.6);

        Assert.Equal(new Rect(10, -10, DrawingViewModel.NewBoxWidth, DrawingViewModel.NewBoxHeight), _vm.Drawing.Boxes[id].Bounds);
        Assert.Equal(Tool.Select, _vm.Tool);
        Assert.True(_vm.IsBoxSelected);
    }

    [Fact]
    public async Task Dragging_a_box_previews_it_and_sends_one_move_when_let_go()
    {
        var id = await PlaceBox(0, 0);
        _vm.PointerPressed(10, 10);
        _vm.PointerMoved(30, 10);
        _vm.PointerMoved(52, 31);
        Assert.Equal(new Rect(40, 20, 40, 20), _vm.Drawing.Boxes[id].Bounds);
        Assert.Single(_vm.History);

        await _vm.PointerReleased(52, 31);

        Assert.Equal(new Rect(40, 20, 40, 20), _vm.Drawing.Boxes[id].Bounds);
        Assert.Equal(["Add box 1 at (0, 0) 40x20", "Move box 1 to 40, 20"], _vm.History.Select(h => h.Description));
    }

    [Fact]
    public async Task Dragging_the_corner_of_a_selected_box_resizes_it()
    {
        var id = await PlaceBox(0, 0);
        _vm.PointerPressed(40, 20);
        _vm.PointerMoved(61, 44);
        await _vm.PointerReleased(61, 44);
        Assert.Equal(new Rect(0, 0, 60, 45), _vm.Drawing.Boxes[id].Bounds);
    }

    [Fact]
    public async Task A_line_drawn_from_one_box_to_another_is_attached_at_both_ends()
    {
        var a = await PlaceBox(0, 0);
        var b = await PlaceBox(100, 0);
        _vm.LineToolCommand.Execute(null);

        _vm.PointerPressed(38, 10);
        _vm.PointerMoved(70, 10);
        Assert.NotNull(_vm.GhostLine);
        await _vm.PointerReleased(102, 10);
        await Settle();

        var line = _vm.Drawing.Lines.Values.Single();
        Assert.Equal(a, ((LineEnd.Attached)line.Start).BoxId);
        Assert.Equal(b, ((LineEnd.Attached)line.End).BoxId);
        Assert.Null(_vm.GhostLine);
        Assert.Equal(line.Id, _vm.SelectedId);
    }

    [Fact]
    public async Task A_click_with_the_line_tool_draws_nothing()
    {
        _vm.LineToolCommand.Execute(null);
        _vm.PointerPressed(5, 5);
        await _vm.PointerReleased(6, 6);
        Assert.Empty(_vm.Drawing.Lines);
    }

    [Fact]
    public async Task Dragging_a_selected_lines_end_frees_it()
    {
        await PlaceBox(0, 0);
        _vm.LineToolCommand.Execute(null);
        _vm.PointerPressed(40, 10);
        await _vm.PointerReleased(100, 10);
        await Settle();
        var line = _vm.SelectedId!.Value;
        // The line tool stays chosen so several lines can be drawn, so dragging an end needs the select tool.
        _vm.SelectToolCommand.Execute(null);

        _vm.PointerPressed(40, 10);
        _vm.PointerMoved(70, 50);
        await _vm.PointerReleased(70, 50);

        Assert.Equal(Make.Free(70, 50), _vm.Drawing.Lines[line].Start);
    }

    [Fact]
    public async Task Choosing_a_colour_colours_the_selection_and_what_comes_next()
    {
        var id = await PlaceBox(0, 0);
        _vm.Colour = Colour.Red;
        await Settle();
        Assert.Equal(Colour.Red, _vm.Drawing.Boxes[id].Colour);

        var next = await PlaceBox(100, 100);
        Assert.Equal(Colour.Red, _vm.Drawing.Boxes[next].Colour);
    }

    [Fact]
    public async Task Edited_text_is_sent_once_when_committed()
    {
        var id = await PlaceBox(0, 0);
        _vm.SelectedText = "hello\r\nworld";
        await _vm.CommitText();
        await _vm.CommitText();
        Assert.Equal("hello\nworld", _vm.Drawing.Boxes[id].Text);
        Assert.Equal(2, _vm.History.Count);
    }

    [Fact]
    public async Task Undo_and_redo_update_the_drawing_and_the_history_list()
    {
        await PlaceBox(0, 0);
        _vm.UndoCommand.Execute(null);
        await Settle();
        Assert.Empty(_vm.Drawing.Boxes);
        Assert.False(_vm.History[0].Applied);
        Assert.True(_vm.RedoCommand.CanExecute(null));
        Assert.Null(_vm.SelectedId);

        _vm.RedoCommand.Execute(null);
        await Settle();
        Assert.Single(_vm.Drawing.Boxes);
        Assert.True(_vm.History[0].Applied);
    }

    [Fact]
    public async Task Changes_from_an_agent_show_up_with_their_source()
    {
        using var agent = _queue.Join(CommandSource.Agent);
        var reply = await agent.Send(_id, new DrawCommand.AddBox(0, 0, 10, 10, Colour.Blue, "from agent"));
        _vm.Apply(reply.Value);

        Assert.Equal("from agent", _vm.Drawing.Boxes[1].Text);
        Assert.Equal("agent", _vm.History[0].Source);
    }

    [Fact]
    public async Task An_older_reply_arriving_late_is_ignored()
    {
        await PlaceBox(0, 0);
        var stale = new DocumentReply(Drawing.Empty, new HistoryView([], 0), 0, null, DocumentChange.None);
        _vm.Apply(stale);
        Assert.Single(_vm.Drawing.Boxes);
    }

    [Fact]
    public async Task A_refused_command_is_reported_in_the_status()
    {
        // An agent deletes the box while the window still has it selected, so the window's delete is refused.
        var id = await PlaceBox(0, 0);
        using var agent = _queue.Join(CommandSource.Agent);
        await agent.Send(_id, new DrawCommand.Delete(id));
        _vm.DeleteCommand.Execute(null);
        await Settle();
        Assert.Contains("no box or line", _vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_removes_the_selection()
    {
        await PlaceBox(0, 0);
        _vm.DeleteCommand.Execute(null);
        await Settle();
        Assert.Empty(_vm.Drawing.Boxes);
        Assert.False(_vm.DeleteCommand.CanExecute(null));
    }

    [Fact]
    public void Zooming_keeps_the_point_under_the_pointer_still()
    {
        _vm.Zoom(2, 50, 20);
        var (x, y) = _vm.View.ToPx(50, 20, 800, 600);
        var (x0, y0) = ViewTransform.Default.ToPx(50, 20, 800, 600);
        Assert.Equal((x0, y0), (x, y));
        Assert.Equal(8, _vm.View.Scale);
    }

    [Fact]
    public void The_view_starts_with_the_origin_in_the_middle()
    {
        Assert.Equal((400.0, 300.0), _vm.View.ToPx(0, 0, 800, 600));
    }

    [Fact]
    public async Task Save_reports_how_it_went()
    {
        await _vm.Save();
        Assert.Equal(1, _saves);
        Assert.Equal("Saved.", _vm.Status);
    }
}
