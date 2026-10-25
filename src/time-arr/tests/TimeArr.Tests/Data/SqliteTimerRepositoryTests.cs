using Microsoft.Data.Sqlite;
using TimeArr.Core;
using TimeArr.Data;

namespace TimeArr.Tests.Data;

/// <summary>Each test gets its own temp folder; nothing touches the real profile database.</summary>
public sealed class SqliteTimerRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "time-arr-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_root, "time-arr", "time-arr.db");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static readonly string[] Suffixes = ["", "-wal", "-shm"];

    private SqliteTimerRepository Open() => SqliteTimerRepository.Open(DbPath).Value;

    private static long FirstId(SqliteTimerRepository repository) => repository.List().Value[0].Id;

    // --- location and file handling ---

    [Fact]
    public void Default_path_is_in_local_application_data()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "time-arr", "time-arr.db");

        Assert.Equal(expected, SqliteTimerRepository.DefaultPath().Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/time-arr.db")]
    public void Path_must_be_absolute(string path)
    {
        Assert.Equal(ErrorCode.InvalidInput, SqliteTimerRepository.Open(path).Error.Code);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/tmp/time-arr-tests/folder/")]
    public void Path_must_name_a_file(string path)
    {
        Assert.Equal(ErrorCode.InvalidInput, SqliteTimerRepository.Open(path).Error.Code);
    }

    [Fact]
    public void Path_that_is_a_folder_is_unavailable()
    {
        Directory.CreateDirectory(DbPath);

        Assert.Equal(ErrorCode.StorageUnavailable, SqliteTimerRepository.Open(DbPath).Error.Code);
    }

    [Fact]
    public void Existing_folder_that_is_not_ours_keeps_its_mode()
    {
        if (OperatingSystem.IsWindows())
            return;

        var folder = Path.Combine(_root, "shared");
        Directory.CreateDirectory(folder);
        const UnixFileMode open = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(folder, open);
        var path = Path.Combine(folder, "timers.db");

        Assert.True(SqliteTimerRepository.Open(path).IsSuccess);

        Assert.Equal(open, File.GetUnixFileMode(folder));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Concurrent_first_opens_create_one_default_timer()
    {
        var results = new Result<SqliteTimerRepository>[8];

        Parallel.For(0, results.Length, i => results[i] = SqliteTimerRepository.Open(DbPath, busyTimeoutSeconds: 30));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.ToString()));
        Assert.Single(results[0].Value.List().Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void Busy_timeout_must_be_sane(int seconds)
    {
        Assert.Equal(ErrorCode.InvalidInput, SqliteTimerRepository.Open(DbPath, seconds).Error.Code);
    }

    [Fact]
    public void Folder_and_files_are_owner_only()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repository = Open();
        repository.Update(FirstId(repository), s => s.Start(T0));

        var folder = Path.GetDirectoryName(DbPath)!;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
        foreach (var file in Directory.GetFiles(folder))
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Fact]
    public void Loose_permissions_are_tightened_on_open()
    {
        if (OperatingSystem.IsWindows())
            return;

        Open();
        File.SetUnixFileMode(DbPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);

        Open();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(DbPath));
    }

    [Fact]
    public void Symlinked_database_file_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        var elsewhere = Path.Combine(_root, "elsewhere.db");
        File.WriteAllBytes(elsewhere, []);
        File.CreateSymbolicLink(DbPath, elsewhere);

        Assert.Equal(ErrorCode.StorageUnavailable, SqliteTimerRepository.Open(DbPath).Error.Code);
    }

    [Fact]
    public void Unwritable_folder_is_reported_as_unavailable()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return;

        Directory.CreateDirectory(_root);
        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Equal(ErrorCode.StorageUnavailable, SqliteTimerRepository.Open(DbPath).Error.Code);
        }
        finally
        {
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // --- timers ---

    [Fact]
    public void New_database_has_one_default_timer_at_zero()
    {
        var timers = Open().List().Value;

        var only = Assert.Single(timers);
        Assert.Equal(SqliteTimerRepository.FirstTimerName, only.Name);
        Assert.Equal(TimerState.Zero, only.State);
    }

    [Fact]
    public void Reopening_does_not_add_another_default_timer()
    {
        Open();

        Assert.Single(Open().List().Value);
    }

    [Fact]
    public void Created_timers_are_listed_in_creation_order()
    {
        var repository = Open();

        var work = repository.Create("work").Value;
        var study = repository.Create("  study ").Value;

        Assert.Equal("study", study.Name);
        Assert.Equal(TimerState.Zero, work.State);
        Assert.Equal([SqliteTimerRepository.FirstTimerName, "work", "study"], repository.List().Value.Select(t => t.Name));
    }

    [Fact]
    public void Names_are_unique_ignoring_case()
    {
        var repository = Open();
        repository.Create("Work");

        Assert.Equal(ErrorCode.Conflict, repository.Create("wORK").Error.Code);
        Assert.Equal(ErrorCode.Conflict, repository.Rename(FirstId(repository), "work").Error.Code);
    }

    [Fact]
    public void Invalid_names_are_rejected_before_touching_the_database()
    {
        var repository = Open();

        Assert.Equal(ErrorCode.InvalidInput, repository.Create("").Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, repository.Rename(FirstId(repository), "-x").Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, repository.FindByName("a\nb").Error.Code);
    }

    [Fact]
    public void Find_by_name_ignores_case()
    {
        var repository = Open();
        var created = repository.Create("Deep Work").Value;

        Assert.Equal(created, repository.FindByName("deep work").Value);
        Assert.Equal(ErrorCode.NotFound, repository.FindByName("nothing").Error.Code);
    }

    [Fact]
    public void Rename_keeps_id_and_state()
    {
        var repository = Open();
        var id = FirstId(repository);
        repository.Update(id, s => s.Start(T0));

        var renamed = repository.Rename(id, "main").Value;

        Assert.Equal(id, renamed.Id);
        Assert.True(renamed.State.IsRunning);
        Assert.Equal(renamed, repository.Get(id).Value);
    }

    [Fact]
    public void Rename_to_own_name_in_other_case_is_allowed()
    {
        var repository = Open();

        Assert.Equal("DEFAULT", repository.Rename(FirstId(repository), "DEFAULT").Value.Name);
    }

    [Fact]
    public void Delete_removes_the_timer_and_its_id_is_never_reused()
    {
        var repository = Open();
        var doomed = repository.Create("doomed").Value;

        Assert.True(repository.Delete(doomed.Id).IsSuccess);
        var next = repository.Create("next").Value;

        Assert.Equal(ErrorCode.NotFound, repository.Get(doomed.Id).Error.Code);
        Assert.True(next.Id > doomed.Id);
    }

    [Fact]
    public void Unknown_ids_are_not_found()
    {
        var repository = Open();

        Assert.Equal(ErrorCode.NotFound, repository.Get(999).Error.Code);
        Assert.Equal(ErrorCode.NotFound, repository.Rename(999, "x").Error.Code);
        Assert.Equal(ErrorCode.NotFound, repository.Delete(999).Error.Code);
        Assert.Equal(ErrorCode.NotFound, repository.Update(999, s => s.Start(T0)).Error.Code);
    }

    [Fact]
    public void Number_of_timers_is_capped()
    {
        var repository = Open();
        Execute($"""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {SqliteTimerRepository.MaxTimers - 2})
            INSERT INTO timers (name, is_running, started_at_utc, accumulated_ms) SELECT 'bulk' || i, 0, NULL, 0 FROM n;
            """);

        Assert.True(repository.Create("last one").IsSuccess);
        Assert.Equal(ErrorCode.Conflict, repository.Create("one too many").Error.Code);
    }

    // --- state ---

    [Fact]
    public void Timers_keep_separate_state()
    {
        var repository = Open();
        var a = FirstId(repository);
        var b = repository.Create("b").Value.Id;

        repository.Update(a, s => s.Start(T0));
        repository.Update(b, s => s.Start(T0).Pause(T0.AddMinutes(3)));

        Assert.True(repository.Get(a).Value.State.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(3), repository.Get(b).Value.State.Accumulated);
    }

    [Fact]
    public void State_survives_reopening_and_keeps_counting()
    {
        var first = Open();
        var id = FirstId(first);
        Assert.True(first.Update(id, s => s.Start(T0).Pause(T0.AddSeconds(30)).Start(T0.AddMinutes(1))).IsSuccess);

        // A "restart": a fresh repository on the same file.
        var loaded = Open().Get(id).Value.State;

        Assert.True(loaded.IsRunning);
        Assert.Equal(T0.AddMinutes(1), loaded.StartedAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(30), loaded.Accumulated);
        Assert.Equal(TimeSpan.FromSeconds(30) + TimeSpan.FromHours(2), loaded.ElapsedAt(T0.AddMinutes(1).AddHours(2)));
    }

    [Fact]
    public void Sub_millisecond_start_times_round_trip()
    {
        var repository = Open();
        var id = FirstId(repository);
        var start = T0.AddTicks(1_234_567);

        repository.Update(id, s => s.Start(start));

        Assert.Equal(start, repository.Get(id).Value.State.StartedAtUtc);
    }

    [Fact]
    public void Failed_change_writes_nothing()
    {
        var repository = Open();
        var id = FirstId(repository);

        var result = repository.Update(id, _ => Result<TimerState>.Fail(ErrorCode.InvalidInput, "rejected"));

        Assert.Equal(ErrorCode.InvalidInput, result.Error.Code);
        Assert.Equal(TimerState.Zero, repository.Get(id).Value.State);
    }

    [Fact]
    public void Concurrent_updates_do_not_lose_writes()
    {
        var a = SqliteTimerRepository.Open(DbPath, busyTimeoutSeconds: 30).Value;
        var b = SqliteTimerRepository.Open(DbPath, busyTimeoutSeconds: 30).Value;
        var id = FirstId(a);
        const int perWriter = 50;

        static Result<TimerState> AddOneMs(TimerState s) =>
            TimerState.Create(false, null, s.Accumulated + TimeSpan.FromMilliseconds(1));

        Parallel.Invoke(
            () => { for (var i = 0; i < perWriter; i++) Assert.True(a.Update(id, AddOneMs).IsSuccess); },
            () => { for (var i = 0; i < perWriter; i++) Assert.True(b.Update(id, AddOneMs).IsSuccess); });

        Assert.Equal(TimeSpan.FromMilliseconds(2 * perWriter), a.Get(id).Value.State.Accumulated);
    }

    [Fact]
    public void Lock_held_elsewhere_is_reported_as_busy()
    {
        var repository = SqliteTimerRepository.Open(DbPath, busyTimeoutSeconds: 1).Value;
        var id = FirstId(repository);

        using var other = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        other.Open();
        using var hold = other.BeginTransaction(deferred: false);

        Assert.Equal(ErrorCode.StorageBusy, repository.Update(id, s => s.Start(T0)).Error.Code);
    }

    // --- damage ---

    [Fact]
    public void Garbage_file_is_reported_as_foreign()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        File.WriteAllText(DbPath, "this is not a database, just some text that is long enough to have a header");

        Assert.Equal(ErrorCode.StorageForeign, SqliteTimerRepository.Open(DbPath).Error.Code);
    }

    [Fact]
    public void Newer_schema_is_refused()
    {
        Open();
        Execute("PRAGMA user_version = 99;");

        Assert.Equal(ErrorCode.StorageTooNew, SqliteTimerRepository.Open(DbPath).Error.Code);
    }

    [Fact]
    public void Missing_table_is_reported_as_corrupt()
    {
        Open();
        Execute("DROP TABLE timers;");

        Assert.Equal(ErrorCode.StorageCorrupt, SqliteTimerRepository.Open(DbPath).Error.Code);
    }

    [Fact]
    public void Empty_table_is_fine()
    {
        var repository = Open();
        repository.Delete(FirstId(repository));

        Assert.Empty(Open().List().Value);
    }

    [Theory]
    [InlineData("UPDATE timers SET started_at_utc = 'yesterday', is_running = 1;")]
    [InlineData("UPDATE timers SET is_running = 1;")]
    [InlineData("UPDATE timers SET accumulated_ms = -5;")]
    [InlineData("UPDATE timers SET is_running = 7;")]
    [InlineData("UPDATE timers SET name = '';")]
    [InlineData("UPDATE timers SET name = ' padded';")]
    [InlineData("UPDATE timers SET name = 'bad' || char(10) || 'name';")]
    public void Tampered_row_is_reported_as_corrupt(string tamper)
    {
        var repository = Open();
        var id = FirstId(repository);
        Execute("PRAGMA ignore_check_constraints = ON; " + tamper);

        Assert.Equal(ErrorCode.StorageCorrupt, repository.Get(id).Error.Code);
        Assert.Equal(ErrorCode.StorageCorrupt, repository.List().Error.Code);
        Assert.Equal(ErrorCode.StorageCorrupt, SqliteTimerRepository.Open(DbPath).Error.Code);
    }

    [Fact]
    public void Someone_elses_database_is_refused_and_left_untouched()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        Execute("CREATE TABLE notes (x TEXT); INSERT INTO notes VALUES ('precious');");
        var before = File.ReadAllBytes(DbPath);
        var modeBefore = OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(DbPath);

        var result = SqliteTimerRepository.Open(DbPath);

        Assert.Equal(ErrorCode.StorageForeign, result.Error.Code);
        Assert.Equal(before, File.ReadAllBytes(DbPath));
        Assert.Equal("delete", ScalarText("PRAGMA journal_mode;"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(modeBefore, File.GetUnixFileMode(DbPath));
    }

    [Theory]
    [InlineData("PRAGMA application_id = 0; CREATE TABLE notes (x TEXT); INSERT INTO notes VALUES ('precious');", ErrorCode.StorageForeign)]
    [InlineData("CREATE TABLE notes (x TEXT); PRAGMA application_id = 1234;", ErrorCode.StorageForeign)]
    // Our id only in the WAL, never in the main file: not something time-arr writes, so foreign.
    [InlineData("PRAGMA application_id = {ours}; PRAGMA user_version = 2; CREATE TABLE future (x);", ErrorCode.StorageForeign)]
    public void Refused_wal_database_with_pending_writes_is_left_untouched(string setup, ErrorCode expected)
    {
        // Redteam M1 round 2 #1: another app crashed (or is still running) with writes only in
        // its -wal file. Refusing it must not checkpoint that WAL or delete -wal/-shm.
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        var sourceDb = Path.Combine(source, "other.db");
        using (var writer = new SqliteConnection($"Data Source={sourceDb};Pooling=False"))
        {
            writer.Open();
            using var command = writer.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA wal_autocheckpoint = 0; "
                + setup.Replace("{ours}", SqliteTimerRepository.ApplicationId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            command.ExecuteNonQuery();
            // Copy while the writer is open, so the copy has uncheckpointed frames in its -wal.
            foreach (var suffix in Suffixes)
                File.Copy(sourceDb + suffix, DbPath + suffix);
        }
        var before = Suffixes.Select(s => File.ReadAllBytes(DbPath + s)).ToArray();

        Assert.Equal(expected, SqliteTimerRepository.Open(DbPath).Error.Code);

        Assert.True(File.Exists(DbPath + "-wal"));
        Assert.Equal(before[0], File.ReadAllBytes(DbPath));
        Assert.Equal(before[1], File.ReadAllBytes(DbPath + "-wal"));
    }

    /// <summary>Copies the db and its side files while <paramref name="write"/> is mid-flight on another connection.</summary>
    private void CopyWhileWriting(string source, string setup, string pending, bool commitPending)
    {
        using var writer = new SqliteConnection($"Data Source={source};Pooling=False");
        writer.Open();
        using (var command = writer.CreateCommand())
        {
            command.CommandText = setup;
            command.ExecuteNonQuery();
        }
        using var transaction = writer.BeginTransaction();
        using (var command = writer.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = pending;
            command.ExecuteNonQuery();
        }
        if (commitPending)
            transaction.Commit();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            if (File.Exists(source + suffix))
                File.Copy(source + suffix, DbPath + suffix, overwrite: true);
        }
    }

    [Fact]
    public void Our_rollback_database_with_a_hot_journal_recovers()
    {
        // Redteam M1 round 3 #1: a crash mid-write in rollback mode (filesystems without WAL).
        var source = Path.Combine(_root, "source", "time-arr.db");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        var original = SqliteTimerRepository.Open(source).Value;
        original.Create("kept");

        CopyWhileWriting(source,
            "PRAGMA journal_mode = DELETE; PRAGMA cache_size = 1;",
            "UPDATE timers SET name = 'half-written' WHERE name = 'kept'; WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 400) " +
            "INSERT INTO timers (name, is_running, accumulated_ms) SELECT 'bulk' || i, 0, 0 FROM n;",
            commitPending: false);
        Assert.True(File.Exists(DbPath + "-journal"));

        var reopened = SqliteTimerRepository.Open(DbPath);

        Assert.True(reopened.IsSuccess, reopened.ToString());
        Assert.Equal(["default", "kept"], reopened.Value.List().Value.Select(t => t.Name));
    }

    [Fact]
    public void Refused_wal_database_gets_no_new_side_files()
    {
        // Redteam M1 round 3 #2: no -shm may appear next to a database we refuse.
        var source = Path.Combine(_root, "source", "other.db");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        CopyWhileWriting(source, "PRAGMA journal_mode = WAL; PRAGMA wal_autocheckpoint = 0; CREATE TABLE notes (x);",
            "INSERT INTO notes VALUES (1);", commitPending: true);
        File.Delete(DbPath + "-shm");

        Assert.Equal(ErrorCode.StorageForeign, SqliteTimerRepository.Open(DbPath).Error.Code);
        Assert.False(File.Exists(DbPath + "-shm"));
    }

    [Fact]
    public void Our_wal_database_bumped_by_a_newer_time_arr_is_refused_untouched()
    {
        // The realistic "too new" case: our file, then a newer build bumps user_version and
        // hasn't checkpointed yet, so the main file's header still says version 1.
        var source = Path.Combine(_root, "source", "time-arr.db");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        SqliteTimerRepository.Open(source);
        CopyWhileWriting(source, "PRAGMA wal_autocheckpoint = 0;", "PRAGMA user_version = 2;", commitPending: true);
        var main = File.ReadAllBytes(DbPath);
        var wal = File.ReadAllBytes(DbPath + "-wal");

        Assert.Equal(ErrorCode.StorageTooNew, SqliteTimerRepository.Open(DbPath).Error.Code);
        Assert.Equal(main, File.ReadAllBytes(DbPath));
        Assert.Equal(wal, File.ReadAllBytes(DbPath + "-wal"));
    }

    [Fact]
    public void Too_new_database_is_refused_and_left_untouched()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        Execute($"PRAGMA application_id = {SqliteTimerRepository.ApplicationId}; PRAGMA user_version = 2; CREATE TABLE future (x);");
        var before = File.ReadAllBytes(DbPath);

        Assert.Equal(ErrorCode.StorageTooNew, SqliteTimerRepository.Open(DbPath).Error.Code);
        Assert.Equal(before, File.ReadAllBytes(DbPath));
        Assert.Equal("delete", ScalarText("PRAGMA journal_mode;"));
    }

    [Fact]
    public void New_database_is_marked_as_time_arrs_and_uses_wal()
    {
        Open();

        Assert.Equal(SqliteTimerRepository.ApplicationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     ScalarText("PRAGMA application_id;"));
        Assert.Equal("wal", ScalarText("PRAGMA journal_mode;"));
    }

    [Fact]
    public void Read_only_database_stays_read_only()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return;

        Open();
        File.SetUnixFileMode(DbPath, UnixFileMode.UserRead);

        var repository = SqliteTimerRepository.Open(DbPath);

        Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(DbPath));
        if (repository.IsSuccess)
            Assert.True(repository.Value.Create("x").IsFailure);
    }

    [Fact]
    public void Path_with_nul_is_invalid_input()
    {
        Assert.Equal(ErrorCode.InvalidInput, SqliteTimerRepository.Open(Path.Combine(_root, "bad\0name", "t.db")).Error.Code);
    }

    [Fact]
    public void Ok_with_null_from_a_change_is_a_bug()
    {
        var repository = Open();

        Assert.Throws<ArgumentNullException>(() => repository.Update(FirstId(repository), _ => Result<TimerState>.Ok(null!)));
        Assert.Equal(TimerState.Zero, repository.Get(FirstId(repository)).Value.State);
    }

    private string? ScalarText(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False;Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
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
