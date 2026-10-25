using DrawArr.Commands;
using DrawArr.Core;

namespace DrawArr.Tests.Commands;

/// <summary>Edge cases for <see cref="Document"/>, its commands and its history not covered by <see cref="DocumentTests"/>.</summary>
public sealed class DocumentGapTests
{
    private Document _doc = new(Drawing.Empty);

    private DocumentReply Run(DrawCommand command, CommandSource source = CommandSource.Window)
    {
        Result<DocumentReply>? reply = null;
        Assert.Null(Record.Exception(() => reply = _doc.Execute(command, source)));
        Assert.True(reply!.IsSuccess, reply.ToString());
        return reply.Value;
    }

    private Error Fail(DrawCommand command)
    {
        Result<DocumentReply>? reply = null;
        Assert.Null(Record.Exception(() => reply = _doc.Execute(command, CommandSource.Window)));
        Assert.True(reply!.IsFailure, reply.ToString());
        return reply.Error;
    }

    private long AddBox(int x = 0, int y = 0, int width = 40, int height = 20) =>
        Run(new DrawCommand.AddBox(x, y, width, height, Colour.White)).ItemId!.Value;

    private Point StartOf(long lineId) => _doc.Drawing.Resolve(_doc.Drawing.Lines[lineId].Start);

    // --- line ends follow their box through everything ---

    [Fact]
    public void An_end_on_the_right_edge_follows_a_resize_and_its_undo_and_redo()
    {
        var box = AddBox(0, 0, 40, 20);
        var line = Run(new DrawCommand.AddLine(new EndAt.OnBox(box, 60, 10), new EndAt.Free(100, 10), Colour.White)).ItemId!.Value;
        Assert.Equal(new Point(40, 10), StartOf(line));

        Run(new DrawCommand.ResizeBox(box, 0, 0, 80, 20));
        Assert.Equal(new Point(80, 10), StartOf(line));
        Run(new DrawCommand.Undo());
        Assert.Equal(new Point(40, 10), StartOf(line));
        Run(new DrawCommand.Redo());
        Assert.Equal(new Point(80, 10), StartOf(line));
    }

    [Fact]
    public void A_freed_end_is_reattached_by_undo_and_freed_again_by_redo()
    {
        var box = AddBox(0, 0, 40, 20);
        var line = Run(new DrawCommand.AddLine(new EndAt.OnBox(box, 20, -10), new EndAt.Free(20, -50), Colour.White)).ItemId!.Value;
        Run(new DrawCommand.MoveBox(box, 100, 100));
        var before = StartOf(line);

        Run(new DrawCommand.Delete(box));
        Assert.Equal(Make.Free(before.X, before.Y), _doc.Drawing.Lines[line].Start);

        Run(new DrawCommand.Undo());
        Assert.IsType<LineEnd.Attached>(_doc.Drawing.Lines[line].Start);
        Run(new DrawCommand.MoveBox(box, 0, 0));
        Assert.Equal(new Point(20, 0), StartOf(line));
    }

    [Fact]
    public void Redo_of_a_delete_frees_the_end_where_the_box_was_then()
    {
        var box = AddBox(0, 0, 40, 20);
        var line = Run(new DrawCommand.AddLine(new EndAt.OnBox(box, 20, -10), new EndAt.Free(20, -50), Colour.White)).ItemId!.Value;
        Run(new DrawCommand.Delete(box));
        Run(new DrawCommand.Undo());

        Run(new DrawCommand.Redo());

        Assert.Equal(Make.Free(20, 0), _doc.Drawing.Lines[line].Start);
        Assert.False(_doc.Drawing.Boxes.ContainsKey(box));
    }

    [Fact]
    public void Both_ends_on_one_box_are_freed_when_it_goes()
    {
        var box = AddBox(0, 0, 40, 20);
        var line = Run(new DrawCommand.AddLine(new EndAt.OnBox(box, 0, 10), new EndAt.OnBox(box, 40, 10), Colour.White)).ItemId!.Value;

        Run(new DrawCommand.Delete(box));

        var l = _doc.Drawing.Lines[line];
        Assert.Equal((Make.Free(0, 10), Make.Free(40, 10)), (l.Start, l.End));
    }

