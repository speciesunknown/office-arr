namespace DrawArr.ViewModels;

/// <summary>What a press on the canvas does.</summary>
public enum Tool
{
    /// <summary>Select, move and resize boxes, and drag line ends.</summary>
    Select = 1,
    /// <summary>Place a box taken from the sidebar.</summary>
    Box = 2,
    /// <summary>Draw a line by dragging, attaching each end to a box if it starts or ends on one.</summary>
    Line = 3,
}

/// <summary>A line being drawn that isn't in the drawing yet, for the canvas to show.</summary>
public sealed record GhostLine(DrawArr.Core.Point From, DrawArr.Core.Point To, DrawArr.Core.Colour Colour);

/// <summary>A box about to be placed that isn't in the drawing yet, for the canvas to show.</summary>
public sealed record GhostBox(DrawArr.Core.Rect Bounds, DrawArr.Core.Colour Colour);

/// <summary>One change in the history list.</summary>
/// <param name="Description">What the change did.</param>
/// <param name="Source">Who made it, as a word for people.</param>
/// <param name="Applied">Whether the change is in effect, rather than undone and waiting to be redone.</param>
public sealed record HistoryItem(string Description, string Source, bool Applied);
