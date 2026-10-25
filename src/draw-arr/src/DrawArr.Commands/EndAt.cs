using DrawArr.Core;

namespace DrawArr.Commands;

/// <summary>Where a command asks a line end to go, worked out against the drawing when the command runs.</summary>
public abstract record EndAt
{
    private EndAt() { }

    /// <summary>A free end at a point, snapped to the grid.</summary>
    public sealed record Free(int X, int Y) : EndAt;

    /// <summary>An end on a box's edge, at the place on its outline nearest to a point.</summary>
    public sealed record OnBox(long BoxId, int X, int Y) : EndAt;

    /// <summary>An end on a box's edge, at the place on its outline nearest to the line's other end.</summary>
    public sealed record FacingOther(long BoxId) : EndAt;

    /// <summary>A point this end can stand for when another end faces it: its own point, or the centre of its box.</summary>
    internal Result<Point> Anchor(Drawing drawing)
    {
        switch (this)
        {
            case Free free:
                return Grid.Point(free.X, free.Y);
            case OnBox onBox:
                return Grid.Point(onBox.X, onBox.Y);
            case FacingOther facing:
                var box = drawing.FindBox(facing.BoxId);
                if (box.IsFailure)
                    return Result<Point>.Fail(box.Error);
                var b = box.Value.Bounds;
                return new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
            default:
                throw new InvalidOperationException($"Unhandled line end target {GetType().Name}.");
        }
    }

    /// <summary>The line end this asks for in a drawing, facing a point when it is <see cref="FacingOther"/>, or an error if the point or box is not valid.</summary>
    internal Result<LineEnd> Resolve(Drawing drawing, Point other)
    {
        if (this is FacingOther facing)
        {
            var box = drawing.FindBox(facing.BoxId);
            return box.IsSuccess ? box.Value.Nearest(other) : Result<LineEnd>.Fail(box.Error);
        }
        return Resolve(drawing);
    }

    /// <summary>The line end this asks for in a drawing, or an error if the point or box is not valid.</summary>
    internal Result<LineEnd> Resolve(Drawing drawing)
    {
        switch (this)
        {
            case Free free:
                var at = Grid.Point(free.X, free.Y);
                if (at.IsFailure)
                    return Result<LineEnd>.Fail(at.Error);
                var end = LineEnd.Free.Create(at.Value);
                return end.IsSuccess ? end.Value : Result<LineEnd>.Fail(end.Error);
            case OnBox onBox:
                var near = Grid.Point(onBox.X, onBox.Y);
                if (near.IsFailure)
                    return Result<LineEnd>.Fail(near.Error);
                var box = drawing.FindBox(onBox.BoxId);
                return box.IsSuccess ? box.Value.Nearest(near.Value) : Result<LineEnd>.Fail(box.Error);
            case FacingOther:
                throw new InvalidOperationException("A facing end needs the point it faces.");
            default:
                throw new InvalidOperationException($"Unhandled line end target {GetType().Name}.");
        }
    }
}
