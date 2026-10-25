using System.Collections.Immutable;
using DrawArr.Core;

namespace DrawArr.Commands;

/// <summary>One change in a document's history, as people see it.</summary>
/// <param name="Description">What the change did, such as "Add box 3".</param>
/// <param name="Source">Who sent the change.</param>
public sealed record HistoryEntry(string Description, CommandSource Source);

/// <summary>A document's history at one moment, where the first <see cref="Applied"/> entries are in effect and the rest can be redone.</summary>
public sealed record HistoryView(ImmutableArray<HistoryEntry> Entries, int Applied)
{
    /// <summary>The most changes kept, after which the oldest can no longer be undone.</summary>
    public const int MaxEntries = 1000;

    public bool CanUndo => Applied > 0;
    public bool CanRedo => Applied < Entries.Length;
}

/// <summary>One document's undo/redo stack, which every change goes on whatever sent it.</summary>
/// <remarks>Use it from one thread at a time, which in the running instance is the command queue's worker.</remarks>
internal sealed class History
{
    private readonly List<Step> _steps = [];
    private int _applied;
    // Built only when the history changes, so replies that change nothing don't copy every entry.
    private HistoryView _view = new([], 0);

    public HistoryView View => _view;

    /// <summary>Records a change that has just been made, dropping anything that could have been redone.</summary>
    public void Push(HistoryEntry entry, Drawing before, Drawing after)
    {
        _steps.RemoveRange(_applied, _steps.Count - _applied);
        _steps.Add(new Step(entry, before, after));
        if (_steps.Count > HistoryView.MaxEntries)
            _steps.RemoveAt(0);
        _applied = _steps.Count;
        _view = new HistoryView([.. _steps.Select(s => s.Entry)], _applied);
    }

    /// <summary>The drawing as it was before the last change in effect, or an error if there is none.</summary>
    public Result<Drawing> Undo()
    {
        if (_applied == 0)
            return Result<Drawing>.Fail(ErrorCode.Conflict, "There is nothing to undo.");
        _applied--;
        _view = _view with { Applied = _applied };
        return _steps[_applied].Before;
    }

    /// <summary>The drawing as it was after the next undone change, or an error if there is none.</summary>
    public Result<Drawing> Redo()
    {
        if (_applied == _steps.Count)
            return Result<Drawing>.Fail(ErrorCode.Conflict, "There is nothing to redo.");
        _applied++;
        _view = _view with { Applied = _applied };
        return _steps[_applied - 1].After;
    }

    private sealed record Step(HistoryEntry Entry, Drawing Before, Drawing After);
}
