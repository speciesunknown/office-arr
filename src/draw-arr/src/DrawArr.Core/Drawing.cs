using System.Collections.Immutable;

namespace DrawArr.Core;

/// <summary>One drawing's boxes and lines, immutable so that undo can keep earlier versions cheaply.</summary>
public sealed class Drawing
{
    /// <summary>The most boxes and lines together that one drawing may hold.</summary>
    public const int MaxItems = 10_000;

    public static Drawing Empty { get; } = new(ImmutableSortedDictionary<long, Box>.Empty, ImmutableSortedDictionary<long, Line>.Empty);

    private Drawing(ImmutableSortedDictionary<long, Box> boxes, ImmutableSortedDictionary<long, Line> lines)
    {
        Boxes = boxes;
        Lines = lines;
    }

    /// <summary>The boxes by id, in the order they are drawn.</summary>
    public ImmutableSortedDictionary<long, Box> Boxes { get; }

    /// <summary>The lines by id, in the order they are drawn.</summary>
    public ImmutableSortedDictionary<long, Line> Lines { get; }

    /// <summary>How many boxes and lines there are.</summary>
    public int Count => Boxes.Count + Lines.Count;

    /// <summary>The highest id in use, or 0 for an empty drawing.</summary>
    public long MaxId => Math.Max(Boxes.IsEmpty ? 0 : Boxes.Keys.Last(), Lines.IsEmpty ? 0 : Lines.Keys.Last());

    /// <summary>The box with an id, or an error if there is none.</summary>
    public Result<Box> FindBox(long id) =>
        Boxes.TryGetValue(id, out var box) ? box : Result<Box>.Fail(ErrorCode.NotFound, $"There is no box with id {id}.");

    /// <summary>The line with an id, or an error if there is none.</summary>
    public Result<Line> FindLine(long id) =>
        Lines.TryGetValue(id, out var line) ? line : Result<Line>.Fail(ErrorCode.NotFound, $"There is no line with id {id}.");

    /// <summary>Where a line end is now, following the box it is attached to.</summary>
    public Point Resolve(LineEnd end) => end switch
    {
        LineEnd.Free free => free.At,
        LineEnd.Attached attached when Boxes.TryGetValue(attached.BoxId, out var box) => box.On(attached.Edge, attached.Offset),
        LineEnd.Attached attached => throw new InvalidOperationException($"Line end is attached to missing box {attached.BoxId}."),
        _ => throw new InvalidOperationException($"Unhandled line end {end?.GetType().Name}."),
    };

    /// <summary>This drawing with a box added or replaced.</summary>
    public Drawing With(Box box)
    {
        ArgumentNullException.ThrowIfNull(box);
        if (Lines.ContainsKey(box.Id))
            throw new InvalidOperationException($"Id {box.Id} is already a line.");
        return new Drawing(Boxes.SetItem(box.Id, box), Lines);
    }

    /// <summary>This drawing with a line added or replaced, whose attached ends must point at boxes that exist.</summary>
    public Drawing With(Line line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (Boxes.ContainsKey(line.Id))
            throw new InvalidOperationException($"Id {line.Id} is already a box.");
        foreach (var end in new[] { line.Start, line.End })
        {
            if (end is LineEnd.Attached attached && !Boxes.ContainsKey(attached.BoxId))
                throw new InvalidOperationException($"Line {line.Id} is attached to missing box {attached.BoxId}.");
        }
        return new Drawing(Boxes, Lines.SetItem(line.Id, line));
    }

    /// <summary>This drawing without a box or line, where line ends that were on a removed box stay where they were as free ends.</summary>
    public Drawing Without(long id)
    {
        if (Lines.ContainsKey(id))
            return new Drawing(Boxes, Lines.Remove(id));
        if (!Boxes.ContainsKey(id))
            return this;

        var lines = Lines;
        foreach (var line in Lines.Values)
        {
            var freed = line with { Start = Free(line.Start, id), End = Free(line.End, id) };
            if (freed != line)
                lines = lines.SetItem(line.Id, freed);
        }
        return new Drawing(Boxes.Remove(id), lines);
    }

    private LineEnd Free(LineEnd end, long boxId) =>
        end is LineEnd.Attached attached && attached.BoxId == boxId ? new LineEnd.Free(Resolve(end)) : end;
}
