using System.Globalization;
using TimeArr.Core;

namespace TimeArr.Data;

/// <summary>The SQLite database file at one path.</summary>
public sealed class SqliteTimerStorage : ITimerStorage
{
    private static readonly string[] SideFileSuffixes = ["-wal", "-shm", "-journal"];

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

    public Result<string> MoveAside(DateTimeOffset nowUtc)
    {
        if (!File.Exists(Location))
            return Result<string>.Fail(ErrorCode.NotFound, $"There is no database at '{Location}' to move.");

        var stamp = nowUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = $"{Location}.corrupt-{stamp}";
        for (var n = 2; Exists(target); n++)
            target = $"{Location}.corrupt-{stamp}-{n}";

        try
        {
            // Side files first: a -wal left behind would be replayed into the fresh database.
            foreach (var suffix in SideFileSuffixes)
            {
                if (File.Exists(Location + suffix))
                    File.Move(Location + suffix, target + suffix);
            }
            File.Move(Location, target);
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Fail(ErrorCode.StorageUnavailable, $"Cannot move the database aside: {e.Message}");
        }
    }

    private static bool Exists(string target) =>
        File.Exists(target) || SideFileSuffixes.Any(suffix => File.Exists(target + suffix));
}
