using System.Collections.Concurrent;
using TimeArr.Core;

namespace TimeArr.Commands;

/// <summary>Runs every command of the running instance on one thread, taking one command from each requester in turn so each gets a fair share.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "It is a queue of commands, which is what the user asked for.")]
public sealed class CommandQueue : IDisposable
{
    private enum QueueState { Running = 1, Stopped = 2 }

    private readonly ITimerCommands _commands;
    // Requesters with work waiting, in turn order, each one in it at most once.
    private readonly ConcurrentQueue<Requester> _turns = new();
    // Counts commands waiting across all requesters, so the worker sleeps while there are none.
    private readonly SemaphoreSlim _waiting = new(0);
    private QueueState _state = QueueState.Running;

    public CommandQueue(ITimerCommands commands)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        new Thread(Drain) { IsBackground = true, Name = "time-arr commands" }.Start();
    }

    /// <summary>Why storage can't be used right now, or null, without waiting for the queue.</summary>
    public Error? StorageProblem => _commands.StorageProblem;

    /// <summary>A new requester with its own turn, such as the window or one CLI connection.</summary>
    public Requester Join() => new(this);

    // A compare-exchange that changes nothing is the fenced read Volatile.Read can't do for an enum.
    internal bool IsStopped => Interlocked.CompareExchange(ref _state, QueueState.Stopped, QueueState.Stopped) == QueueState.Stopped;

    internal void Enqueue(Requester requester, Work work)
    {
        requester.Pending.Enqueue(work);
        if (requester.TryTakeTurn())
            _turns.Enqueue(requester);
        _waiting.Release();
        // Checked after queueing, so either this sees the stop or the stop's sweep sees this work.
        if (IsStopped)
            FailAll(requester);
    }

    /// <summary>Stops taking commands, failing those still waiting, while a command already running finishes on its own.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _state, QueueState.Stopped) == QueueState.Stopped)
            return;
        _waiting.Release();
        while (_turns.TryDequeue(out var requester))
            FailAll(requester);
    }

    private void Drain()
    {
        while (true)
        {
            _waiting.Wait();
            if (IsStopped)
                return;
            if (NextTurn() is not { } requester)
                return;
            if (!requester.Pending.TryDequeue(out var work))
            {
                // Only a stop's sweep empties a requester that is in line.
                if (IsStopped)
                    return;
                throw new InvalidOperationException("A requester in line had no work.");
            }

            // A bug in a command is left unhandled here, so it reaches the crash log and ends the instance (CODING.md #4).
            work.Done.TrySetResult(_commands.Execute(work.Command));

            if (IsStopped)
                FailAll(requester);
            else if (!requester.Pending.IsEmpty || requester.GiveUpTurn())
                _turns.Enqueue(requester);
        }
    }

    /// <summary>The requester whose turn it is, or null once the queue stops.</summary>
    private Requester? NextTurn()
    {
        // Another sender on the same requester can raise the count just before the turn holder puts it in line, so wait for it briefly.
        var spin = new SpinWait();
        Requester? requester;
        while (!_turns.TryDequeue(out requester))
        {
            if (IsStopped)
                return null;
            spin.SpinOnce();
        }
        return requester;
    }

    private static void FailAll(Requester requester)
    {
        while (requester.Pending.TryDequeue(out var work))
            work.Done.TrySetResult(Closing());
    }

    internal static Result<CommandReply> Closing() =>
        Result<CommandReply>.Fail(ErrorCode.Ipc, "time-arr is closing, so the command didn't run.");

    internal sealed record Work(TimerCommand Command, TaskCompletionSource<Result<CommandReply>> Done);
}

/// <summary>One source of commands with its own turn in a <see cref="CommandQueue"/>, such as the window or one CLI connection.</summary>
public sealed class Requester : ITimerCommands, IDisposable
{
    private enum Turn { Idle = 1, InLine = 2 }

    private enum Membership { Active = 1, Left = 2 }

    private readonly CommandQueue _queue;
    private Turn _turn = Turn.Idle;
    private volatile Membership _membership = Membership.Active;

    internal Requester(CommandQueue queue) => _queue = queue;

    internal ConcurrentQueue<CommandQueue.Work> Pending { get; } = new();

    public Error? StorageProblem => _queue.StorageProblem;

    /// <summary>Queues a command and completes when the queue's worker has run it.</summary>
    public Task<Result<CommandReply>> Send(TimerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_membership == Membership.Left, this);
        if (_queue.IsStopped)
            return Task.FromResult(CommandQueue.Closing());
        // Continuations run elsewhere, so a slow awaiter never holds up the worker.
        var done = new TaskCompletionSource<Result<CommandReply>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(this, new CommandQueue.Work(command, done));
        return done.Task;
    }

    /// <summary>Queues a command and waits for it, for callers on their own thread such as a CLI connection.</summary>
    public Result<CommandReply> Execute(TimerCommand command) => Send(command).GetAwaiter().GetResult();

    /// <summary>Whether this call put the requester in line, which only one caller can do until it gives its turn up.</summary>
    internal bool TryTakeTurn() => Interlocked.CompareExchange(ref _turn, Turn.InLine, Turn.Idle) == Turn.Idle;

    /// <summary>Leaves the line, and returns whether work that arrived meanwhile put it straight back in.</summary>
    internal bool GiveUpTurn()
    {
        // A full fence, so the emptiness check below can't be read before the turn is given up.
        Interlocked.Exchange(ref _turn, Turn.Idle);
        return !Pending.IsEmpty && TryTakeTurn();
    }

    /// <summary>Stops this requester sending, while anything it already sent still runs.</summary>
    public void Dispose() => _membership = Membership.Left;
}
