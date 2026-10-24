using Microsoft.Data.Sqlite;
using TimeArr.Core;
using TimeArr.Data;

namespace TimeArr.Tests.Data;

/// <summary>
/// Edge cases for file identification (application_id), read-only files and stored names after the
/// post-M1 changes. Each test gets its own temp folder; nothing touches the real profile database.
/// </summary>
public sealed class SqliteFileHandlingGapTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "time-arr-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_root, "time-arr", "time-arr.db");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static Result<SqliteTimerRepository> OpenWithoutThrowing(string path, int busyTimeoutSeconds = 5)
    {
        Result<SqliteTimerRepository>? result = null;
        Assert.Null(Record.Exception(() => result = SqliteTimerRepository.Open(path, busyTimeoutSeconds)));
        return result!;
    }

    // --- identifying the file ---

    [Fact]
    public void Database_with_another_application_id_is_refused_and_left_untouched()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        Execute("PRAGMA application_id = 1234;");
        var before = File.ReadAllBytes(DbPath);

        Assert.Equal(ErrorCode.StorageCorrupt, OpenWithoutThrowing(DbPath).Error.Code);
        Assert.Equal(before, File.ReadAllBytes(DbPath));
    }

    [Fact]
    public void Database_with_only_a_user_version_is_refused()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        Execute("PRAGMA user_version = 1;");

        Assert.Equal(ErrorCode.StorageCorrupt, OpenWithoutThrowing(DbPath).Error.Code);
    }

    [Fact]
    public void Our_marker_without_a_table_is_corrupt_and_not_recreated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        Execute($"PRAGMA application_id = {SqliteTimerRepository.ApplicationId}; PRAGMA user_version = 1;");

        Assert.Equal(ErrorCode.StorageCorrupt, OpenWithoutThrowing(DbPath).Error.Code);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema;"));
    }

    [Fact]
    public void Older_marked_file_at_version_zero_is_handled_without_throwing()
    {
        // Ours by marker but no schema version: whatever the verdict, it must be a Result.
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        Execute($"PRAGMA application_id = {SqliteTimerRepository.ApplicationId};");

        OpenWithoutThrowing(DbPath);
    }

    [Fact]
    public void New_empty_file_with_a_writer_holding_the_lock_is_busy()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        File.WriteAllBytes(DbPath, []);
        using var other = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        other.Open();
        using var hold = other.BeginTransaction(deferred: false);

        Assert.Equal(ErrorCode.StorageBusy, OpenWithoutThrowing(DbPath, busyTimeoutSeconds: 1).Error.Code);
    }

    // --- read-only file ---

    [Fact]
    public void Read_only_database_gives_results_for_reads_and_writes()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return;

        var id = SqliteTimerRepository.Open(DbPath).Value.List().Value[0].Id;
        File.SetUnixFileMode(DbPath, UnixFileMode.UserRead);

        var opened = OpenWithoutThrowing(DbPath);
        if (opened.IsFailure)
        {
            // Refusing to open is acceptable, but not as "corrupt" or "too new".
            Assert.Equal(ErrorCode.StorageUnavailable, opened.Error.Code);
            return;
        }

        var repository = opened.Value;
        Result<TimerEntry>? write = null;
        Assert.Null(Record.Exception(() => write = repository.Update(id, s => s.Start(T0))));
        Assert.Equal(ErrorCode.StorageUnavailable, write!.Error.Code);
        Assert.True(repository.Get(id).IsSuccess);
        Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(DbPath));
    }

    // --- stored names under the stricter rules ---

    [Theory]
    [InlineData("UPDATE timers SET name = '42';")]
    [InlineData("UPDATE timers SET name = 'work' || char(8203);")]  // zero-width space
    [InlineData("UPDATE timers SET name = char(8238) || 'evil';")]  // right-to-left override
    [InlineData("UPDATE timers SET name = 'a' || char(8232) || 'b';")] // line separator
    public void Stored_names_breaking_the_new_rules_are_reported_as_corrupt(string tamper)
    {
        var repository = SqliteTimerRepository.Open(DbPath).Value;
        var id = repository.List().Value[0].Id;
        Execute(tamper);

        Assert.Equal(ErrorCode.StorageCorrupt, repository.Get(id).Error.Code);
        Assert.Equal(ErrorCode.StorageCorrupt, OpenWithoutThrowing(DbPath).Error.Code);
    }

    [Fact]
    public void Digit_only_name_cannot_be_created_or_renamed_to()
    {
        var repository = SqliteTimerRepository.Open(DbPath).Value;
        var id = repository.List().Value[0].Id;

        Assert.Equal(ErrorCode.InvalidInput, repository.Create(id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, repository.Rename(id, "  7 ").Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, repository.FindByName("7").Error.Code);
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False;Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
