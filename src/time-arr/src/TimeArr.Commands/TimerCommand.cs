using TimeArr.Core;

namespace TimeArr.Commands;

/// <summary>Everything the UI and the CLI can ask for, each command carrying the work it does.</summary>
public abstract record TimerCommand
{
    private TimerCommand() { }

    /// <summary>Does the command's work against an open database, called only by <see cref="TimerCommandHandler"/>.</summary>
    internal abstract Result<CommandReply> Execute(CommandContext run);

    /// <summary>All timers.</summary>
    public sealed record List : TimerCommand
    {
        internal override Result<CommandReply> Execute(CommandContext run) => run.Reply(run.Timers.List());
    }

    /// <summary>One timer (CLI <c>status</c>).</summary>
    public sealed record Get(TimerRef Target) : TimerCommand
    {
        internal override Result<CommandReply> Execute(CommandContext run) => run.Reply(run.Resolve(Target));
    }

    public sealed record Add(string Name) : TimerCommand
    {
        internal override Result<CommandReply> Execute(CommandContext run) => run.Reply(run.Timers.Create(Name));
    }

    public sealed record Rename(TimerRef Target, string NewName) : TimerCommand
    {
        internal override Result<CommandReply> Execute(CommandContext run) =>
            run.Reply(CommandContext.Then(run.Resolve(Target), t => run.Timers.Rename(t.Id, NewName)));
    }

    public sealed record Delete(TimerRef Target) : TimerCommand
    {
        internal override Result<CommandReply> Execute(CommandContext run) =>
            run.Reply(CommandContext.Then(run.Resolve(Target), t =>
            {
                var deleted = run.Timers.Delete(t.Id);
                return deleted.IsSuccess ? t : Result<TimerEntry>.Fail(deleted.Error);
            }));
    }

    public sealed record Start(TimerRef Target) : TimerCommand
    {
        internal override Result<CommandReply> Execute(CommandContext run) => run.Change(Target, (s, now) => s.Start(now));
    }

    public sealed record Pause(TimerRef Target) : TimerCommand
    {
        internal override Result<CommandReply> Execute(CommandContext run) => run.Change(Target, (s, now) => s.Pause(now));
    }

    public sealed record Reset(TimerRef Target) : TimerCommand
    {
        internal override Result<CommandReply> Execute(CommandContext run) => run.Change(Target, (s, now) => s.Reset(now));
    }
}

/// <summary>What a command produced.</summary>
/// <param name="Timers">The timers the command is about: all of them for <see cref="TimerCommand.List"/>, otherwise the one affected (for <see cref="TimerCommand.Delete"/>, as it was before deletion).</param>
/// <param name="NowUtc">The time the command took effect, which is the instant to show elapsed time at.</param>
public sealed record CommandReply(IReadOnlyList<TimerEntry> Timers, DateTimeOffset NowUtc);
