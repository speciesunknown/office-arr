using TimeArr.Core;

namespace TimeArr.Commands;

/// <summary>
/// Runs <see cref="TimerCommand"/>s against storage, thread-safely, as the one path the window and
/// the CLI share, so a button click and a CLI <c>reset</c> run the same code.
/// </summary>
/// <remarks>A damaged database is reported and never replaced or repaired, so the next command picks up whatever the user leaves there.</remarks>
public sealed class TimerCommandHandler : ITimerCommands
{
    private readonly ITimerStorage _storage;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    private ITimerRepository? _repository;
    private volatile Error? _storageProblem;

    public TimerCommandHandler(ITimerStorage storage, TimeProvider clock)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Why the last command couldn't use the database, or null once it works again, read without blocking so the UI thread can poll it.</summary>
    public Error? StorageProblem => _storageProblem;

    public Result<CommandReply> Execute(TimerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var repository = Repository();
        if (repository.IsFailure)
            return Result<CommandReply>.Fail(repository.Error);
        var repo = repository.Value;

        var result = command switch
        {
            TimerCommand.List => Reply(repo.List()),
            TimerCommand.Get get => Reply(Resolve(repo, get.Target)),
            TimerCommand.Add add => Reply(repo.Create(add.Name)),
            TimerCommand.Rename rename => Reply(Then(Resolve(repo, rename.Target), t => repo.Rename(t.Id, rename.NewName))),
            TimerCommand.Delete delete => Reply(Then(Resolve(repo, delete.Target), t =>
            {
                var deleted = repo.Delete(t.Id);
                return deleted.IsSuccess ? t : Result<TimerEntry>.Fail(deleted.Error);
            })),
            TimerCommand.Start start => Change(repo, start.Target, (s, now) => s.Start(now)),
            TimerCommand.Pause pause => Change(repo, pause.Target, (s, now) => s.Pause(now)),
            TimerCommand.Reset reset => Change(repo, reset.Target, (s, now) => s.Reset(now)),
            _ => throw new InvalidOperationException($"Unhandled command {command.GetType().Name}."),
        };

        if (result.IsFailure && IsStorageFailure(result.Error.Code))
            Forget(repo, result.Error);
        else if (result.IsSuccess)
            _storageProblem = null;
        return result;
    }

    private static bool IsStorageFailure(ErrorCode code) =>
        code is ErrorCode.StorageCorrupt or ErrorCode.StorageForeign or ErrorCode.StorageTooNew or ErrorCode.StorageUnavailable;

    private Result<ITimerRepository> Repository()
    {
        lock (_gate)
        {
            if (_repository is not null)
                return Result<ITimerRepository>.Ok(_repository);

            // Not cached on failure: the user may fix the problem (free disk space, permissions,
            // remove a damaged file) and the next command should just work.
            var opened = _storage.Open();
            _repository = opened.IsSuccess ? opened.Value : null;
            _storageProblem = opened.IsSuccess ? null : opened.Error;
            return opened;
        }
    }

    /// <summary>Drops the open database after a storage failure, so the next command opens it afresh as a restart would.</summary>
    private void Forget(ITimerRepository failed, Error error)
    {
        lock (_gate)
        {
            // Another thread may already have re-opened it, and then this failure is old news.
            if (!ReferenceEquals(_repository, failed))
                return;
            _repository = null;
            _storageProblem = error;
        }
    }

    private static Result<TimerEntry> Resolve(ITimerRepository repo, TimerRef target)
    {
        switch (target)
        {
            case TimerRef.ById { Id: <= 0 } byId:
                return Result<TimerEntry>.Fail(ErrorCode.InvalidInput, $"Timer id must be positive, not {byId.Id}.");
            case TimerRef.ById byId:
                return repo.Get(byId.Id);
            case TimerRef.ByName byName:
                return repo.FindByName(byName.Name);
            case TimerRef.Default:
                var found = repo.FindByName(TimerRef.DefaultName);
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

    /// <summary>
    /// Applies a state change, reading the clock inside the write lock: a command that had to wait
    /// for the lock takes effect when it actually runs, not when it was issued, so a delayed Start
    /// can't count a stretch of time twice.
    /// </summary>
    private Result<CommandReply> Change(ITimerRepository repo, TimerRef target, Func<TimerState, DateTimeOffset, TimerState> change)
    {
        var resolved = Resolve(repo, target);
        if (resolved.IsFailure)
            return Result<CommandReply>.Fail(resolved.Error);

        var at = default(DateTimeOffset);
        var updated = repo.Update(resolved.Value.Id, state =>
        {
            at = _clock.GetUtcNow();
            return change(state, at);
        });
        return updated.IsSuccess ? new CommandReply([updated.Value], at) : Result<CommandReply>.Fail(updated.Error);
    }

    private static Result<TimerEntry> Then(Result<TimerEntry> first, Func<TimerEntry, Result<TimerEntry>> next) =>
        first.IsSuccess ? next(first.Value) : first;

    private Result<CommandReply> Reply(Result<TimerEntry> timer) =>
        timer.IsSuccess ? new CommandReply([timer.Value], _clock.GetUtcNow()) : Result<CommandReply>.Fail(timer.Error);

    private Result<CommandReply> Reply(Result<IReadOnlyList<TimerEntry>> timers) =>
        timers.IsSuccess ? new CommandReply(timers.Value, _clock.GetUtcNow()) : Result<CommandReply>.Fail(timers.Error);
}
