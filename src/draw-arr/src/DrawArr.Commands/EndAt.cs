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

    /// <summary>The line end this asks for in a drawing, or an error if the point or box is not valid.</summary>
    internal Result<LineEnd> Resolve(Drawing drawing)
    {
        switch (this)
        {
            case Free free:
                var at = Grid.Point(free.X, free.Y);
                return at.IsSuccess ? new LineEnd.Free(at.Value) : Result<LineEnd>.Fail(at.Error);
            case OnBox onBox:
                var near = Grid.Point(onBox.X, onBox.Y);
                if (near.IsFailure)
                    return Result<LineEnd>.Fail(near.Error);
                var box = drawing.FindBox(onBox.BoxId);
                return box.IsSuccess ? box.Value.Nearest(near.Value) : Result<LineEnd>.Fail(box.Error);
            default:
                throw new InvalidOperationException($"Unhandled line end target {GetType().Name}.");
        }
    }
}
