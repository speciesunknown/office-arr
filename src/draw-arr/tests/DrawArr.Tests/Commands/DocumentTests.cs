using DrawArr.Commands;
using DrawArr.Core;

namespace DrawArr.Tests.Commands;

public sealed class DocumentTests
{
    private readonly Document _doc = new(Drawing.Empty);

    private DocumentReply Run(DrawCommand command, CommandSource source = CommandSource.Window)
    {
        var reply = _doc.Execute(command, source);
        Assert.True(reply.IsSuccess, reply.ToString());
        return reply.Value;
    }

    private Error Fail(DrawCommand command)
    {
        var reply = _doc.Execute(command, CommandSource.Window);
        Assert.True(reply.IsFailure);
        return reply.Error;
    }

    [Fact]
    public void Adding_a_box_snaps_it_and_gives_it_an_id()
    {
        var reply = Run(new DrawCommand.AddBox(1, 2, 39, 21, Colour.Red, "hi"));
        Assert.Equal(1, reply.ItemId);
        Assert.Equal(new Box(1, new Rect(0, 0, 40, 20), Colour.Red, "hi"), reply.Drawing.Boxes[1]);
    }

    [Fact]
    public void Moving_a_box_takes_attached_line_ends_with_it()
    {
        Run(new DrawCommand.AddBox(0, 0, 40, 20, Colour.White));
        Run(new DrawCommand.AddLine(new EndAt.OnBox(1, 50, 10), new EndAt.Free(100, 10), Colour.White));

        var reply = Run(new DrawCommand.MoveBox(1, -100, -100));

        Assert.Equal(new Point(-60, -90), reply.Drawing.Resolve(reply.Drawing.Lines[2].Start));
        Assert.Equal(new Point(100, 10), reply.Drawing.Resolve(reply.Drawing.Lines[2].End));
    }

    [Fact]
    public void Shrinking_a_box_keeps_attached_ends_on_its_edge()
    {
        Run(new DrawCommand.AddBox(0, 0, 40, 20, Colour.White));
        Run(new DrawCommand.AddLine(new EndAt.OnBox(1, 35, -10), new EndAt.Free(100, 10), Colour.White));

        var reply = Run(new DrawCommand.ResizeBox(1, 0, 0, 10, 20));

        Assert.Equal(new Point(10, 0), reply.Drawing.Resolve(reply.Drawing.Lines[2].Start));
    }

    [Fact]
    public void Undo_and_redo_step_through_changes_from_every_source()
    {
        Run(new DrawCommand.AddBox(0, 0, 10, 10, Colour.White), CommandSource.Window);
        Run(new DrawCommand.SetText(1, "agent"), CommandSource.Agent);

        var undone = Run(new DrawCommand.Undo());
        Assert.Equal("", undone.Drawing.Boxes[1].Text);
        Assert.Equal(1, undone.History.Applied);
        Assert.True(undone.History.CanRedo);

        var redone = Run(new DrawCommand.Redo());
        Assert.Equal("agent", redone.Drawing.Boxes[1].Text);
        Assert.Equal(
            [new HistoryEntry("Add box 1", CommandSource.Window), new HistoryEntry("Set text of box 1", CommandSource.Agent)],
            redone.History.Entries.AsEnumerable());
    }

    [Fact]
    public void A_new_change_after_undo_drops_what_could_be_redone()
    {
        Run(new DrawCommand.AddBox(0, 0, 10, 10, Colour.White));
        Run(new DrawCommand.MoveBox(1, 50, 50));
        Run(new DrawCommand.Undo());

        var reply = Run(new DrawCommand.SetColour(1, Colour.Blue), CommandSource.Cli);

        Assert.False(reply.History.CanRedo);
        Assert.Equal(["Add box 1", "Colour box 1 blue"], reply.History.Entries.Select(e => e.Description));
    }

    [Fact]
    public void Nothing_to_undo_or_redo_is_a_conflict_not_a_crash()
    {
        Assert.Equal(ErrorCode.Conflict, Fail(new DrawCommand.Undo()).Code);
        Assert.Equal(ErrorCode.Conflict, Fail(new DrawCommand.Redo()).Code);
    }

    [Fact]
    public void An_edit_that_changes_nothing_is_not_in_the_history()
    {
        Run(new DrawCommand.AddBox(0, 0, 10, 10, Colour.White));
        var reply = Run(new DrawCommand.MoveBox(1, 1, -1));
        Assert.Equal(DocumentChange.None, reply.Change);
        Assert.Single(reply.History.Entries);
    }

    [Fact]
    public void Ids_are_not_given_out_again_after_undo()
    {
        Run(new DrawCommand.AddBox(0, 0, 10, 10, Colour.White));
        Run(new DrawCommand.Undo());
        Assert.Equal(2, Run(new DrawCommand.AddBox(0, 0, 10, 10, Colour.White)).ItemId);
    }

