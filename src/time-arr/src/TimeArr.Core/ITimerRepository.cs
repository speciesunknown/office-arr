namespace TimeArr.Core;

/// <summary>Durable storage for the timers. Implemented by the data layer; used by command handlers.</summary>
/// <remarks>
/// Every method is one transaction. Names are validated with <see cref="TimerName.Validate"/>;
/// unknown ids give <see cref="ErrorCode.NotFound"/>, name clashes <see cref="ErrorCode.Conflict"/>.
/// </remarks>
public interface ITimerRepository
{
    /// <summary>All timers, ordered by id (creation order).</summary>
    Result<IReadOnlyList<TimerEntry>> List();

    Result<TimerEntry> Get(long id);

    /// <summary>Finds a timer by name, ignoring ASCII case.</summary>
    Result<TimerEntry> FindByName(string name);

    /// <summary>Adds a paused timer at zero.</summary>
    Result<TimerEntry> Create(string name);

    Result<TimerEntry> Rename(long id, string name);

    Result Delete(long id);

    /// <summary>
    /// Reads the timer's state, applies <paramref name="change"/> and writes the result in one
    /// transaction, so concurrent updates cannot interleave. If <paramref name="change"/> fails,
    /// nothing is written and its failure is returned.
    /// </summary>
    /// <returns>The timer as written.</returns>
    Result<TimerEntry> Update(long id, Func<TimerState, Result<TimerState>> change);
}
