using TimeArr.Core;

namespace TimeArr.Data;

/// <summary>The SQLite database file at one path.</summary>
public sealed class SqliteTimerStorage : ITimerStorage
{
    private readonly int _busyTimeoutSeconds;

    public SqliteTimerStorage(string path, int busyTimeoutSeconds = 5)
    {
        Location = path;
        _busyTimeoutSeconds = busyTimeoutSeconds;
    }

    public string Location { get; }

    /// <summary>The storage at <see cref="SqliteTimerRepository.DefaultPath"/>.</summary>
    public static Result<SqliteTimerStorage> Default()
    {
        var path = SqliteTimerRepository.DefaultPath();
        return path.IsSuccess ? new SqliteTimerStorage(path.Value) : Result<SqliteTimerStorage>.Fail(path.Error);
    }

    public Result<ITimerRepository> Open()
    {
        var opened = SqliteTimerRepository.Open(Location, _busyTimeoutSeconds);
        return opened.IsSuccess ? opened.Value : Result<ITimerRepository>.Fail(opened.Error);
    }
}
