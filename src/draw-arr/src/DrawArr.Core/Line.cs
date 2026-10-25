namespace DrawArr.Core;

/// <summary>A straight line between two ends, each free or attached to a box's edge.</summary>
/// <param name="Id">The line's id, unique among all boxes and lines in the drawing.</param>
/// <param name="Start">Where the line starts.</param>
/// <param name="End">Where the line ends.</param>
/// <param name="Colour">The colour the line is drawn in.</param>
public sealed record Line(long Id, LineEnd Start, LineEnd End, Colour Colour)
{
    /// <summary>One of the line's two ends.</summary>
    public LineEnd this[LineEndName which] => which switch
    {
        LineEndName.Start => Start,
        LineEndName.End => End,
        _ => throw new InvalidOperationException($"Unhandled line end {which}."),
    };

    /// <summary>This line with one end replaced.</summary>
    public Line With(LineEndName which, LineEnd end) => which switch
    {
        LineEndName.Start => this with { Start = end },
        LineEndName.End => this with { End = end },
        _ => throw new InvalidOperationException($"Unhandled line end {which}."),
    };
}

/// <summary>Which end of a line, stored by number in files and sent over IPC.</summary>
public enum LineEndName
{
    Start = 1,
    End = 2,
}

/// <summary>Where one end of a line is: a free point, or a place on a box's edge that moves with the box.</summary>
public abstract record LineEnd
{
    private LineEnd() { }

    /// <summary>An end at a fixed point on the grid.</summary>
    public sealed record Free(Point At) : LineEnd;

    /// <summary>An end on a box's edge, at an offset in millimetres from the edge's top or left end.</summary>
    public sealed record Attached(long BoxId, Edge Edge, int Offset) : LineEnd;
}
