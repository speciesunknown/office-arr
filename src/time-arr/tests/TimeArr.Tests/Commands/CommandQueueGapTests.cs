using System.Collections.Concurrent;
using TimeArr.Commands;
using TimeArr.Core;
using TimeArr.Data;
using TimeArr.ViewModels;

namespace TimeArr.Tests.Commands;

/// <summary>Edge cases for <see cref="CommandQueue"/> and <see cref="Requester"/> not covered by <see cref="CommandQueueTests"/>.</summary>
public sealed class CommandQueueGapTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "time-arr-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static TimerCommand.Get Named(string name) => new(new TimerRef.ByName(name));

    private TimerCommandHandler Handler() =>
        new(new SqliteTimerStorage(Path.Combine(_root, "time-arr", "time-arr.db")), new FakeClock(T0));

    // --- order and fairness ---

    [Fact]
    public async Task Three_requesters_take_strict_turns()
    {
        var commands = new RecordingCommands { HoldFirst = true };
        using var queue = new CommandQueue(commands);
        var a = queue.Join();
        var b = queue.Join();
        var c = queue.Join();

        var first = a.Send(Named("a1"));
        Assert.True(commands.Started.Wait(Wait));
        Task[] rest = [a.Send(Named("a2")), a.Send(Named("a3")), b.Send(Named("b1")), b.Send(Named("b2")), c.Send(Named("c1"))];
        commands.Release.Set();
        await Task.WhenAll([first, .. rest]).WaitAsync(Wait);

        // The holder goes to the back of the line after each command, so one command from each requester at a time.
        Assert.Equal(["a1", "b1", "c1", "a2", "b2", "a3"], commands.Ran);
    }

    [Fact]
    public async Task A_requester_joining_late_waits_only_for_the_command_running_now()
    {
        var commands = new RecordingCommands { HoldFirst = true };
        using var queue = new CommandQueue(commands);
        var flood = queue.Join();

        var sends = new List<Task> { flood.Send(Named("f0")) };
        Assert.True(commands.Started.Wait(Wait));
        for (var i = 1; i <= 50; i++)
            sends.Add(flood.Send(Named($"f{i}")));
        var late = queue.Join();
        sends.Add(late.Send(Named("late")));
        commands.Release.Set();
        await Task.WhenAll(sends).WaitAsync(Wait);

        // Straight after the command that was running when it joined, ahead of the flood's 50-command backlog.
        Assert.Equal(1, commands.Ran.ToList().IndexOf("late"));
    }

    [Fact]
    public async Task One_thread_per_requester_keeps_each_requesters_order_under_load()
    {
        var commands = new RecordingCommands();
        using var queue = new CommandQueue(commands);
        const int requesters = 16, each = 200;

        var sends = new Task[requesters * each];
        Parallel.For(0, requesters, r =>
        {
            var requester = queue.Join();
            for (var i = 0; i < each; i++)
                sends[(r * each) + i] = requester.Send(Named($"{r}:{i}"));
        });
        await Task.WhenAll(sends).WaitAsync(Wait);

        var ran = commands.Ran;
        Assert.Equal(requesters * each, ran.Count);
        for (var r = 0; r < requesters; r++)
        {
            var mine = ran.Where(n => n.StartsWith($"{r}:", StringComparison.Ordinal)).Select(n => int.Parse(n[(n.IndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(Enumerable.Range(0, each), mine);
        }
    }

    // --- one worker ---

    [Fact]
    public async Task Commands_run_one_at_a_time_on_the_queues_own_thread()
    {
        var commands = new RecordingCommands();
        using var queue = new CommandQueue(commands);

        var sends = new ConcurrentBag<Task>();
        Parallel.For(0, 8, _ =>
        {
            var requester = queue.Join();
            for (var i = 0; i < 100; i++)
                sends.Add(requester.Send(Named("x")));
        });
        await Task.WhenAll(sends).WaitAsync(Wait);

        Assert.Equal(1, commands.MaxConcurrent);
        Assert.Equal(["time-arr commands"], commands.ThreadNames.Distinct());
    }

    [Fact]
    public async Task A_slow_awaiter_does_not_hold_up_the_next_command()
    {
        var commands = new RecordingCommands();
        using var queue = new CommandQueue(commands);
        var requester = queue.Join();
        using var stuck = new ManualResetEventSlim();

        // ExecuteSynchronously would run this on the worker if the queue let it, blocking every later command.
        var blocking = requester.Send(Named("first")).ContinueWith(_ => stuck.Wait(Wait), TaskContinuationOptions.ExecuteSynchronously);
        var second = await requester.Send(Named("second")).WaitAsync(TimeSpan.FromSeconds(3));
        stuck.Set();
        await blocking.WaitAsync(Wait);

        Assert.True(second.IsSuccess);
    }

    [Fact]
    public async Task Storage_problem_is_read_without_waiting_for_a_busy_worker()
    {
        var commands = new RecordingCommands { HoldFirst = true, Problem = new Error(ErrorCode.StorageBusy, "locked") };
        using var queue = new CommandQueue(commands);
        var requester = queue.Join();

        var running = requester.Send(Named("hold"));
        Assert.True(commands.Started.Wait(Wait));
        var problem = await Task.Run(() => requester.StorageProblem).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ErrorCode.StorageBusy, problem!.Code);
        Assert.Equal(ErrorCode.StorageBusy, queue.StorageProblem!.Code);
        commands.Release.Set();
        await running.WaitAsync(Wait);
    }

    // --- stopping ---

    [Fact]
    public async Task Dispose_twice_is_harmless_and_every_later_send_says_closing()
    {
        var queue = new CommandQueue(new RecordingCommands());
        var requester = queue.Join();

        queue.Dispose();
        queue.Dispose();

        Assert.Equal(ErrorCode.Ipc, (await requester.Send(Named("a"))).Error.Code);
        Assert.Equal(ErrorCode.Ipc, requester.Execute(Named("b")).Error.Code);
        Assert.Equal(ErrorCode.Ipc, (await queue.Join().Send(Named("c"))).Error.Code);
        Assert.Contains("closing", requester.Execute(Named("d")).Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopping_while_many_threads_send_leaves_no_command_waiting_forever()
    {
        for (var round = 0; round < 20; round++)
        {
            var commands = new RecordingCommands();
            var queue = new CommandQueue(commands);
            var sends = new ConcurrentBag<Task<Result<CommandReply>>>();
            using var go = new ManualResetEventSlim();

            var senders = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                var requester = queue.Join();
                go.Wait();
                for (var i = 0; i < 200; i++)
                    sends.Add(requester.Send(Named("x")));
            })).ToArray();
            go.Set();
            Thread.Sleep(round % 5);
            queue.Dispose();
            await Task.WhenAll(senders).WaitAsync(Wait);

            var replies = await Task.WhenAll(sends).WaitAsync(Wait);
            Assert.All(replies, r => Assert.True(r.IsSuccess || r.Error.Code == ErrorCode.Ipc, r.ToString()));
            Assert.Equal(replies.Count(r => r.IsSuccess), commands.Ran.Count);
        }
    }

    [Fact]
    public async Task A_requester_that_left_does_not_affect_the_others()
    {
        var commands = new RecordingCommands();
        using var queue = new CommandQueue(commands);
        var leaving = queue.Join();
        var staying = queue.Join();

        leaving.Dispose();
        leaving.Dispose();

        Assert.True((await staying.Send(Named("ok")).WaitAsync(Wait)).IsSuccess);
        Assert.Throws<ObjectDisposedException>(() => leaving.Execute(Named("no")));
    }

    [Fact]
    public void A_null_command_is_a_bug()
    {
        using var queue = new CommandQueue(new RecordingCommands());
        var requester = queue.Join();

        Assert.Throws<ArgumentNullException>(() => { _ = requester.Send(null!); });
        Assert.Throws<ArgumentNullException>(() => new CommandQueue(null!));
    }

    // --- against the real handler ---

    [Fact]
    public async Task A_window_and_many_cli_connections_share_one_handler_through_the_queue()
    {
        using var queue = new CommandQueue(Handler());
        var window = queue.Join();

        var cli = Enumerable.Range(0, 30).Select(i => Task.Run(() =>
        {
            using var connection = queue.Join();
            return connection.Execute(new TimerCommand.Add($"cli{i}"));
        }));
        var refreshes = Enumerable.Range(0, 30).Select(_ => window.Send(new TimerCommand.List()));
        var results = await Task.WhenAll(cli.Concat(refreshes)).WaitAsync(Wait);

        Assert.All(results, r => Assert.True(r.IsSuccess, r.ToString()));
        Assert.Equal(31, (await window.Send(new TimerCommand.List())).Value.Timers.Count);
    }

    [Fact]
    public async Task A_viewmodel_on_the_queue_gets_its_replies_off_the_worker_thread()
    {
        using var queue = new CommandQueue(Handler());
        var vm = new MainViewModel(queue.Join().Send, new FakeClock(T0));

        await vm.Refresh().WaitAsync(Wait);
        vm.NewTimerName = "via queue";
        vm.AddCommand.Execute(null);
        var deadline = DateTime.UtcNow + Wait;
        while (vm.Timers.Count < 2 && DateTime.UtcNow < deadline)
            await vm.Refresh();

        Assert.Equal(["default", "via queue"], vm.Timers.Select(t => t.Name));
    }

    [Fact]
    public async Task Storage_trouble_comes_back_through_the_queue_as_a_result()
    {
        var path = Path.Combine(_root, "time-arr", "time-arr.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "this is not a database, just some text that is long enough to have a header....");
        using var queue = new CommandQueue(new TimerCommandHandler(new SqliteTimerStorage(path), new FakeClock(T0)));
        using var requester = queue.Join();

        var reply = await requester.Send(new TimerCommand.List()).WaitAsync(Wait);

        Assert.Equal(ErrorCode.StorageForeign, reply.Error.Code);
        Assert.Equal(ErrorCode.StorageForeign, requester.StorageProblem!.Code);
    }

    /// <summary>Records what ran, on which thread and how many at once, optionally holding the worker on the first command.</summary>
    private sealed class RecordingCommands : ITimerCommands
    {
        private readonly List<string> _ran = [];
        private readonly ConcurrentBag<string?> _threads = [];
        private int _inside;
        private int _maxConcurrent;
        private int _calls;

        public bool HoldFirst { get; init; }
        public Error? Problem { get; init; }
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public IReadOnlyList<string> Ran
        {
            get
            {
                lock (_ran)
                    return [.. _ran];
            }
        }

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
        public IEnumerable<string?> ThreadNames => _threads;
        public Error? StorageProblem => Problem;

        public Result<CommandReply> Execute(TimerCommand command)
        {
            var inside = Interlocked.Increment(ref _inside);
            int seen;
            while (inside > (seen = Volatile.Read(ref _maxConcurrent)) && Interlocked.CompareExchange(ref _maxConcurrent, inside, seen) != seen)
            {
            }
            _threads.Add(Thread.CurrentThread.Name);
            Started.Set();
            if (HoldFirst && Interlocked.Increment(ref _calls) == 1)
                Release.Wait();
            lock (_ran)
                _ran.Add(((TimerRef.ByName)((TimerCommand.Get)command).Target).Name);
            Interlocked.Decrement(ref _inside);
            return Result<CommandReply>.Ok(new CommandReply([], DateTimeOffset.UnixEpoch));
        }
    }
}
