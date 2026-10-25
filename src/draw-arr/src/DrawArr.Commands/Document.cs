using DrawArr.Core;

namespace DrawArr.Commands;

/// <summary>One open drawing with its own undo/redo stack, changed only by running commands.</summary>
/// <remarks>Use it from one thread at a time, which in the running instance is the command queue's worker.</remarks>
public sealed class Document
{
    private readonly History _history = new();
    // Ids keep counting up through undo, so an id is never given to two different things in one session.
    private long _lastId;

    public Document(Drawing drawing)
    {
        Drawing = drawing ?? throw new ArgumentNullException(nameof(drawing));
        _lastId = drawing.MaxId;
    }

    /// <summary>The drawing as it is now.</summary>
    public Drawing Drawing { get; private set; }

    /// <summary>Counts changes, including undo and redo, so savers can tell whether there is something new.</summary>
    public long Revision { get; private set; }

    public HistoryView History => _history.View;

    /// <summary>Runs a command, putting any change on the undo stack.</summary>
    public Result<DocumentReply> Execute(DrawCommand command, CommandSource source)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(source), source, "Not a command source.");

        switch (command)
        {
            case DrawCommand.Show:
                return Reply(null, DocumentChange.None);
            case DrawCommand.Undo:
                return Move(_history.Undo());
            case DrawCommand.Redo:
                return Move(_history.Redo());
            case DrawCommand.Edit edit:
                var edited = edit.Apply(new EditContext(Drawing, () => ++_lastId));
                if (edited.IsFailure)
                    return Result<DocumentReply>.Fail(edited.Error);
                if (ReferenceEquals(edited.Value.Drawing, Drawing))
                    return Reply(edited.Value.ItemId, DocumentChange.None);
                _history.Push(new HistoryEntry(edited.Value.Description, source), Drawing, edited.Value.Drawing);
                Drawing = edited.Value.Drawing;
                Revision++;
                return Reply(edited.Value.ItemId, DocumentChange.Changed);
            default:
                throw new InvalidOperationException($"Unhandled command {command.GetType().Name}.");
        }
    }

    private Result<DocumentReply> Move(Result<Drawing> to)
    {
        if (to.IsFailure)
            return Result<DocumentReply>.Fail(to.Error);
        Drawing = to.Value;
        Revision++;
        return Reply(null, DocumentChange.Changed);
    }

    private DocumentReply Reply(long? itemId, DocumentChange change) => new(Drawing, History, Revision, itemId, change);
}

/// <summary>Whether a command changed the document.</summary>
public enum DocumentChange
{
    None = 1,
    Changed = 2,
}

/// <summary>What a command left the document as.</summary>
/// <param name="Drawing">The drawing after the command.</param>
/// <param name="History">The history after the command.</param>
/// <param name="Revision">The document's revision after the command.</param>
/// <param name="ItemId">The box or line the command was about, such as the id of a box just added.</param>
/// <param name="Change">Whether the command changed the document.</param>
public sealed record DocumentReply(Drawing Drawing, HistoryView History, long Revision, long? ItemId, DocumentChange Change);
