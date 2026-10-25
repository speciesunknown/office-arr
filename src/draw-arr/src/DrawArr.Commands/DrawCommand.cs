using System.Globalization;
using DrawArr.Core;

namespace DrawArr.Commands;

/// <summary>Everything the window, the CLI and agents can ask of one document.</summary>
public abstract record DrawCommand
{
    private DrawCommand() { }

    /// <summary>Shows the drawing and its history without changing anything.</summary>
    public sealed record Show : DrawCommand;

    /// <summary>Takes back the last change in effect, whoever made it.</summary>
    public sealed record Undo : DrawCommand;

    /// <summary>Makes the last undone change again.</summary>
    public sealed record Redo : DrawCommand;

    /// <summary>A change to the drawing, which goes on the undo stack.</summary>
    public abstract record Edit : DrawCommand
    {
        private protected Edit() { }

        /// <summary>Works out the changed drawing, called only by <see cref="Document"/> on the queue's worker.</summary>
        internal abstract Result<Edited> Apply(EditContext run);
    }

    /// <summary>Adds a box, snapped to the grid.</summary>
    public sealed record AddBox(int X, int Y, int Width, int Height, Colour Colour, string Text = "") : Edit
    {
        internal override Result<Edited> Apply(EditContext run)
        {
            var bounds = Grid.Rect(X, Y, Width, Height);
            if (bounds.IsFailure)
                return Result<Edited>.Fail(bounds.Error);
            var colour = EditContext.Check(Colour);
            if (colour.IsFailure)
                return Result<Edited>.Fail(colour.Error);
            var text = BoxText.Validate(Text);
            if (text.IsFailure)
                return Result<Edited>.Fail(text.Error);
            var room = run.CheckRoom();
            if (room.IsFailure)
                return Result<Edited>.Fail(room.Error);

            var id = run.NewId();
            if (id.IsFailure)
                return Result<Edited>.Fail(id.Error);
            var box = Box.Create(id.Value, bounds.Value, Colour, text.Value);
            if (box.IsFailure)
                return Result<Edited>.Fail(box.Error);
            return new Edited(run.Drawing.With(box.Value), box.Value.Id, $"Add box {box.Value.Id} at {bounds.Value}");
        }
    }

    /// <summary>Moves a box so its top-left corner is at a point, keeping its size, with attached line ends following it.</summary>
    public sealed record MoveBox(long Id, int X, int Y) : Edit
    {
        internal override Result<Edited> Apply(EditContext run)
        {
            var box = run.FindBox(Id);
            if (box.IsFailure)
                return Result<Edited>.Fail(box.Error);
            var bounds = Grid.Rect(X, Y, box.Value.Bounds.Width, box.Value.Bounds.Height);
            if (bounds.IsFailure)
                return Result<Edited>.Fail(bounds.Error);
            var b = box.Value;
            return run.Change(Box.Create(b.Id, bounds.Value, b.Colour, b.Text),
                string.Create(CultureInfo.InvariantCulture, $"Move box {Id} to {bounds.Value.X}, {bounds.Value.Y}"));
        }
    }

    /// <summary>Gives a box new bounds, with attached line ends staying on its edges.</summary>
    public sealed record ResizeBox(long Id, int X, int Y, int Width, int Height) : Edit
    {
        internal override Result<Edited> Apply(EditContext run)
        {
            var box = run.FindBox(Id);
            if (box.IsFailure)
                return Result<Edited>.Fail(box.Error);
            var bounds = Grid.Rect(X, Y, Width, Height);
            if (bounds.IsFailure)
                return Result<Edited>.Fail(bounds.Error);
            var b = box.Value;
            return run.Change(Box.Create(b.Id, bounds.Value, b.Colour, b.Text), $"Resize box {Id} to {bounds.Value}");
        }
    }

    /// <summary>Sets the text shown in a box, where empty text removes it.</summary>
    public sealed record SetText(long Id, string Text) : Edit
    {
        internal override Result<Edited> Apply(EditContext run)
        {
            var box = run.FindBox(Id);
            if (box.IsFailure)
                return Result<Edited>.Fail(box.Error);
            var text = BoxText.Validate(Text);
            if (text.IsFailure)
                return Result<Edited>.Fail(text.Error);
            var b = box.Value;
            return run.Change(Box.Create(b.Id, b.Bounds, b.Colour, text.Value), text.Value.Length == 0 ? $"Clear text of box {Id}" : $"Set text of box {Id}");
        }
    }

    /// <summary>Sets the colour of a box or a line.</summary>
    public sealed record SetColour(long Id, Colour Colour) : Edit
    {
        internal override Result<Edited> Apply(EditContext run)
        {
            var colour = EditContext.Check(Colour);
            if (colour.IsFailure)
                return Result<Edited>.Fail(colour.Error);
            var description = $"Colour {{0}} {Id} {Colour.ToString().ToLowerInvariant()}";
            if (run.Drawing.Boxes.TryGetValue(Id, out var box))
                return run.Change(Box.Create(box.Id, box.Bounds, Colour, box.Text), string.Format(CultureInfo.InvariantCulture, description, "box"));
            var line = run.FindLine(Id);
            if (line.IsFailure)
                return Result<Edited>.Fail(ErrorCode.NotFound, $"There is no box or line with id {Id}.");
            var l = line.Value;
            return run.Change(Line.Create(l.Id, l.Start, l.End, Colour), string.Format(CultureInfo.InvariantCulture, description, "line"));
        }
    }