    [Fact]
    public void Deleting_one_box_leaves_ends_on_another_box_attached()
    {
        var a = AddBox(0, 0, 40, 20);
        var b = AddBox(100, 0, 40, 20);
        var line = Run(new DrawCommand.AddLine(new EndAt.OnBox(a, 40, 10), new EndAt.OnBox(b, 100, 10), Colour.White)).ItemId!.Value;

        Run(new DrawCommand.Delete(a));

        Assert.IsType<LineEnd.Free>(_doc.Drawing.Lines[line].Start);
        Assert.Equal(Make.On(b, Edge.Left, 10), _doc.Drawing.Lines[line].End);
    }

    [Fact]
    public void Deleting_a_line_leaves_its_boxes_alone()
    {
        var box = AddBox();
        var line = Run(new DrawCommand.AddLine(new EndAt.OnBox(box, 0, 10), new EndAt.Free(-50, 10), Colour.White)).ItemId!.Value;
        var before = _doc.Drawing.Boxes[box];

        Run(new DrawCommand.Delete(line));

        Assert.Equal(before, _doc.Drawing.Boxes[box]);
        Assert.Empty(_doc.Drawing.Lines);
    }

    [Fact]
    public void Shrinking_then_growing_a_box_puts_an_end_back_at_its_old_offset()
    {
        // Pins current behaviour: the stored offset is kept and only clamped for display, so it jumps back when the box grows.
        var box = AddBox(0, 0, 100, 20);
        var line = Run(new DrawCommand.AddLine(new EndAt.OnBox(box, 80, -10), new EndAt.Free(80, -50), Colour.White)).ItemId!.Value;

        Run(new DrawCommand.ResizeBox(box, 0, 0, 20, 20));
        Assert.Equal(new Point(20, 0), StartOf(line));
        Run(new DrawCommand.ResizeBox(box, 0, 0, 100, 20));

        Assert.Equal(new Point(80, 0), StartOf(line));
    }

    // --- history order and sources ---

    [Fact]
    public void History_records_each_change_with_its_source_in_order()
    {
        var box = Run(new DrawCommand.AddBox(0, 0, 40, 20, Colour.White), CommandSource.Window).ItemId!.Value;
        Run(new DrawCommand.MoveBox(box, 10, 10), CommandSource.Cli);
        Run(new DrawCommand.SetText(box, "hi"), CommandSource.Agent);

        var entries = _doc.History.Entries;

        Assert.Equal([CommandSource.Window, CommandSource.Cli, CommandSource.Agent], entries.Select(e => e.Source));
        Assert.Equal(["Add box 1 at (0, 0) 40x20", "Move box 1 to 10, 10", "Set text of box 1"], entries.Select(e => e.Description));
        Assert.Equal(3, _doc.History.Applied);
    }

    [Fact]
    public void Undo_and_redo_are_not_entries_themselves()
    {
        AddBox();
        AddBox(50);

        Run(new DrawCommand.Undo(), CommandSource.Cli);
        Run(new DrawCommand.Undo(), CommandSource.Agent);
        Run(new DrawCommand.Redo(), CommandSource.Window);

        Assert.Equal(2, _doc.History.Entries.Length);
        Assert.Equal(1, _doc.History.Applied);
        Assert.True(_doc.History.CanUndo && _doc.History.CanRedo);
    }

    [Fact]
    public void An_undo_from_the_window_takes_back_an_agents_change()
    {
        var box = AddBox();
        Run(new DrawCommand.SetText(box, "from an agent"), CommandSource.Agent);

        Run(new DrawCommand.Undo(), CommandSource.Window);

        Assert.Equal("", _doc.Drawing.Boxes[box].Text);
    }

    [Fact]
    public void A_change_that_does_nothing_after_undo_keeps_redo()
    {
        var box = AddBox();
        Run(new DrawCommand.MoveBox(box, 50, 50));
        Run(new DrawCommand.Undo());

        Assert.Equal(DocumentChange.None, Run(new DrawCommand.MoveBox(box, 0, 0)).Change);
        Assert.Equal(DocumentChange.None, Run(new DrawCommand.SetText(box, "")).Change);

        Assert.True(_doc.History.CanRedo);
        Run(new DrawCommand.Redo());
        Assert.Equal(new Rect(50, 50, 40, 20), _doc.Drawing.Boxes[box].Bounds);
    }

    [Fact]
    public void A_failed_change_after_undo_keeps_redo()
    {
        var box = AddBox();
        Run(new DrawCommand.MoveBox(box, 50, 50));
        Run(new DrawCommand.Undo());

        Fail(new DrawCommand.MoveBox(box, Grid.Limit * 2, 0));
        Fail(new DrawCommand.Delete(999));

        Assert.True(_doc.History.CanRedo);
    }

