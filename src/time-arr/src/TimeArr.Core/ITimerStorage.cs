namespace TimeArr.Core;

/// <summary>
/// The database file as a whole: opening it, and moving a damaged one out of the way.
/// Implemented by the data layer; command handlers own the lifecycle.
/// </summary>
public interface ITimerStorage
{
    /// <summary>Where the database lives, for messages.</summary>
    string Location { get; }

    /// <summary>Opens (creating if needed) the database.</summary>
    Result<ITimerRepository> Open();

    /// <summary>
    /// Renames the database file (and its side files) to
    /// <c>&lt;name&gt;.corrupt-&lt;timestamp&gt;</c> so a fresh one can be created. Nothing is deleted.
    /// Only for a database the user agreed to give up on.
    /// </summary>
    /// <returns>The new path of the moved file.</returns>
    Result<string> MoveAside(DateTimeOffset nowUtc);
}
