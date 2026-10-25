using TimeArr.Commands;
using TimeArr.Core;

namespace TimeArr.Tests.Commands;

/// <summary>The instance's one command queue (sub plan 006), against a fake that records what ran and can hold the worker.</summary>
public sealed class CommandQueueTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static TimerCommand.Get Named(string name) => new TimerCommand.Get(new TimerRef.ByName(name));

    [Fact]
    public async Task Requesters_take_turns()
    {
        var commands = new HoldingCommands();
        using var queue = new CommandQueue(commands);
        var busy = queue.Join();
        var other = queue.Join();

        var first = busy.Send(Named("a1"));
        Assert.True(commands.Started.Wait(Wait));
        var rest = new[] { busy.Send(Named("a2")), busy.Send(Named("a3")), other.Send(Named("b1")) };
        commands.Release.Set();
        await Task.WhenAll([first, .. rest]).WaitAsync(Wait);

        // The other requester's one command goes before the busy one's backlog.
        Assert.Equal(["a1", "b1", "a2", "a3"], commands.Ran);
    }

    [Fact]
    public async Task Each_requesters_commands_run_in_the_order_sent()
    {
        var commands = new HoldingCommands();
        commands.Release.Set();
        using var queue = new CommandQueue(commands);
        var requester = queue.Join();

        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => requester.Send(Named($"n{i}")))).WaitAsync(Wait);

        Assert.Equal(Enumerable.Range(1, 20).Select(i => $"n{i}"), commands.Ran);
    }

    [Fact]
    public async Task Work_queued_before_a_requester_leaves_still_runs()
    {
        var commands = new HoldingCommands();
        using var queue = new CommandQueue(commands);
        var holder = queue.Join();
        var leaving = queue.Join();

        var first = holder.Send(Named("hold"));
        Assert.True(commands.Started.Wait(Wait));
        var queued = leaving.Send(Named("left"));
        leaving.Dispose();
        commands.Release.Set();

        Assert.True((await queued.WaitAsync(Wait)).IsSuccess);
        await first.WaitAsync(Wait);
        // Send throws before any task exists, because sending on a requester that left is a bug.
        Assert.Throws<ObjectDisposedException>(() => { _ = leaving.Send(Named("late")); });
    }

    [Fact]
    public async Task Stopping_fails_waiting_commands_instead_of_dropping_them()
    {
        var commands = new HoldingCommands();
        var queue = new CommandQueue(commands);
        var requester = queue.Join();

        var running = requester.Send(Named("running"));
        Assert.True(commands.Started.Wait(Wait));
        var waiting = requester.Send(Named("waiting"));
        // Dispose doesn't wait for the running command, so the stop has happened before it is let go.
        queue.Dispose();
        commands.Release.Set();

        Assert.True((await running.WaitAsync(Wait)).IsSuccess);
        Assert.Equal(ErrorCode.Ipc, (await waiting.WaitAsync(Wait)).Error.Code);
        Assert.Equal(ErrorCode.Ipc, (await requester.Send(Named("after"))).Error.Code);
        Assert.Equal(["running"], commands.Ran);
    }

    [Fact]
    public async Task A_bug_in_a_command_reaches_whoever_awaits_it_and_the_queue_carries_on()
    {
        var commands = new HoldingCommands { BugOn = "bug" };
        commands.Release.Set();
        using var queue = new CommandQueue(commands);
        var requester = queue.Join();

        await Assert.ThrowsAsync<InvalidOperationException>(() => requester.Send(Named("bug")).WaitAsync(Wait));
        Assert.True((await requester.Send(Named("next")).WaitAsync(Wait)).IsSuccess);
    }

    [Fact]
    public void Execute_waits_for_the_queue()
    {
        var commands = new HoldingCommands();
        commands.Release.Set();
        using var queue = new CommandQueue(commands);
        using var requester = queue.Join();

        Assert.True(requester.Execute(Named("sync")).IsSuccess);
        Assert.Equal(["sync"], commands.Ran);
    }

    /// <summary>Records each command's timer name, holding the worker on the first one until released.</summary>
    private sealed class HoldingCommands : ITimerCommands
    {
        private readonly List<string> _ran = [];

        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public string? BugOn { get; init; }

        public IReadOnlyList<string> Ran
        {
            get
            {
                lock (_ran)
                    return [.. _ran];
            }
        }

        public Error? StorageProblem => null;

        public Result<CommandReply> Execute(TimerCommand command)
        {
            var name = ((TimerRef.ByName)((TimerCommand.Get)command).Target).Name;
            if (name == BugOn)
                throw new InvalidOperationException("Test bug.");
            Started.Set();
            Release.Wait();
            lock (_ran)
                _ran.Add(name);
            return Result<CommandReply>.Ok(new CommandReply([], DateTimeOffset.UnixEpoch));
        }
    }
}
