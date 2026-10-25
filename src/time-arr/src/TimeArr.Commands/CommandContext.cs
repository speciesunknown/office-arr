using TimeArr.Core;

namespace TimeArr.Commands;

/// <summary>The open database and clock a <see cref="TimerCommand"/> runs against, with the steps commands share.</summary>
internal sealed class CommandContext(ITimerRepository timers, TimeProvider clock)
{
    public ITimerRepository Timers { get; } = timers;

    /// <summary>Finds the timer a command is about.</summary>
    public Result<TimerEntry> Resolve(TimerRef target)
    {
        switch (target)
        {
            case TimerRef.ById { Id: <= 0 } byId:
                return Result<TimerEntry>.Fail(ErrorCode.InvalidInput, $"Timer id must be positive, not {byId.Id}.");
            case TimerRef.ById byId:
                return Timers.Get(byId.Id);
            case TimerRef.ByName byName:
                return Timers.FindByName(byName.Name);
            case TimerRef.Default:
                var found = Timers.FindByName(TimerRef.DefaultName);
                return found.IsFailure && found.Error.Code == ErrorCode.NotFound
                    ? Result<TimerEntry>.Fail(ErrorCode.NotFound,
                        $"There is no timer named '{TimerRef.DefaultName}'; say which timer you mean.")
                    : found;
            case null:
                return Result<TimerEntry>.Fail(ErrorCode.InvalidInput, "No timer given.");
            default:
                throw new InvalidOperationException($"Unhandled timer reference {target.GetType().Name}.");
        }
    }

    /// <summary>Applies a state change, reading the clock inside the write lock so a command that waited takes effect when it runs, not when it was issued.</summary>
    public Result<CommandReply> Change(TimerRef target, Func<TimerState, DateTimeOffset, TimerState> change)
    {
        var resolved = Resolve(target);
        if (resolved.IsFailure)
            return Result<CommandReply>.Fail(resolved.Error);

        var at = default(DateTimeOffset);
        var updated = Timers.Update(resolved.Value.Id, state =>
        {
            at = clock.GetUtcNow();
            return change(state, at);
        });
        return updated.IsSuccess ? new CommandReply([updated.Value], at) : Result<CommandReply>.Fail(updated.Error);
    }

    public static Result<TimerEntry> Then(Result<TimerEntry> first, Func<TimerEntry, Result<TimerEntry>> next) =>
        first.IsSuccess ? next(first.Value) : first;

    public Result<CommandReply> Reply(Result<TimerEntry> timer) =>
        timer.IsSuccess ? new CommandReply([timer.Value], clock.GetUtcNow()) : Result<CommandReply>.Fail(timer.Error);

    public Result<CommandReply> Reply(Result<IReadOnlyList<TimerEntry>> timers) =>
        timers.IsSuccess ? new CommandReply(timers.Value, clock.GetUtcNow()) : Result<CommandReply>.Fail(timers.Error);
}