    [Fact]
    public void The_history_cap_keeps_the_newest_thousand_and_undo_stops_there()
    {
        var box = AddBox();
        for (var i = 1; i <= HistoryView.MaxEntries + 4; i++)
            Run(new DrawCommand.MoveBox(box, i * Grid.Step, 0));

        Assert.Equal(HistoryView.MaxEntries, _doc.History.Entries.Length);
        Assert.Equal("Move box 1 to 25, 0", _doc.History.Entries[0].Description);

        for (var i = 0; i < HistoryView.MaxEntries; i++)
            Run(new DrawCommand.Undo());

        Assert.Equal(ErrorCode.Conflict, Fail(new DrawCommand.Undo()).Code);
        // Undone back to just before the oldest kept step, the fifth move.
        Assert.Equal(new Rect(20, 0, 40, 20), _doc.Drawing.Boxes[box].Bounds);
        Run(new DrawCommand.Redo());
        Assert.Equal(new Rect(25, 0, 40, 20), _doc.Drawing.Boxes[box].Bounds);
    }

    [Fact]
    public void Revision_moves_only_when_the_drawing_changes()
    {
        var box = AddBox();
        var revision = _doc.Revision;

        Run(new DrawCommand.Show());
        Run(new DrawCommand.MoveBox(box, 0, 0));
        Fail(new DrawCommand.Delete(999));
        Fail(new DrawCommand.Redo());

        Assert.Equal(revision, _doc.Revision);
        Run(new DrawCommand.Undo());
        Assert.Equal(revision + 1, _doc.Revision);
    }

    // --- ids ---

    [Fact]
    public void Ids_are_not_reused_after_undo_delete_or_a_dropped_redo()
    {
        var first = AddBox();
        Run(new DrawCommand.Undo());
        var second = AddBox();
        Run(new DrawCommand.Delete(second));
        var third = AddBox();

        Assert.Equal([1L, 2L, 3L], [first, second, third]);
    }

    [Fact]
    public void Redo_brings_back_the_same_id()
    {
        var box = AddBox();
        Run(new DrawCommand.Undo());

        Run(new DrawCommand.Redo());

        Assert.True(_doc.Drawing.Boxes.ContainsKey(box));
    }

    [Fact]
    public void Failed_adds_do_not_use_up_ids()
    {
        Fail(new DrawCommand.AddBox(0, 0, 1, 1, Colour.White));
        Fail(new DrawCommand.AddBox(0, 0, 40, 20, (Colour)0));
        Fail(new DrawCommand.AddLine(new EndAt.OnBox(999, 0, 0), new EndAt.Free(0, 0), Colour.White));

        Assert.Equal(1, AddBox());
    }

    [Fact]
    public void Running_out_of_ids_is_reported_not_wrapped_to_a_negative_id()
    {
        // Matters from milestone 2, when a loaded file can carry any id; the next id must not go past Drawing.MaxId.
        _doc = new Document(Drawing.Empty.With(Make.Box(Drawing.MaxId, 0, 0, 5, 5, Colour.White)));

        Result<DocumentReply>? reply = null;
        Assert.Null(Record.Exception(() => reply = _doc.Execute(new DrawCommand.AddBox(10, 10, 5, 5, Colour.White), CommandSource.Window)));

        Assert.Equal(ErrorCode.Conflict, reply!.Error.Code);
    }

    // --- the item cap ---

    [Fact]
    public void The_item_cap_counts_boxes_and_lines_and_frees_up_after_a_delete()
    {
        var drawing = Drawing.Empty;
        for (var id = 1; id < Drawing.MaxItems; id++)
            drawing = drawing.With(Make.Box(id, 0, 0, 5, 5, Colour.White));
        _doc = new Document(drawing);

        var line = Run(new DrawCommand.AddLine(new EndAt.Free(0, 0), new EndAt.Free(10, 0), Colour.White)).ItemId!.Value;
        Assert.Equal(Drawing.MaxItems, _doc.Drawing.Count);
        Assert.Equal(ErrorCode.Conflict, Fail(new DrawCommand.AddBox(0, 0, 5, 5, Colour.White)).Code);
        Assert.Equal(ErrorCode.Conflict, Fail(new DrawCommand.AddLine(new EndAt.Free(0, 0), new EndAt.Free(5, 0), Colour.White)).Code);

        Run(new DrawCommand.Delete(line));
        Assert.True(Run(new DrawCommand.AddBox(0, 0, 5, 5, Colour.White)).ItemId > line);
    }

