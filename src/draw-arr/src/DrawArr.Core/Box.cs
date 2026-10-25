namespace DrawArr.Core;

/// <summary>A box on the drawing, with optional text centred in it, which is always valid because only <see cref="Create"/> makes one.</summary>
public sealed record Box
{
    private Box(long id, Rect bounds, Colour colour, string text)
    {
        Id = id;
        Bounds = bounds;
        Colour = colour;
        Text = text;
    }

    /// <summary>The box's id, unique among all boxes and lines in the drawing.</summary>
    public long Id { get; }

    /// <summary>Where the box is, on the grid.</summary>
    public Rect Bounds { get; }

    /// <summary>The colour of the box's outline and text.</summary>
    public Colour Colour { get; }

    /// <summary>The text shown in the box, which is empty for none.</summary>
    public string Text { get; }

    /// <summary>A box, or an error if any part of it breaks the model's rules.</summary>
    /// <param name="id">A positive id.</param>
    /// <param name="bounds">Bounds already on the grid, as <see cref="Grid.Rect"/> makes them.</param>
    /// <param name="colour">A colour from the palette.</param>
    /// <param name="text">Text that <see cref="BoxText.Validate"/> leaves unchanged.</param>
    /// <returns>The box, or an input error saying which part is wrong.</returns>
    public static Result<Box> Create(long id, Rect bounds, Colour colour, string text)
    {
        if (id <= 0 || id > Drawing.MaxId)
            return Result<Box>.Fail(ErrorCode.InvalidInput, $"Ids run from 1 to {Drawing.MaxId}, not {id}.");
        if (!Grid.IsValid(bounds))
            return Result<Box>.Fail(ErrorCode.InvalidInput, $"Box {id}'s bounds {bounds} aren't on the {Grid.Step} mm grid or within the limits.");
        if (!Enum.IsDefined(colour))
            return Result<Box>.Fail(ErrorCode.InvalidInput, "That is not one of the palette's colours.");
        var checkedText = BoxText.Validate(text);
        if (checkedText.IsFailure)
            return Result<Box>.Fail(checkedText.Error);
        if (checkedText.Value != text)
            return Result<Box>.Fail(ErrorCode.InvalidInput, "A box's line breaks must already be plain newlines.");
        return new Box(id, bounds, colour, text);
    }

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

    /// <summary>The text with every kind of line break made into a newline, or an error if it is too long or holds characters that can't be shown or saved.</summary>
    public static Result<string> Validate(string? text)
    {
        if (text is null)
            return Result<string>.Fail(ErrorCode.InvalidInput, "No text given.");
        var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Replace('\u2028', '\n').Replace('\u2029', '\n');
        if (normalised.Length > MaxLength)
            return Result<string>.Fail(ErrorCode.InvalidInput, $"A box's text can be at most {MaxLength} characters.");
        for (var i = 0; i < normalised.Length; i++)
        {
            var c = normalised[i];
            if (char.IsControl(c) && c != '\n')
                return Result<string>.Fail(ErrorCode.InvalidInput, "A box's text can't hold control characters other than line breaks.");
            if (char.IsHighSurrogate(c) && i + 1 < normalised.Length && char.IsLowSurrogate(normalised[i + 1]))
            {
                i++;
                continue;
            }
            // A lone surrogate or U+FFFE/U+FFFF is not a character at all and can't be saved in a file.
            if (char.IsSurrogate(c) || c is '\uFFFE' or '\uFFFF')
                return Result<string>.Fail(ErrorCode.InvalidInput, "A box's text holds a broken character.");
        }
        return normalised;
    }
}
