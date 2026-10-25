namespace DrawArr.Core;

/// <summary>A straight line between two ends, each free or attached to a box's edge, which is always valid because only <see cref="Create"/> makes one.</summary>
public sealed record Line
{
    private Line(long id, LineEnd start, LineEnd end, Colour colour)
    {
        Id = id;
        Start = start;
        End = end;
        Colour = colour;
    }

    /// <summary>The line's id, unique among all boxes and lines in the drawing.</summary>
    public long Id { get; }

    /// <summary>Where the line starts.</summary>
    public LineEnd Start { get; }

    /// <summary>Where the line ends.</summary>
    public LineEnd End { get; }

    /// <summary>The colour the line is drawn in.</summary>
    public Colour Colour { get; }

    /// <summary>A line, or an error if its id or colour breaks the model's rules.</summary>
    public static Result<Line> Create(long id, LineEnd start, LineEnd end, Colour colour)
    {
        if (id <= 0)
            return Result<Line>.Fail(ErrorCode.InvalidInput, $"Ids are positive, not {id}.");
        if (start is null || end is null)
            return Result<Line>.Fail(ErrorCode.InvalidInput, "A line needs a start and an end.");
        if (!Enum.IsDefined(colour))
            return Result<Line>.Fail(ErrorCode.InvalidInput, "That is not one of the palette's colours.");
        return new Line(id, start, end, colour);
    }

    /// <summary>One of the line's two ends.</summary>
    public LineEnd this[LineEndName which] => which switch
    {
        LineEndName.Start => Start,
        LineEndName.End => End,
        _ => throw new InvalidOperationException($"Unhandled line end {which}."),
    };

    /// <summary>This line with one end replaced.</summary>
    public Line With(LineEndName which, LineEnd end)
    {
        ArgumentNullException.ThrowIfNull(end);
        return which switch
        {
            LineEndName.Start => new Line(Id, end, End, Colour),
            LineEndName.End => new Line(Id, Start, end, Colour),
            _ => throw new InvalidOperationException($"Unhandled line end {which}."),
        };
    }
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
    public sealed record Free : LineEnd
    {
        internal Free(Point at) => At = at;

        public Point At { get; }

        /// <summary>A free end, or an error if the point is off the grid or outside the limits.</summary>
        public static Result<Free> Create(Point at) => Grid.IsValid(at)
            ? new Free(at)
            : Result<Free>.Fail(ErrorCode.InvalidInput, $"{at} isn't on the {Grid.Step} mm grid or within the limits.");
    }

    /// <summary>An end on a box's edge, at an offset from the edge's top or left end that may exceed the edge's length, and then sits at its far end.</summary>
    /// <remarks>Keeping the offset when a box shrinks means growing it again puts the end back where it was.</remarks>
    public sealed record Attached : LineEnd
    {
        internal Attached(long boxId, Edge edge, int offset)
        {
            BoxId = boxId;
            Edge = edge;
            Offset = offset;
        }

        public long BoxId { get; }
        public Edge Edge { get; }
        public int Offset { get; }

        /// <summary>An attached end, or an error if the box id, edge or offset is not valid.</summary>
        public static Result<Attached> Create(long boxId, Edge edge, int offset)
        {
            if (boxId <= 0)
                return Result<Attached>.Fail(ErrorCode.InvalidInput, $"Ids are positive, not {boxId}.");
            if (!Enum.IsDefined(edge))
                return Result<Attached>.Fail(ErrorCode.InvalidInput, "An edge is top, right, bottom or left.");
            if (offset < 0 || offset > Grid.MaxSize || offset % Grid.Step != 0)
                return Result<Attached>.Fail(ErrorCode.InvalidInput, $"An offset along an edge must be on the {Grid.Step} mm grid, from 0 to {Grid.MaxSize}.");
            return new Attached(boxId, edge, offset);
        }
    }
}