    [Fact]
    public void Undo_and_redo_never_take_a_drawing_past_the_item_cap()
    {
        var drawing = Drawing.Empty;
        for (var id = 1; id < Drawing.MaxItems; id++)
            drawing = drawing.With(Make.Box(id, 0, 0, 5, 5, Colour.White));
        _doc = new Document(drawing);

        Run(new DrawCommand.AddBox(0, 0, 5, 5, Colour.White));
        Run(new DrawCommand.Delete(1));
        Run(new DrawCommand.AddBox(0, 0, 5, 5, Colour.White));
        Run(new DrawCommand.Undo());
        Run(new DrawCommand.Undo());
        Run(new DrawCommand.Redo());
        Run(new DrawCommand.Redo());

        Assert.Equal(Drawing.MaxItems, _doc.Drawing.Count);
    }

    // --- limits through commands ---

    [Fact]
    public void Adding_a_box_that_would_reach_past_the_limits_is_an_input_error()
    {
        Assert.Equal(ErrorCode.InvalidInput, Fail(new DrawCommand.AddBox(Grid.Limit, 0, Grid.MaxSize, 5, Colour.White)).Code);
    }

    [Fact]
    public void Moving_a_box_so_it_reaches_past_the_limits_is_an_input_error()
    {
        var box = AddBox(0, 0, 1000, 1000);

        Assert.Equal(ErrorCode.InvalidInput, Fail(new DrawCommand.MoveBox(box, Grid.Limit, Grid.Limit)).Code);
    }

    // --- bad commands ---

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void Colours_outside_the_palette_are_input_errors(int raw)
    {
        var box = AddBox();

        Assert.Equal(ErrorCode.InvalidInput, Fail(new DrawCommand.AddBox(0, 0, 5, 5, (Colour)raw)).Code);
        Assert.Equal(ErrorCode.InvalidInput, Fail(new DrawCommand.SetColour(box, (Colour)raw)).Code);
    }

    [Fact]
    public void A_line_end_that_is_neither_start_nor_end_is_an_input_error()
    {
        var line = Run(new DrawCommand.AddLine(new EndAt.Free(0, 0), new EndAt.Free(10, 0), Colour.White)).ItemId!.Value;

        Assert.Equal(ErrorCode.InvalidInput, Fail(new DrawCommand.MoveLineEnd(line, (LineEndName)0, new EndAt.Free(5, 5))).Code);
        Assert.Equal(ErrorCode.InvalidInput, Fail(new DrawCommand.MoveLineEnd(line, LineEndName.Start, null!)).Code);
    }

    [Fact]
    public void Commands_on_the_wrong_kind_of_item_are_not_found()
    {
        var box = AddBox();
        var line = Run(new DrawCommand.AddLine(new EndAt.Free(0, 0), new EndAt.Free(10, 0), Colour.White)).ItemId!.Value;

        Assert.Equal(ErrorCode.NotFound, Fail(new DrawCommand.MoveBox(line, 0, 0)).Code);
        Assert.Equal(ErrorCode.NotFound, Fail(new DrawCommand.SetText(line, "x")).Code);
        Assert.Equal(ErrorCode.NotFound, Fail(new DrawCommand.MoveLineEnd(box, LineEndName.Start, new EndAt.Free(0, 0))).Code);
        Assert.Equal(ErrorCode.NotFound, Fail(new DrawCommand.AddLine(new EndAt.OnBox(line, 0, 0), new EndAt.Free(0, 0), Colour.White)).Code);
    }

    [Fact]
    public void Text_set_through_a_command_is_normalised_and_an_equal_text_changes_nothing()
    {
        var box = AddBox();

        Run(new DrawCommand.SetText(box, "a\r\nb"));
        var reply = Run(new DrawCommand.SetText(box, "a\rb"));

        Assert.Equal("a\nb", _doc.Drawing.Boxes[box].Text);
        Assert.Equal(DocumentChange.None, reply.Change);
    }

    [Fact]
    public void An_unknown_source_is_a_bug_at_the_document()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _doc.Execute(new DrawCommand.Show(), (CommandSource)0));
    }
}
