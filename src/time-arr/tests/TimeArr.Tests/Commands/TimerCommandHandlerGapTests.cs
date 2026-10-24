using Microsoft.Data.Sqlite;
using TimeArr.Commands;
using TimeArr.Core;
using TimeArr.Data;

namespace TimeArr.Tests.Commands;

/// <summary>Wraps real storage and lets a test fail chosen Open calls and count them.</summary>
public sealed class ScriptedStorage(ITimerStorage inner) : ITimerStorage
{
    private int _opens;

    /// <summary>Given the 1-based number of the Open call, the error to fail it with, or null to open for real.</summary>
    public Func<int, Error?> FailOpen { get; set; } = _ => null;

    public int Opens => Volatile.Read(ref _opens);
    public string Location => inner.Location;

    public Result<ITimerRepository> Open()
    {
        var call = Interlocked.Increment(ref _opens);
        return FailOpen(call) is { } error ? Result<ITimerRepository>.Fail(error) : inner.Open();
    }
}

/// <summary>Edge cases for <see cref="TimerCommandHandler"/> not covered by <see cref="TimerCommandHandlerTests"/>.</summary>
public sealed class TimerCommandHandlerGapTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimerRef Default = new TimerRef.Default();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "time-arr-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(T0);
    private string DbPath => Path.Combine(_root, "time-arr", "time-arr.db");
    private string Folder => Path.GetDirectoryName(DbPath)!;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private TimerCommandHandler Handler() => new(new SqliteTimerStorage(DbPath), _clock);

    private (TimerCommandHandler Handler, ScriptedStorage Storage) Scripted()
    {
        var storage = new ScriptedStorage(new SqliteTimerStorage(DbPath));
        return (new TimerCommandHandler(storage, _clock), storage);
    }

    /// <summary>Runs a command, failing the test if the handler throws instead of returning a Result.</summary>
    private static Result<CommandReply> Run(TimerCommandHandler handler, TimerCommand command)
    {
        Result<CommandReply>? result = null;
        Assert.Null(Record.Exception(() => result = handler.Execute(command)));
        return result!;
    }

    private static TimerEntry Only(Result<CommandReply> reply)
    {
        Assert.True(reply.IsSuccess, reply.ToString());
        return Assert.Single(reply.Value.Timers);
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Damages our own database in a way the structural row check catches.</summary>
    private void DamageRows() => Execute("PRAGMA ignore_check_constraints = ON; UPDATE timers SET is_running = 7;");

    private void DeleteDatabase()
    {
        File.Delete(DbPath);
        File.Delete(DbPath + "-wal");
        File.Delete(DbPath + "-shm");
    }

    // --- construction and bad input ---

    [Fact]
    public void Null_dependencies_are_a_bug()
    {
        Assert.Throws<ArgumentNullException>(() => new TimerCommandHandler(null!, _clock));
        Assert.Throws<ArgumentNullException>(() => new TimerCommandHandler(new SqliteTimerStorage(DbPath), null!));
        Assert.Throws<ArgumentNullException>(() => Handler().Execute(null!));
    }

    [Fact]
    public void Null_parts_of_a_command_are_invalid_input_not_exceptions()
    {
        var handler = Handler();

        Assert.Equal(ErrorCode.InvalidInput, Run(handler, new TimerCommand.Add(null!)).Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, Run(handler, new TimerCommand.Rename(Default, null!)).Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, Run(handler, new TimerCommand.Get(null!)).Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, Run(handler, new TimerCommand.Start(new TimerRef.ByName(null!))).Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, Run(handler, new TimerCommand.Delete(new TimerRef.ByName("   "))).Error.Code);
    }

    [Fact]
    public void Huge_id_is_not_found()
    {
        Assert.Equal(ErrorCode.NotFound, Run(Handler(), new TimerCommand.Get(new TimerRef.ById(long.MaxValue))).Error.Code);
    }

    [Fact]
    public void Names_are_trimmed_and_matched_ignoring_ascii_case()
    {
        var handler = Handler();
        var work = Only(Run(handler, new TimerCommand.Add("  Work ")));

        Assert.Equal("Work", work.Name);
        Assert.Equal(work.Id, Only(Run(handler, new TimerCommand.Start(new TimerRef.ByName("  WORK  ")))).Id);
    }

    [Fact]
    public void Default_means_whichever_timer_is_named_default_now()
    {
        var handler = Handler();
        Run(handler, new TimerCommand.Rename(Default, "old"));
        Assert.Equal(ErrorCode.NotFound, Run(handler, new TimerCommand.Get(Default)).Error.Code);

        var other = Only(Run(handler, new TimerCommand.Add("other")));
        Run(handler, new TimerCommand.Rename(new TimerRef.ById(other.Id), "DEFAULT"));

        Assert.Equal(other.Id, Only(Run(handler, new TimerCommand.Get(Default))).Id);
    }

    [Fact]
    public void An_id_is_never_read_as_a_name()
    {
        var handler = Handler();
        var other = Only(Run(handler, new TimerCommand.Add("other")));

        // There is no timer *named* after other's id, so ByName must not fall back to the id.
        var byName = Run(handler, new TimerCommand.Get(new TimerRef.ByName(other.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))));

        Assert.Equal(ErrorCode.NotFound, byName.Error.Code);
    }

    // --- replies ---

    [Fact]
    public void Reply_carries_the_time_the_command_ran_at()
    {
        var handler = Handler();
        _clock.Advance(TimeSpan.FromMinutes(7));

        Assert.Equal(T0.AddMinutes(7), Run(handler, new TimerCommand.Start(Default)).Value.NowUtc);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(T0.AddMinutes(8), Run(handler, new TimerCommand.List()).Value.NowUtc);
    }

    [Fact]
    public void Delete_replies_with_the_state_before_deletion()
    {
        var handler = Handler();
        Run(handler, new TimerCommand.Start(Default));
        _clock.Advance(TimeSpan.FromMinutes(3));

        var reply = Run(handler, new TimerCommand.Delete(Default)).Value;

        var deleted = Assert.Single(reply.Timers);
        Assert.True(deleted.State.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(3), deleted.State.ElapsedAt(reply.NowUtc));
    }

    [Fact]
    public void Start_twice_keeps_the_first_start_time()
    {
        var handler = Handler();
        Run(handler, new TimerCommand.Start(Default));
        _clock.Advance(TimeSpan.FromMinutes(5));

        var again = Only(Run(handler, new TimerCommand.Start(Default)));

        Assert.Equal(T0, again.State.StartedAtUtc);
    }

    // --- restarts and the clock ---

    [Fact]
    public void Paused_timer_does_not_count_across_a_restart()
    {
        var first = Handler();
        Run(first, new TimerCommand.Start(Default));
        _clock.Advance(TimeSpan.FromMinutes(10));
        Run(first, new TimerCommand.Pause(Default));
        _clock.Advance(TimeSpan.FromHours(12));

        var reply = Run(Handler(), new TimerCommand.Get(Default)).Value;

        Assert.Equal(TimeSpan.FromMinutes(10), reply.Timers[0].State.ElapsedAt(reply.NowUtc));
    }

    [Fact]
    public void Reset_of_a_running_timer_keeps_running_across_a_restart()
    {
        var first = Handler();
        Run(first, new TimerCommand.Start(Default));
        _clock.Advance(TimeSpan.FromHours(1));
        Run(first, new TimerCommand.Reset(Default));
        _clock.Advance(TimeSpan.FromMinutes(2));

        var reply = Run(Handler(), new TimerCommand.Get(Default)).Value;

        Assert.True(reply.Timers[0].State.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(2), reply.Timers[0].State.ElapsedAt(reply.NowUtc));
    }

    [Fact]
    public void Clock_set_back_across_a_restart_never_goes_negative()
    {
        Run(Handler(), new TimerCommand.Start(Default));
        _clock.Now = T0.AddHours(-2);

        var handler = Handler();
        var reply = Run(handler, new TimerCommand.Get(Default)).Value;
        var paused = Only(Run(handler, new TimerCommand.Pause(Default)));

        Assert.Equal(TimeSpan.Zero, reply.Timers[0].State.ElapsedAt(reply.NowUtc));
        Assert.Equal(TimeSpan.Zero, paused.State.Accumulated);
    }

    // --- opening and StorageProblem ---

    [Fact]
    public void Storage_is_not_opened_until_the_first_command()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(DbPath, "this is not a database, just some text that is long enough to have a header....");
        var (handler, storage) = Scripted();

        Assert.Null(handler.StorageProblem);
        Assert.Equal(0, storage.Opens);
    }

    [Fact]
    public void Successful_open_happens_once_even_under_concurrent_first_commands()
    {
        var (handler, storage) = Scripted();

        Parallel.For(0, 50, _ => Assert.True(Run(handler, new TimerCommand.List()).IsSuccess));

        Assert.Equal(1, storage.Opens);
    }

    [Fact]
    public void Storage_problem_follows_the_latest_reason_and_clears_on_success()
    {
        var (handler, storage) = Scripted();
        storage.FailOpen = call => call switch
        {
            1 => new Error(ErrorCode.StorageUnavailable, "disk full"),
            2 => new Error(ErrorCode.StorageBusy, "locked"),
            _ => null,
        };

        Assert.Equal(ErrorCode.StorageUnavailable, Run(handler, new TimerCommand.List()).Error.Code);
        Assert.Equal("disk full", handler.StorageProblem!.Message);
        Assert.Equal(ErrorCode.StorageBusy, Run(handler, new TimerCommand.Start(Default)).Error.Code);
        Assert.Equal(ErrorCode.StorageBusy, handler.StorageProblem!.Code);
        Assert.True(Run(handler, new TimerCommand.List()).IsSuccess);
        Assert.Null(handler.StorageProblem);
        Assert.Equal(3, storage.Opens);
    }

    [Theory]
    [InlineData("relative/time-arr.db", 5)]
    [InlineData(null, 0)]
    public void Misconfigured_storage_is_a_result_and_a_storage_problem(string? path, int busySeconds)
    {
        var handler = new TimerCommandHandler(new SqliteTimerStorage(path ?? DbPath, busySeconds), _clock);

        Assert.Equal(ErrorCode.InvalidInput, Run(handler, new TimerCommand.List()).Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, handler.StorageProblem!.Code);
    }

    [Fact]
    public void Ordinary_failures_keep_the_open_database()
    {
        var (handler, storage) = Scripted();
        Run(handler, new TimerCommand.List());

        Run(handler, new TimerCommand.Get(new TimerRef.ByName("nope")));       // NotFound
        Run(handler, new TimerCommand.Add(""));                                // InvalidInput
        Run(handler, new TimerCommand.Add(TimerRef.DefaultName));              // Conflict
        Run(handler, new TimerCommand.Start(new TimerRef.ById(0)));            // InvalidInput
        Run(handler, new TimerCommand.List());

        Assert.Equal(1, storage.Opens);
        Assert.Null(handler.StorageProblem);
    }

    [Fact]
    public void Busy_write_keeps_the_open_database_and_the_next_command_works()
    {
        var storage = new ScriptedStorage(new SqliteTimerStorage(DbPath, busyTimeoutSeconds: 1));
        var handler = new TimerCommandHandler(storage, _clock);
        Run(handler, new TimerCommand.List());

        using (var other = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            other.Open();
            using var hold = other.BeginTransaction(deferred: false);

            Assert.Equal(ErrorCode.StorageBusy, Run(handler, new TimerCommand.Start(Default)).Error.Code);
            Assert.Null(handler.StorageProblem);
        }

        Assert.True(Run(handler, new TimerCommand.Start(Default)).IsSuccess);
        Assert.Equal(1, storage.Opens);
    }

    // --- damage and other trouble after opening ---

    [Fact]
    public void Damage_found_after_opening_is_reported_and_the_file_left_alone()
    {
        var handler = Handler();
        Run(handler, new TimerCommand.List());
        DamageRows();
        var before = File.ReadAllBytes(DbPath);

        Assert.Equal(ErrorCode.StorageCorrupt, Run(handler, new TimerCommand.List()).Error.Code);
        Assert.Equal(ErrorCode.StorageCorrupt, handler.StorageProblem?.Code);
        Assert.Equal(ErrorCode.StorageCorrupt, Run(handler, new TimerCommand.Start(Default)).Error.Code);
        Assert.Equal(ErrorCode.StorageCorrupt, handler.StorageProblem?.Code);
        Assert.Equal(before, File.ReadAllBytes(DbPath));
        Assert.Single(Directory.GetFiles(Folder), f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal));
    }

    [Fact]
    public void After_the_user_removes_a_damaged_file_the_next_command_starts_fresh()
    {
        var handler = Handler();
        Run(handler, new TimerCommand.Add("lost"));
        DamageRows();
        Assert.Equal(ErrorCode.StorageCorrupt, Run(handler, new TimerCommand.List()).Error.Code);

        DeleteDatabase();
        var fresh = Run(handler, new TimerCommand.List());

        Assert.Equal(TimerRef.DefaultName, Assert.Single(fresh.Value.Timers).Name);
        Assert.Null(handler.StorageProblem);
    }

    [Fact]
    public void After_the_user_repairs_the_rows_the_next_command_works_on_the_same_data()
    {
        var handler = Handler();
        var kept = Only(Run(handler, new TimerCommand.Add("kept")));
        DamageRows();
        Assert.Equal(ErrorCode.StorageCorrupt, Run(handler, new TimerCommand.List()).Error.Code);

        Execute("UPDATE timers SET is_running = 0;");

        Assert.Equal(kept.Id, Only(Run(handler, new TimerCommand.Get(new TimerRef.ByName("kept")))).Id);
        Assert.Null(handler.StorageProblem);
    }

    [Fact]
    public void Database_deleted_while_running_is_recreated_on_a_later_command()
    {
        var (handler, storage) = Scripted();
        Run(handler, new TimerCommand.Add("gone"));
        DeleteDatabase();

        var first = Run(handler, new TimerCommand.List());
        var later = first.IsSuccess ? first : Run(handler, new TimerCommand.List());

        Assert.True(later.IsSuccess, later.ToString());
        Assert.Equal(TimerRef.DefaultName, Assert.Single(later.Value.Timers).Name);
        Assert.True(storage.Opens >= 2);
        Assert.Null(handler.StorageProblem);
    }

    [Fact]
    public void Newer_schema_appearing_while_running_is_only_noticed_on_the_next_open()
    {
        // Pins current behaviour (flagged in the report): the open repository doesn't re-check
        // user_version, so it keeps reading and writing; a fresh handler refuses.
        var handler = Handler();
        Run(handler, new TimerCommand.List());
        Execute("PRAGMA user_version = 9;");

        Assert.True(Run(handler, new TimerCommand.Start(Default)).IsSuccess);
        Assert.Equal(ErrorCode.StorageTooNew, Run(Handler(), new TimerCommand.List()).Error.Code);
        Assert.Equal(9L, ScalarLong("PRAGMA user_version;"));
    }

    [Fact]
    public void Another_apps_database_is_foreign_and_never_touched()
    {
        Directory.CreateDirectory(Folder);
        Execute("CREATE TABLE notes (x TEXT); INSERT INTO notes VALUES ('precious');");
        var before = File.ReadAllBytes(DbPath);
        var handler = Handler();

        Assert.Equal(ErrorCode.StorageForeign, Run(handler, new TimerCommand.List()).Error.Code);
        Assert.Equal(ErrorCode.StorageForeign, handler.StorageProblem!.Code);
        Assert.Equal(ErrorCode.StorageForeign, Run(handler, new TimerCommand.Add("x")).Error.Code);
        Assert.Equal(before, File.ReadAllBytes(DbPath));
        Assert.Single(Directory.GetFiles(Folder));
    }

    [Fact]
    public void Text_file_at_the_path_is_foreign()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(DbPath, "this is not a database, just some text that is long enough to have a header....");

        Assert.Equal(ErrorCode.StorageForeign, Run(Handler(), new TimerCommand.List()).Error.Code);
    }

    // --- threads ---

    [Fact]
    public void Commands_racing_damage_and_repair_all_return_results()
    {
        var handler = Handler();
        Run(handler, new TimerCommand.List());
        var results = new System.Collections.Concurrent.ConcurrentBag<Result<CommandReply>>();

        // Damage lands somewhere in the middle of the burst (Parallel.For runs indexes in any order).
        Parallel.For(0, 90, i =>
        {
            if (i == 45)
                DamageRows();
            else
                results.Add(Run(handler, i % 2 == 0 ? new TimerCommand.List() : new TimerCommand.Pause(Default)));
        });

        Assert.All(results.Where(r => r.IsFailure),
            r => Assert.True(r.Error.Code is ErrorCode.StorageCorrupt or ErrorCode.StorageBusy, r.ToString()));
        Assert.Equal(ErrorCode.StorageCorrupt, Run(handler, new TimerCommand.List()).Error.Code);

        Execute("UPDATE timers SET is_running = 0, started_at_utc = NULL;");
        Assert.True(Run(handler, new TimerCommand.List()).IsSuccess);
        Assert.Null(handler.StorageProblem);
    }

    [Fact]
    public async Task Storage_problem_can_be_read_while_a_command_holds_the_gate()
    {
        // StorageProblem must never block: read it from another thread while Open is stuck.
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var storage = new ScriptedStorage(new SqliteTimerStorage(DbPath))
        {
            FailOpen = _ =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return null;
            },
        };
        var handler = new TimerCommandHandler(storage, _clock);

        var command = Task.Run(() => handler.Execute(new TimerCommand.List()));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var read = Task.Run(() => handler.StorageProblem);

        var first = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(2)));
        release.Set();
        Assert.True(first == read, "StorageProblem blocked while Open was running.");
        Assert.True((await command).IsSuccess);
    }

    [Fact]
    public void Concurrent_adds_of_one_name_on_one_handler_give_exactly_one_timer()
    {
        var handler = Handler();
        var results = new System.Collections.Concurrent.ConcurrentBag<Result<CommandReply>>();

        Parallel.For(0, 20, i => results.Add(Run(handler, new TimerCommand.Add(i % 2 == 0 ? "shared" : "SHARED"))));

        Assert.Equal(1, results.Count(r => r.IsSuccess));
        Assert.All(results.Where(r => r.IsFailure), r => Assert.Equal(ErrorCode.Conflict, r.Error.Code));
    }

    [Fact]
    public void Concurrent_commands_on_different_timers_do_not_disturb_each_other()
    {
        var handler = Handler();
        var ids = Enumerable.Range(0, 8).Select(i => Only(Run(handler, new TimerCommand.Add($"t{i}"))).Id).ToArray();

        Parallel.ForEach(ids, id =>
        {
            for (var n = 0; n < 10; n++)
            {
                Assert.True(Run(handler, new TimerCommand.Start(new TimerRef.ById(id))).IsSuccess);
                Assert.True(Run(handler, new TimerCommand.Pause(new TimerRef.ById(id))).IsSuccess);
            }
            if (id % 2 == 0)
                Assert.True(Run(handler, new TimerCommand.Start(new TimerRef.ById(id))).IsSuccess);
        });

        var timers = Run(handler, new TimerCommand.List()).Value.Timers.Where(t => ids.Contains(t.Id));
        Assert.All(timers, t => Assert.Equal(t.Id % 2 == 0, t.State.IsRunning));
    }

    private long ScalarLong(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False;Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