    /// <summary>Adds a line between two ends.</summary>
    public sealed record AddLine(EndAt Start, EndAt End, Colour Colour) : Edit
    {
        internal override Result<Edited> Apply(EditContext run)
        {
            var colour = EditContext.Check(Colour);
            if (colour.IsFailure)
                return Result<Edited>.Fail(colour.Error);
            var start = run.Resolve(Start);
            if (start.IsFailure)
                return Result<Edited>.Fail(start.Error);
            var end = run.Resolve(End);
            if (end.IsFailure)
                return Result<Edited>.Fail(end.Error);
            var room = run.CheckRoom();
            if (room.IsFailure)
                return Result<Edited>.Fail(room.Error);

            var id = run.NewId();
            if (id.IsFailure)
                return Result<Edited>.Fail(id.Error);
            var line = Line.Create(id.Value, start.Value, end.Value, Colour);
            if (line.IsFailure)
                return Result<Edited>.Fail(line.Error);
            return new Edited(run.Drawing.With(line.Value), line.Value.Id, $"Add line {line.Value.Id}");
        }
    }

    /// <summary>Moves one end of a line, freeing it or attaching it to a box.</summary>
    public sealed record MoveLineEnd(long Id, LineEndName Which, EndAt To) : Edit
    {
        internal override Result<Edited> Apply(EditContext run)
        {
            if (!Enum.IsDefined(Which))
                return Result<Edited>.Fail(ErrorCode.InvalidInput, "A line end is either its start or its end.");
            var line = run.FindLine(Id);
            if (line.IsFailure)
                return Result<Edited>.Fail(line.Error);
            var to = run.Resolve(To);
            if (to.IsFailure)
                return Result<Edited>.Fail(to.Error);
            var where = to.Value is LineEnd.Attached attached ? $"to box {attached.BoxId}" : $"to {run.Drawing.Resolve(to.Value)}";
            return run.Change(Result<Line>.Ok(line.Value.With(Which, to.Value)), $"Move {Which.ToString().ToLowerInvariant()} of line {Id} {where}");
        }
    }

    /// <summary>Deletes a box or a line, where lines attached to a deleted box keep their ends where they were.</summary>
    public sealed record Delete(long Id) : Edit
    {
        internal override Result<Edited> Apply(EditContext run)
        {
            if (Id <= 0)
                return Result<Edited>.Fail(ErrorCode.InvalidInput, $"Ids are positive, not {Id}.");
            var kind = run.Drawing.Boxes.ContainsKey(Id) ? "box" : run.Drawing.Lines.ContainsKey(Id) ? "line" : null;
            if (kind is null)
                return Result<Edited>.Fail(ErrorCode.NotFound, $"There is no box or line with id {Id}.");
            return new Edited(run.Drawing.Without(Id), Id, $"Delete {kind} {Id}");
        }
    }
}

/// <summary>What an edit produced.</summary>
/// <param name="Drawing">The drawing after the edit, which is the same instance as before when nothing changed.</param>
/// <param name="ItemId">The box or line the edit was about.</param>
/// <param name="Description">What the edit did, for the history.</param>
internal sealed record Edited(Drawing Drawing, long ItemId, string Description);

/// <summary>The drawing an edit runs against, with the steps edits share.</summary>
internal sealed class EditContext(Drawing drawing, Func<Result<long>> newId)
{
    public Drawing Drawing { get; } = drawing;

    /// <summary>An id no box or line has had in this session, or an error once they have run out.</summary>
    public Result<long> NewId() => newId();

    public Result<Box> FindBox(long id) =>
        id <= 0 ? Result<Box>.Fail(ErrorCode.InvalidInput, $"Ids are positive, not {id}.") : Drawing.FindBox(id);

    public Result<Line> FindLine(long id) =>
        id <= 0 ? Result<Line>.Fail(ErrorCode.InvalidInput, $"Ids are positive, not {id}.") : Drawing.FindLine(id);

    public Result<LineEnd> Resolve(EndAt? at) =>
        at is null ? Result<LineEnd>.Fail(ErrorCode.InvalidInput, "No line end given.") : at.Resolve(Drawing);

    /// <summary>An error if the drawing can't take another box or line.</summary>
    public Result CheckRoom() => Drawing.Count < Drawing.MaxItems
        ? Result.Ok()
        : Result.Fail(ErrorCode.Conflict, $"A drawing can hold at most {Drawing.MaxItems} boxes and lines.");

    public static Result Check(Colour colour) => Enum.IsDefined(colour)
        ? Result.Ok()
        : Result.Fail(ErrorCode.InvalidInput, "That is not one of the palette's colours.");

    /// <summary>The edit that replaces a box, which changes nothing when the box is the same.</summary>
    public Result<Edited> Change(Result<Box> box, string description)
    {
        if (box.IsFailure)
            return Result<Edited>.Fail(box.Error);
        var b = box.Value;
        return new Edited(b == Drawing.Boxes[b.Id] ? Drawing : Drawing.With(b), b.Id, description);
    }

    /// <summary>The edit that replaces a line, which changes nothing when the line is the same.</summary>
    public Result<Edited> Change(Result<Line> line, string description)
    {
        if (line.IsFailure)
            return Result<Edited>.Fail(line.Error);
        var l = line.Value;
        return new Edited(l == Drawing.Lines[l.Id] ? Drawing : Drawing.With(l), l.Id, description);
    }
}
