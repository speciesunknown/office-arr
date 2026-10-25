using DrawArr.Core;

namespace DrawArr.Files;

/// <summary>Saves one document a short while after it last changed, so a burst of edits is written once.</summary>
public sealed class Autosaver : IDisposable
{
    /// <summary>How long after the last change a save happens.</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(2);

    private readonly Func<Drawing, Result> _save;
    private readonly TimeSpan _delay;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();
    // Held for the whole of a save, so two saves never write the same file at once.
    private readonly Lock _saving = new();
    private Drawing? _pending;
    private long _pendingRevision;
    private long _newestRevision;
    private int _failures;

    /// <summary>How long to wait before trying again after one, two, three or more failed saves in a row.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    /// <summary>Raised after each save attempt on whichever thread made it, so handlers must post to the UI thread rather than wait on it.</summary>
    public event EventHandler<AutosaveEventArgs>? Saved;

    /// <param name="clock">Where the delay's timer comes from, so tests can move time on.</param>
    /// <param name="delay">How long after the last change to save.</param>
    /// <param name="save">Writes a drawing to the document's file.</param>
    public Autosaver(TimeProvider clock, TimeSpan delay, Func<Drawing, Result> save)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _delay = delay;
        _timer = clock.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Notes a new version of the drawing and restarts the delay, ignoring versions older than one already seen.</summary>
    public void Changed(Drawing drawing, long revision)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        lock (_gate)
        {
            if (revision <= _newestRevision)
                return;
            _newestRevision = revision;
            _pending = drawing;
            _pendingRevision = revision;
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Whether there is a change not yet saved, which is false while a save is running, so call <see cref="Flush"/> rather than trusting this on close.</summary>
    public bool HasUnsaved
    {
        get
        {
            lock (_gate)
                return _pending is not null;
        }
    }

    /// <summary>Saves the newest version now if it hasn't been saved, as the Save menu and closing a window do.</summary>
    public Result Flush()
    {
        long revision;
        Result saved;
        lock (_saving)
        {
            Drawing? drawing;
            lock (_gate)
            {
                (drawing, revision) = (_pending, _pendingRevision);
                _pending = null;
            }
            if (drawing is null)
                return Result.Ok();

            saved = _save(drawing);
            lock (_gate)
            {
                if (saved.IsSuccess)
                {
                    _failures = 0;
                }
                else
                {
                    // Kept and tried again after a back-off, unless something newer has arrived meanwhile and set its own timer.
                    _failures++;
                    if (_pending is null)
                    {
                        (_pending, _pendingRevision) = (drawing, revision);
                        _timer.Change(RetryDelays[Math.Min(_failures, RetryDelays.Count) - 1], Timeout.InfiniteTimeSpan);
                    }
                }
            }
        }
        // Raised outside the locks, so a handler that waits on the UI thread can't deadlock with a flush made there.
        Saved?.Invoke(this, new AutosaveEventArgs(revision, saved));
        return saved;
    }

    /// <summary>Stops the timer without saving, so call <see cref="Flush"/> first to keep the last changes.</summary>
    public void Dispose() => _timer.Dispose();
}

/// <summary>Which version a save attempt was for, and how it went.</summary>
public sealed class AutosaveEventArgs(long revision, Result result) : EventArgs
{
    public long Revision { get; } = revision;
    public Result Result { get; } = result;
}
