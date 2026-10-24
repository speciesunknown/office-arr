using TimeArr.Data;

namespace TimeArr.Tests.Data;

public sealed class SqliteTimerStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "time-arr-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_root, "time-arr.db");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Open_gives_a_working_repository()
    {
        var storage = new SqliteTimerStorage(DbPath);

        Assert.Equal(DbPath, storage.Location);
        Assert.Single(storage.Open().Value.List().Value);
    }

    [Fact]
    public async Task Fifo_at_the_path_fails_at_once_instead_of_hanging()
    {
        // Redteam M1 round 4: reading the header of a FIFO blocked forever.
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(_root);
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", DbPath))
            await mkfifo.WaitForExitAsync();

        var open = Task.Run(() => new SqliteTimerStorage(DbPath, busyTimeoutSeconds: 1).Open());
        var finished = await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(open, finished);
        Assert.True((await open).IsFailure);
    }

    [Fact]
    public void Default_storage_is_at_the_default_path()
    {
        Assert.Equal(SqliteTimerRepository.DefaultPath().Value, SqliteTimerStorage.Default().Value.Location);
    }
}
