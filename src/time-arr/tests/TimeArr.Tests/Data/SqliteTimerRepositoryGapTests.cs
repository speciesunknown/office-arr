using Microsoft.Data.Sqlite;
using TimeArr.Core;
using TimeArr.Data;

namespace TimeArr.Tests.Data;

/// <summary>
/// Edge cases for <see cref="SqliteTimerRepository"/> not covered by <see cref="SqliteTimerRepositoryTests"/>.
/// Each test gets its own temp folder; nothing touches the real profile database.
/// </summary>
public sealed class SqliteTimerRepositoryGapTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "time-arr-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_root, "time-arr", "time-arr.db");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private SqliteTimerRepository Open() => SqliteTimerRepository.Open(DbPath).Value;

    private static long FirstId(SqliteTimerRepository repository) => repository.List().Value[0].Id;

    /// <summary>Opens <paramref name="path"/>, failing the test if Open throws instead of returning a Result.</summary>
    private static Result<SqliteTimerRepository> OpenWithoutThrowing(string path)
    {
        Result<SqliteTimerRepository>? result = null;
        var thrown = Record.Exception(() => result = SqliteTimerRepository.Open(path));
        Assert.Null(thrown);
        return result!;
    }

    // --- opening ---

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    public void Busy_timeout_bounds_are_accepted(int seconds)
    {
        Assert.True(SqliteTimerRepository.Open(DbPath, seconds).IsSuccess);
    }

    [Fact]
    public void Whitespace_path_is_invalid()
    {
        Assert.Equal(ErrorCode.InvalidInput, SqliteTimerRepository.Open("   ").Error.Code);
    }

    [Fact]
    public void Root_path_gives_a_result_not_an_exception()
    {
        // "/" has no parent folder; every expected failure should be a Result.
        if (OperatingSystem.IsWindows())
            return;

        Assert.True(OpenWithoutThrowing("/").IsFailure);
    }

    [Fact]
    public void Path_with_nul_character_gives_a_result_not_an_exception()
    {
        Assert.True(OpenWithoutThrowing(Path.Combine(_root, "bad\0name", "time-arr.db")).IsFailure);
    }

    [Fact]
    public void Path_that_is_a_folder_is_unavailable()
    {
        Directory.CreateDirectory(DbPath);

        Assert.Equal(ErrorCode.StorageUnavailable, OpenWithoutThrowing(DbPath).Error.Code);
    }

    [Fact]
    public void Dangling_symlink_database_file_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        File.CreateSymbolicLink(DbPath, Path.Combine(_root, "does-not-exist.db"));

        Assert.Equal(ErrorCode.StorageUnavailable, SqliteTimerRepository.Open(DbPath).Error.Code);
        Assert.False(File.Exists(Path.Combine(_root, "does-not-exist.db")));
    }

    [Fact]
    public void Symlinked_database_folder_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;

        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(Path.GetDirectoryName(DbPath)!, real);

        Assert.Equal(ErrorCode.StorageUnavailable, SqliteTimerRepository.Open(DbPath).Error.Code);
    }

    [Fact]
    public void Existing_empty_file_becomes_a_new_database()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        File.WriteAllBytes(DbPath, []);

        var only = Assert.Single(Open().List().Value);
        Assert.Equal(SqliteTimerRepository.FirstTimerName, only.Name);
    }

    [Fact]
    public void Table_with_wrong_columns_is_reported_as_corrupt()
    {
        Open();
        Execute("DROP TABLE timers; CREATE TABLE timers (id INTEGER PRIMARY KEY, name TEXT);");

        Assert.Equal(ErrorCode.StorageCorrupt, SqliteTimerRepository.Open(DbPath).Error.Code);
    }

    [Fact]
    public void Foreign_database_with_a_timers_table_but_no_version_is_reported_as_foreign()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        Execute("CREATE TABLE timers (something TEXT);");

        Assert.Equal(ErrorCode.StorageForeign, OpenWithoutThrowing(DbPath).Error.Code);
    }

    [Fact]
    public void Open_while_another_writer_holds_the_lock_still_works()
    {
        // Changed after redteam M1: opening an existing database only reads, so a CLI can open
        // it while the running app is mid-write (WAL lets readers through).
        Open();
        using var other = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        other.Open();
        using var hold = other.BeginTransaction(deferred: false);

        Assert.True(OpenWithoutThrowing(DbPath, busyTimeoutSeconds: 1).IsSuccess);
    }

    private static Result<SqliteTimerRepository> OpenWithoutThrowing(string path, int busyTimeoutSeconds)
    {
        Result<SqliteTimerRepository>? result = null;
        var thrown = Record.Exception(() => result = SqliteTimerRepository.Open(path, busyTimeoutSeconds));
        Assert.Null(thrown);
        return result!;
    }

    // --- locking ---

    [Fact]
    public void Reads_still_work_while_another_writer_holds_the_lock()
    {
        var repository = SqliteTimerRepository.Open(DbPath, busyTimeoutSeconds: 1).Value;
        var id = FirstId(repository);
        using var other = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        other.Open();
        using var hold = other.BeginTransaction(deferred: false);

        Assert.True(repository.List().IsSuccess);
        Assert.True(repository.Get(id).IsSuccess);
        Assert.True(repository.FindByName(SqliteTimerRepository.FirstTimerName).IsSuccess);
    }

    [Fact]
    public void Every_write_is_busy_while_another_writer_holds_the_lock()
    {
        var repository = SqliteTimerRepository.Open(DbPath, busyTimeoutSeconds: 1).Value;
        var id = FirstId(repository);
        using var other = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        other.Open();
        using var hold = other.BeginTransaction(deferred: false);

        Assert.Equal(ErrorCode.StorageBusy, repository.Create("x").Error.Code);
        Assert.Equal(ErrorCode.StorageBusy, repository.Rename(id, "y").Error.Code);
        Assert.Equal(ErrorCode.StorageBusy, repository.Delete(id).Error.Code);
    }

    [Fact]
    public void Concurrent_creates_of_the_same_name_give_one_timer_and_one_conflict()
    {
        var a = SqliteTimerRepository.Open(DbPath, busyTimeoutSeconds: 30).Value;
        var b = SqliteTimerRepository.Open(DbPath, busyTimeoutSeconds: 30).Value;

        for (var i = 0; i < 25; i++)
        {
            var name = $"race{i}";
            Result<TimerEntry>? ra = null, rb = null;
            Parallel.Invoke(() => ra = a.Create(name), () => rb = b.Create(name.ToUpperInvariant()));

            Assert.True(ra!.IsSuccess ^ rb!.IsSuccess, $"round {i}: {ra} / {rb}");
            Assert.Equal(ErrorCode.Conflict, (ra.IsFailure ? ra : rb).Error.Code);
        }

        Assert.Equal(26, a.List().Value.Count);
    }

    [Fact]
    public void Concurrent_creates_never_exceed_the_cap()
    {
        var a = SqliteTimerRepository.Open(DbPath, busyTimeoutSeconds: 30).Value;
        var b = SqliteTimerRepository.Open(DbPath, busyTimeoutSeconds: 30).Value;
        FillTo(SqliteTimerRepository.MaxTimers - 10);

        var outcomes = new System.Collections.Concurrent.ConcurrentBag<Result<TimerEntry>>();
        Parallel.Invoke(
            () => { for (var i = 0; i < 10; i++) outcomes.Add(a.Create($"a{i}")); },
            () => { for (var i = 0; i < 10; i++) outcomes.Add(b.Create($"b{i}")); });

        Assert.Equal(10, outcomes.Count(r => r.IsSuccess));
        Assert.All(outcomes.Where(r => r.IsFailure), r => Assert.Equal(ErrorCode.Conflict, r.Error.Code));
        Assert.Equal(SqliteTimerRepository.MaxTimers, a.List().Value.Count);
    }

    // --- names ---

    [Fact]
    public void Names_that_normalize_alike_clash()
    {
        var repository = Open();
        repository.Create("café");

        Assert.Equal(ErrorCode.Conflict, repository.Create("café").Error.Code);
    }

    [Fact]
    public void Find_by_name_normalizes_and_trims()
    {
        var repository = Open();
        var created = repository.Create("café").Value;

        // ASCII letters fold; the composed "é" must match exactly (see Case_folding_is_ascii_only).
        Assert.Equal(created, repository.FindByName("  CAFé ").Value);
    }

    [Fact]
    public void Case_folding_is_ascii_only()
    {
        // Pins the documented rule: unique ignoring ASCII case, so "Ä" and "ä" are different names.
        var repository = Open();
        var upper = repository.Create("Ärger").Value;
        var lower = repository.Create("ärger");

        Assert.True(lower.IsSuccess);
        Assert.Equal(upper, repository.FindByName("ÄRGER").Value);
        Assert.Equal(lower.Value, repository.FindByName("ärger").Value);
    }

    [Fact]
    public void Rename_to_exactly_the_same_name_is_allowed()
    {
        var repository = Open();
        var id = FirstId(repository);

        Assert.Equal(SqliteTimerRepository.FirstTimerName, repository.Rename(id, SqliteTimerRepository.FirstTimerName).Value.Name);
    }

    [Fact]
    public void Rename_stores_the_trimmed_name_and_frees_the_old_one()
    {
        var repository = Open();
        var id = FirstId(repository);

        Assert.Equal("main", repository.Rename(id, "  main  ").Value.Name);
        Assert.Equal("main", repository.Get(id).Value.Name);
        Assert.True(repository.Create(SqliteTimerRepository.FirstTimerName).IsSuccess);
    }

    [Fact]
    public void Longest_name_survives_a_round_trip()
    {
        var repository = Open();
        var name = new string('n', TimerName.MaxLength);

        var created = repository.Create(name).Value;

        Assert.Equal(created, Open().FindByName(name).Value);
    }

    [Fact]
    public void Names_with_quotes_and_sql_are_stored_verbatim()
    {
        var repository = Open();
        const string name = "x'); DROP TABLE timers; --";

        var created = repository.Create(name).Value;

        Assert.Equal(name, repository.Get(created.Id).Value.Name);
        Assert.Equal(2, repository.List().Value.Count);
    }

    // --- ids and cap ---

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Odd_ids_are_not_found(long id)
    {
        var repository = Open();

        Assert.Equal(ErrorCode.NotFound, repository.Get(id).Error.Code);
        Assert.Equal(ErrorCode.NotFound, repository.Delete(id).Error.Code);
    }

    [Fact]
    public void Deleting_twice_is_not_found_the_second_time()
    {
        var repository = Open();
        var id = repository.Create("once").Value.Id;

        Assert.True(repository.Delete(id).IsSuccess);
        Assert.Equal(ErrorCode.NotFound, repository.Delete(id).Error.Code);
    }

    [Fact]
    public void Ids_are_not_reused_after_deleting_the_newest_and_reopening()
    {
        var repository = Open();
        var newest = repository.Create("newest").Value;
        repository.Delete(newest.Id);

        var next = Open().Create("next").Value;

        Assert.True(next.Id > newest.Id);
    }

    [Fact]
    public void Ids_are_not_reused_after_deleting_every_timer()
    {
        var repository = Open();
        var last = repository.Create("last").Value;
        foreach (var timer in repository.List().Value)
            repository.Delete(timer.Id);

        Assert.True(Open().Create("fresh").Value.Id > last.Id);
    }

    [Fact]
    public void Deleting_at_the_cap_makes_room_again()
    {
        var repository = Open();
        FillTo(SqliteTimerRepository.MaxTimers);
        Assert.Equal(ErrorCode.Conflict, repository.Create("extra").Error.Code);

        repository.Delete(FirstId(repository));

        Assert.True(repository.Create("extra").IsSuccess);
    }

    [Fact]
    public void Rename_and_update_still_work_at_the_cap()
    {
        var repository = Open();
        FillTo(SqliteTimerRepository.MaxTimers);
        var id = FirstId(repository);

        Assert.True(repository.Rename(id, "renamed").IsSuccess);
        Assert.True(repository.Update(id, s => s.Start(T0)).IsSuccess);
    }

    [Fact]
    public void Name_clash_is_reported_before_the_cap()
    {
        var repository = Open();
        FillTo(SqliteTimerRepository.MaxTimers);

        // Either is a Conflict; this pins that the duplicate check runs and nothing is inserted.
        Assert.Equal(ErrorCode.Conflict, repository.Create(SqliteTimerRepository.FirstTimerName).Error.Code);
        Assert.Equal(SqliteTimerRepository.MaxTimers, repository.List().Value.Count);
    }

    // --- state through storage ---

    [Fact]
    public void Update_returns_what_was_written()
    {
        var repository = Open();
        var id = FirstId(repository);

        var written = repository.Update(id, s => s.Start(T0)).Value;

        Assert.Equal(written, repository.Get(id).Value);
    }

    [Fact]
    public void Reset_while_running_is_stored_as_running_from_now()
    {
        var repository = Open();
        var id = FirstId(repository);
        repository.Update(id, s => s.Start(T0));

        repository.Update(id, s => s.Reset(T0.AddMinutes(30)));
        var loaded = Open().Get(id).Value.State;

        Assert.True(loaded.IsRunning);
        Assert.Equal(TimeSpan.Zero, loaded.Accumulated);
        Assert.Equal(T0.AddMinutes(30), loaded.StartedAtUtc);
    }

    [Fact]
    public void Reset_while_paused_is_stored_as_zero()
    {
        var repository = Open();
        var id = FirstId(repository);
        repository.Update(id, s => s.Start(T0).Pause(T0.AddMinutes(5)));

        repository.Update(id, s => s.Reset(T0.AddMinutes(30)));

        Assert.Equal(TimerState.Zero, Open().Get(id).Value.State);
    }

    [Fact]
    public void Paused_timer_does_not_count_while_closed()
    {
        var repository = Open();
        var id = FirstId(repository);
        repository.Update(id, s => s.Start(T0).Pause(T0.AddMinutes(5)));

        var loaded = Open().Get(id).Value.State;

        Assert.Equal(TimeSpan.FromMinutes(5), loaded.ElapsedAt(T0.AddDays(3)));
    }

    [Fact]
    public void Clock_set_back_after_reopening_never_goes_negative()
    {
        var repository = Open();
        var id = FirstId(repository);
        repository.Update(id, s => s.Start(T0).Pause(T0.AddMinutes(2)).Start(T0.AddMinutes(3)));

        var reopened = Open();
        Assert.Equal(TimeSpan.FromMinutes(2), reopened.Get(id).Value.State.ElapsedAt(T0.AddHours(-1)));
        var paused = reopened.Update(id, s => s.Pause(T0.AddHours(-1))).Value.State;

        Assert.Equal(TimeSpan.FromMinutes(2), paused.Accumulated);
    }

    [Fact]
    public void Large_accumulated_time_round_trips_exactly()
    {
        var repository = Open();
        var id = FirstId(repository);
        var big = TimeSpan.FromDays(100_000) + TimeSpan.FromMilliseconds(1);

        repository.Update(id, _ => TimerState.Create(false, null, big));

        Assert.Equal(big, Open().Get(id).Value.State.Accumulated);
    }

    [Fact]
    public void Saturated_pause_round_trips()
    {
        var repository = Open();
        var id = FirstId(repository);

        var written = repository.Update(id, _ =>
            TimerState.Create(true, DateTimeOffset.MinValue, TimeSpan.Zero).Value.Pause(DateTimeOffset.MaxValue)).Value;

        Assert.Equal(written, Open().Get(id).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extreme_start_times_round_trip(bool max)
    {
        var repository = Open();
        var id = FirstId(repository);
        var start = max ? DateTimeOffset.MaxValue : DateTimeOffset.MinValue;

        repository.Update(id, s => s.Start(start));

        Assert.Equal(start, Open().Get(id).Value.State.StartedAtUtc);
    }

    [Fact]
    public void Start_time_given_in_local_offset_is_stored_as_utc()
    {
        var repository = Open();
        var id = FirstId(repository);

        repository.Update(id, s => s.Start(new DateTimeOffset(2026, 10, 24, 14, 0, 0, TimeSpan.FromHours(2))));
        var loaded = Open().Get(id).Value.State.StartedAtUtc!.Value;

        Assert.Equal(T0, loaded);
        Assert.Equal(TimeSpan.Zero, loaded.Offset);
    }

    [Fact]
    public void Throwing_change_writes_nothing()
    {
        // A throwing callback is a caller bug, so the exception may escape; the row must stay as it was.
        var repository = Open();
        var id = FirstId(repository);

        Record.Exception(() => repository.Update(id, _ => throw new InvalidOperationException("boom")));

        Assert.Equal(TimerState.Zero, repository.Get(id).Value.State);
        Assert.True(repository.Update(id, s => s.Start(T0)).IsSuccess);
    }

    [Fact]
    public void Update_with_null_change_is_a_bug()
    {
        var repository = Open();

        Assert.Throws<ArgumentNullException>(() => repository.Update(FirstId(repository), null!));
    }

    // --- damage ---

    [Theory]
    [InlineData("UPDATE timers SET is_running = 0, started_at_utc = '2026-10-24T12:00:00.0000000Z';")]
    [InlineData("UPDATE timers SET is_running = 1, started_at_utc = '2026-10-24T12:00:00.0000000+01:00';")]
    [InlineData("UPDATE timers SET is_running = 1, started_at_utc = '2026-10-24T12:00:00Z';")]
    [InlineData("UPDATE timers SET is_running = 1, started_at_utc = '2026-02-30T12:00:00.0000000Z';")]
    [InlineData("UPDATE timers SET accumulated_ms = 9223372036854775807;")]
    [InlineData("UPDATE timers SET name = 'padded ';")]
    [InlineData("UPDATE timers SET name = printf('%.65c', 'x');")]
    // '-flag' and non-NFC names were here: they break today's input rules but not the stored
    // format, so they now stay readable (see SqliteFileHandlingGapTests).
    public void More_tampered_rows_are_reported_as_corrupt(string tamper)
    {
        var repository = Open();
        var id = FirstId(repository);
        Execute("PRAGMA ignore_check_constraints = ON; " + tamper);

        Assert.Equal(ErrorCode.StorageCorrupt, repository.Get(id).Error.Code);
        Assert.Equal(ErrorCode.StorageCorrupt, repository.List().Error.Code);
        Assert.Equal(ErrorCode.StorageCorrupt, SqliteTimerRepository.Open(DbPath).Error.Code);
    }

    [Fact]
    public void Tampered_row_makes_update_fail_cleanly()
    {
        var repository = Open();
        var id = FirstId(repository);
        Execute("PRAGMA ignore_check_constraints = ON; UPDATE timers SET is_running = 7;");

        Assert.Equal(ErrorCode.StorageCorrupt, repository.Update(id, s => s.Start(T0)).Error.Code);
        Assert.Equal(ErrorCode.StorageCorrupt, repository.Rename(id, "fine").Error.Code);
    }

    [Fact]
    public void Database_file_deleted_between_operations_is_reported_not_thrown()
    {
        var repository = Open();
        var id = FirstId(repository);
        File.Delete(DbPath);
        File.Delete(DbPath + "-wal");
        File.Delete(DbPath + "-shm");

        Result<TimerEntry>? result = null;
        Assert.Null(Record.Exception(() => result = repository.Get(id)));
        Assert.True(result!.IsFailure);
        Assert.False(File.Exists(DbPath));
    }

    private void FillTo(int total)
    {
        // The table already holds whatever timers exist; top it up with bulk rows.
        Execute($"""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {total} - (SELECT count(*) FROM timers))
            INSERT INTO timers (name, is_running, started_at_utc, accumulated_ms) SELECT 'bulk' || i, 0, NULL, 0 FROM n;
            """);
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
