using TimeArr.Commands;
using TimeArr.Core;
using TimeArr.Data;

namespace TimeArr.Tests.Commands;

/// <summary>A clock the test moves by hand.</summary>
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public sealed class TimerCommandHandlerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "time-arr-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(T0);
    private string DbPath => Path.Combine(_root, "time-arr", "time-arr.db");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private TimerCommandHandler Handler() => new(new SqliteTimerStorage(DbPath), _clock);

    private static TimerEntry Only(Result<CommandReply> reply)
    {
        Assert.True(reply.IsSuccess, reply.ToString());
        return Assert.Single(reply.Value.Timers);
    }

    private static readonly TimerRef Default = new TimerRef.Default();

    // --- basics ---

    [Fact]
    public void List_shows_the_default_timer_of_a_new_database()
    {
        var reply = Handler().Execute(new TimerCommand.List()).Value;

        Assert.Equal(TimerRef.DefaultName, Assert.Single(reply.Timers).Name);
        Assert.Equal(T0, reply.NowUtc);
    }

    [Fact]
    public void Start_pause_and_reset_use_the_clock()
    {
        var handler = Handler();

        Assert.True(Only(handler.Execute(new TimerCommand.Start(Default))).State.IsRunning);
        _clock.Advance(TimeSpan.FromMinutes(25));
        var paused = Only(handler.Execute(new TimerCommand.Pause(Default)));
        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromMinutes(25), paused.State.ElapsedAt(_clock.Now));
        Assert.Equal(TimerState.Zero, Only(handler.Execute(new TimerCommand.Reset(Default))).State);
    }

    [Fact]
    public void Running_timer_keeps_counting_across_a_restart()
    {
        Handler().Execute(new TimerCommand.Start(Default));
        _clock.Advance(TimeSpan.FromHours(8)); // app closed overnight

        var reply = Handler().Execute(new TimerCommand.Get(Default)).Value;

        Assert.Equal(TimeSpan.FromHours(8), reply.Timers[0].State.ElapsedAt(reply.NowUtc));
    }

    [Fact]
    public void Timers_can_be_addressed_by_name_or_id()
    {
        var handler = Handler();
        var work = Only(handler.Execute(new TimerCommand.Add("Work")));

        Assert.True(Only(handler.Execute(new TimerCommand.Start(new TimerRef.ByName("work")))).State.IsRunning);
        Assert.False(Only(handler.Execute(new TimerCommand.Pause(new TimerRef.ById(work.Id)))).State.IsRunning);
    }

    [Fact]
    public void A_name_made_of_digits_is_a_name_not_an_id()
    {
        var handler = Handler();
        var defaultId = Only(handler.Execute(new TimerCommand.Get(Default))).Id;
        var named = Only(handler.Execute(new TimerCommand.Add(defaultId.ToString(System.Globalization.CultureInfo.InvariantCulture))));

        var byName = Only(handler.Execute(new TimerCommand.Start(new TimerRef.ByName(named.Name))));

        Assert.Equal(named.Id, byName.Id);
        Assert.False(Only(handler.Execute(new TimerCommand.Get(new TimerRef.ById(defaultId)))).State.IsRunning);
    }

    [Fact]
    public void Rename_and_delete_reply_with_the_timer()
    {
        var handler = Handler();

        Assert.Equal("main", Only(handler.Execute(new TimerCommand.Rename(Default, "main"))).Name);
        var deleted = Only(handler.Execute(new TimerCommand.Delete(new TimerRef.ByName("main"))));

        Assert.Equal("main", deleted.Name);
        Assert.Empty(handler.Execute(new TimerCommand.List()).Value.Timers);
    }

    // --- failures are Results ---

    [Fact]
    public void Missing_default_timer_asks_for_a_name()
    {
        var handler = Handler();
        handler.Execute(new TimerCommand.Delete(Default));

        var result = handler.Execute(new TimerCommand.Start(Default));

        Assert.Equal(ErrorCode.NotFound, result.Error.Code);
        Assert.Contains("say which timer", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-3L)]
    public void Non_positive_ids_are_invalid(long id)
    {
        Assert.Equal(ErrorCode.InvalidInput, Handler().Execute(new TimerCommand.Start(new TimerRef.ById(id))).Error.Code);
    }

    [Fact]
    public void Unknown_timers_are_not_found()
    {
        var handler = Handler();

        Assert.Equal(ErrorCode.NotFound, handler.Execute(new TimerCommand.Start(new TimerRef.ById(999))).Error.Code);
        Assert.Equal(ErrorCode.NotFound, handler.Execute(new TimerCommand.Delete(new TimerRef.ByName("nope"))).Error.Code);
        Assert.Equal(ErrorCode.NotFound, handler.Execute(new TimerCommand.Rename(new TimerRef.ByName("nope"), "x")).Error.Code);
    }

    [Fact]
    public void Bad_names_are_invalid_input()
    {
        var handler = Handler();

        Assert.Equal(ErrorCode.InvalidInput, handler.Execute(new TimerCommand.Add("")).Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, handler.Execute(new TimerCommand.Rename(Default, "-x")).Error.Code);
        Assert.Equal(ErrorCode.Conflict, handler.Execute(new TimerCommand.Add("DEFAULT")).Error.Code);
    }

    [Fact]
    public void Concurrent_commands_from_many_threads_all_complete()
    {
        var handler = Handler();
        var failures = 0;

        Parallel.For(0, 200, i =>
        {
            TimerCommand command = (i % 3) switch
            {
                0 => new TimerCommand.Start(Default),
                1 => new TimerCommand.Pause(Default),
                _ => new TimerCommand.List(),
            };
            if (handler.Execute(command).IsFailure)
                Interlocked.Increment(ref failures);
        });

        Assert.Equal(0, failures);
    }

    // --- storage problems: reported, never repaired (user decision) ---

    private void Execute(string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Someone_elses_file_is_reported_and_left_alone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        File.WriteAllText(DbPath, "this is not a database, just some text that is long enough to have a header....");
        var before = File.ReadAllBytes(DbPath);
        var handler = Handler();

        Assert.Equal(ErrorCode.StorageForeign, handler.Execute(new TimerCommand.List()).Error.Code);
        Assert.Equal(ErrorCode.StorageForeign, handler.StorageProblem!.Code);
        Assert.Equal(before, File.ReadAllBytes(DbPath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(DbPath)!));
    }

    [Fact]
    public void Damage_found_while_running_is_reported_and_clears_once_fixed()
    {
        // Reviewer M2 #1 / redteam M2 #2: the problem must show without a restart.
        var handler = Handler();
        Assert.True(handler.Execute(new TimerCommand.List()).IsSuccess);
        Execute("PRAGMA ignore_check_constraints = ON; UPDATE timers SET name = ' bad';");

        Assert.Equal(ErrorCode.StorageCorrupt, handler.Execute(new TimerCommand.List()).Error.Code);
        Assert.Equal(ErrorCode.StorageCorrupt, handler.StorageProblem!.Code);

        Execute("UPDATE timers SET name = 'fixed';");

        Assert.Equal("fixed", Assert.Single(handler.Execute(new TimerCommand.List()).Value.Timers).Name);
        Assert.Null(handler.StorageProblem);
    }

    [Fact]
    public void Database_deleted_while_running_is_recreated_like_on_a_restart()
    {
        // Redteam M2 #3.
        var handler = Handler();
        handler.Execute(new TimerCommand.Add("gone soon"));
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(DbPath)!))
            File.Delete(file);

        var first = handler.Execute(new TimerCommand.List());
        var second = first.IsSuccess ? first : handler.Execute(new TimerCommand.List());

        Assert.True(second.IsSuccess, second.ToString());
        Assert.Equal(TimerRef.DefaultName, Assert.Single(second.Value.Timers).Name);
    }

    [Fact]
    public void Newer_time_arrs_database_is_reported()
    {
        SqliteTimerRepository.Open(DbPath);
        Execute("PRAGMA user_version = 9;");

        var handler = Handler();

        Assert.Equal(ErrorCode.StorageTooNew, handler.Execute(new TimerCommand.List()).Error.Code);
        Assert.Equal(ErrorCode.StorageTooNew, handler.StorageProblem!.Code);
    }

    // --- time ---

    /// <summary>A repository whose next Update waits for a signal before taking the lock.</summary>
    private sealed class StallingRepository(ITimerRepository inner) : ITimerRepository
    {
        public ManualResetEventSlim Reached { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public volatile bool StallNext;

        public Result<IReadOnlyList<TimerEntry>> List() => inner.List();
        public Result<TimerEntry> Get(long id) => inner.Get(id);
        public Result<TimerEntry> FindByName(string name) => inner.FindByName(name);
        public Result<TimerEntry> Create(string name) => inner.Create(name);
        public Result<TimerEntry> Rename(long id, string name) => inner.Rename(id, name);
        public Result Delete(long id) => inner.Delete(id);

        public Result<TimerEntry> Update(long id, Func<TimerState, Result<TimerState>> change)
        {
            if (StallNext)
            {
                StallNext = false;
                Reached.Set();
                Release.Wait();
            }
            return inner.Update(id, change);
        }
    }

    private sealed class FixedStorage(ITimerRepository repository) : ITimerStorage
    {
        public string Location => "test";
        public Result<ITimerRepository> Open() => Result<ITimerRepository>.Ok(repository);
    }

    [Fact]
    public async Task A_delayed_start_takes_effect_when_it_runs()
    {
        // Redteam M2 #4: the clock must be read under the write lock, not when the command arrives.
        var stalling = new StallingRepository(SqliteTimerRepository.Open(DbPath).Value);
        var handler = new TimerCommandHandler(new FixedStorage(stalling), _clock);

        _clock.Now = T0.AddSeconds(100);
        stalling.StallNext = true;
        var late = Task.Run(() => handler.Execute(new TimerCommand.Start(Default)));
        stalling.Reached.Wait();

        _clock.Now = T0.AddSeconds(200);
        handler.Execute(new TimerCommand.Start(Default));
        _clock.Now = T0.AddSeconds(300);
        handler.Execute(new TimerCommand.Pause(Default));

        _clock.Now = T0.AddSeconds(400);
        stalling.Release.Set();
        var reply = (await late).Value;

        Assert.Equal(T0.AddSeconds(400), reply.NowUtc);
        Assert.Equal(TimeSpan.FromSeconds(100), reply.Timers[0].State.ElapsedAt(T0.AddSeconds(400)));
    }

    [Fact]
    public void Open_is_retried_after_a_failure()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return;

        Directory.CreateDirectory(_root);
        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var handler = Handler();
        Assert.Equal(ErrorCode.StorageUnavailable, handler.Execute(new TimerCommand.List()).Error.Code);

        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.True(handler.Execute(new TimerCommand.List()).IsSuccess);
        Assert.Null(handler.StorageProblem);
    }
}