    [Fact]
    public void A_loaded_drawing_carries_on_counting_ids_from_its_highest()
    {
        var doc = new Document(Drawing.Empty.With(new Box(41, new Rect(0, 0, 5, 5), Colour.White, "")));
        Assert.Equal(42, doc.Execute(new DrawCommand.AddBox(0, 0, 5, 5, Colour.White), CommandSource.Cli).Value.ItemId);
    }

    [Fact]
    public void Deleting_a_box_can_be_undone_with_its_lines_reattached()
    {
        Run(new DrawCommand.AddBox(0, 0, 40, 20, Colour.White));
        Run(new DrawCommand.AddLine(new EndAt.OnBox(1, 0, 10), new EndAt.Free(-50, 10), Colour.Red));
        Run(new DrawCommand.Delete(1));
        Assert.IsType<LineEnd.Free>(_doc.Drawing.Lines[2].Start);

        var reply = Run(new DrawCommand.Undo());
        Assert.Equal(new LineEnd.Attached(1, Edge.Left, 10), reply.Drawing.Lines[2].Start);
    }

    [Fact]
    public void A_line_end_can_be_freed_and_reattached()
    {
        Run(new DrawCommand.AddBox(0, 0, 40, 20, Colour.White));
        Run(new DrawCommand.AddLine(new EndAt.Free(-50, 0), new EndAt.Free(-50, 50), Colour.Red));

        var attached = Run(new DrawCommand.MoveLineEnd(2, LineEndName.End, new EndAt.OnBox(1, 20, 30)));
        Assert.Equal(new LineEnd.Attached(1, Edge.Bottom, 20), attached.Drawing.Lines[2].End);
        Assert.Equal("Move end of line 2 to box 1", attached.History.Entries[^1].Description);
    }

    [Theory]
    [MemberData(nameof(BadCommands))]
    public void Bad_commands_are_reported_and_change_nothing(DrawCommand command, ErrorCode code)
    {
        Run(new DrawCommand.AddBox(0, 0, 40, 20, Colour.White));
        Run(new DrawCommand.AddLine(new EndAt.Free(0, 50), new EndAt.Free(50, 50), Colour.Red));
        var before = _doc.Drawing;

        Assert.Equal(code, Fail(command).Code);
        Assert.Same(before, _doc.Drawing);
        Assert.Equal(2, _doc.History.Entries.Length);
    }

    public static TheoryData<DrawCommand, ErrorCode> BadCommands() => new()
    {
        { new DrawCommand.MoveBox(9, 0, 0), ErrorCode.NotFound },
        { new DrawCommand.MoveBox(2, 0, 0), ErrorCode.NotFound },
        { new DrawCommand.MoveBox(0, 0, 0), ErrorCode.InvalidInput },
        { new DrawCommand.MoveBox(1, Grid.Limit + 1, 0), ErrorCode.InvalidInput },
        { new DrawCommand.AddBox(0, 0, 0, 10, Colour.White), ErrorCode.InvalidInput },
        { new DrawCommand.AddBox(0, 0, 10, 10, (Colour)0), ErrorCode.InvalidInput },
        { new DrawCommand.AddBox(0, 0, 10, 10, Colour.White, "\0"), ErrorCode.InvalidInput },
        { new DrawCommand.SetText(1, null!), ErrorCode.InvalidInput },
        { new DrawCommand.SetColour(9, Colour.Red), ErrorCode.NotFound },
        { new DrawCommand.SetColour(1, (Colour)99), ErrorCode.InvalidInput },
        { new DrawCommand.AddLine(new EndAt.OnBox(9, 0, 0), new EndAt.Free(0, 0), Colour.Red), ErrorCode.NotFound },
        { new DrawCommand.AddLine(null!, new EndAt.Free(0, 0), Colour.Red), ErrorCode.InvalidInput },
        { new DrawCommand.MoveLineEnd(2, (LineEndName)0, new EndAt.Free(0, 0)), ErrorCode.InvalidInput },
        { new DrawCommand.MoveLineEnd(1, LineEndName.Start, new EndAt.Free(0, 0)), ErrorCode.NotFound },
        { new DrawCommand.Delete(9), ErrorCode.NotFound },
        { new DrawCommand.Delete(-1), ErrorCode.InvalidInput },
    };

    [Fact]
    public void The_history_keeps_only_the_newest_changes()
    {
        Run(new DrawCommand.AddBox(0, 0, 10, 10, Colour.White));
        for (var i = 1; i <= HistoryView.MaxEntries; i++)
            Run(new DrawCommand.MoveBox(1, i * 5, 0));

        var history = _doc.History;
        Assert.Equal(HistoryView.MaxEntries, history.Entries.Length);
        Assert.Equal("Move box 1 to 5, 0", history.Entries[0].Description);
    }

    [Fact]
    public void Every_change_moves_the_revision_on()
    {
        Assert.Equal(1, Run(new DrawCommand.AddBox(0, 0, 10, 10, Colour.White)).Revision);
        Assert.Equal(1, Run(new DrawCommand.Show()).Revision);
        Assert.Equal(2, Run(new DrawCommand.Undo()).Revision);
        Assert.Equal(3, Run(new DrawCommand.Redo()).Revision);
    }
}
