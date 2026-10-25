namespace DrawArr.Core;

/// <summary>A box on the drawing, with optional text centred in it.</summary>
/// <param name="Id">The box's id, unique among all boxes and lines in the drawing.</param>
/// <param name="Bounds">Where the box is, on the grid.</param>
/// <param name="Colour">The colour of the box's outline and text.</param>
/// <param name="Text">The text shown in the box, which is empty for none.</param>
public sealed record Box(long Id, Rect Bounds, Colour Colour, string Text)
{
    /// <summary>The length of one of the box's edges in millimetres.</summary>
    public int Length(Edge edge) => edge switch
    {
        Edge.Top or Edge.Bottom => Bounds.Width,
        Edge.Left or Edge.Right => Bounds.Height,
        _ => throw new InvalidOperationException($"Unhandled edge {edge}."),
    };

    /// <summary>The point on an edge at an offset from its top or left end, clamped to the edge.</summary>
    public Point On(Edge edge, int offset)
    {
        var along = Math.Clamp(offset, 0, Length(edge));
        var b = Bounds;
        return edge switch
        {
            Edge.Top => new Point(b.X + along, b.Y),
            Edge.Bottom => new Point(b.X + along, b.Bottom),
            Edge.Left => new Point(b.X, b.Y + along),
            Edge.Right => new Point(b.Right, b.Y + along),
            _ => throw new InvalidOperationException($"Unhandled edge {edge}."),
        };
    }

    /// <summary>The place on this box's outline nearest to a point, snapped to the grid.</summary>
    public LineEnd.Attached Nearest(Point near)
    {
        var best = Edge.Top;
        var bestOffset = 0;
        var bestDistance = long.MaxValue;
        foreach (var edge in Edges)
        {
            var offset = Grid.Snap(Math.Clamp(Along(edge, near), 0, Length(edge)));
            var at = On(edge, offset);
            var distance = (long)(at.X - near.X) * (at.X - near.X) + (long)(at.Y - near.Y) * (at.Y - near.Y);
            if (distance < bestDistance)
                (best, bestOffset, bestDistance) = (edge, offset, distance);
        }
        return new LineEnd.Attached(Id, best, bestOffset);
    }

    private static readonly Edge[] Edges = [Edge.Top, Edge.Right, Edge.Bottom, Edge.Left];

    private int Along(Edge edge, Point p) => edge is Edge.Top or Edge.Bottom ? p.X - Bounds.X : p.Y - Bounds.Y;
}

/// <summary>One of a box's four edges, stored by number in files.</summary>
public enum Edge
{
    Top = 1,
    Right = 2,
    Bottom = 3,
    Left = 4,
}

/// <summary>Rules for the text in a box.</summary>
public static class BoxText
{
    /// <summary>The most characters a box's text may have.</summary>
    public const int MaxLength = 1000;

    /// <summary>The text with Windows and old Mac line breaks made into newlines, or an error if it is too long or holds control characters.</summary>
    public static Result<string> Validate(string? text)
    {
        if (text is null)
            return Result<string>.Fail(ErrorCode.InvalidInput, "No text given.");
        var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalised.Length > MaxLength)
            return Result<string>.Fail(ErrorCode.InvalidInput, $"A box's text can be at most {MaxLength} characters.");
        foreach (var c in normalised)
        {
            if (char.IsControl(c) && c != '\n')
                return Result<string>.Fail(ErrorCode.InvalidInput, "A box's text can't hold control characters other than line breaks.");
        }
        return normalised;
    }
}
