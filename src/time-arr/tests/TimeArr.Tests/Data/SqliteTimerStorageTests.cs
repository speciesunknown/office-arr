using TimeArr.Core;
using TimeArr.Data;

namespace TimeArr.Tests.Data;

public sealed class SqliteTimerStorageTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 30, 15, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "time-arr-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_root, "time-arr.db");

    public SqliteTimerStorageTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Move_aside_renames_the_file_and_its_side_files_with_a_timestamp()
    {
        File.WriteAllText(DbPath, "db");
        File.WriteAllText(DbPath + "-wal", "wal");
        File.WriteAllText(DbPath + "-shm", "shm");

        var moved = new SqliteTimerStorage(DbPath).MoveAside(T0).Value;

        Assert.Equal(DbPath + ".corrupt-20261025-093015", moved);
        Assert.Equal("db", File.ReadAllText(moved));
        Assert.Equal("wal", File.ReadAllText(moved + "-wal"));
        Assert.Equal("shm", File.ReadAllText(moved + "-shm"));
        Assert.False(File.Exists(DbPath));
        Assert.False(File.Exists(DbPath + "-wal"));
    }

    [Fact]
    public void Move_aside_never_overwrites_an_earlier_move()
    {
        var storage = new SqliteTimerStorage(DbPath);
        File.WriteAllText(DbPath, "first");
        var first = storage.MoveAside(T0).Value;
        File.WriteAllText(DbPath, "second");

        var second = storage.MoveAside(T0).Value;

        Assert.NotEqual(first, second);
        Assert.Equal("first", File.ReadAllText(first));
        Assert.Equal("second", File.ReadAllText(second));
    }

    [Fact]
    public void Move_aside_with_nothing_there_is_not_found()
    {
        Assert.Equal(ErrorCode.NotFound, new SqliteTimerStorage(DbPath).MoveAside(T0).Error.Code);
    }

    [Fact]
    public void Open_gives_a_working_repository()
    {
        var repository = new SqliteTimerStorage(DbPath).Open().Value;

        Assert.Single(repository.List().Value);
    }
}
