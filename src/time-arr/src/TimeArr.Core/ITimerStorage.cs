namespace TimeArr.Core;

/// <summary>
/// The database file as a whole. Implemented by the data layer; the command handler decides
/// when to open (and re-open) it. A damaged database is reported, never replaced (user decision).
/// </summary>
public interface ITimerStorage
{
    /// <summary>Where the database lives, for messages.</summary>
    string Location { get; }

    /// <summary>Opens (creating if needed) the database.</summary>
    Result<ITimerRepository> Open();
}
