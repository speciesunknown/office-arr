using TimeArr.Core;

namespace TimeArr.Commands;

/// <summary>
/// Runs <see cref="TimerCommand"/>s against storage. The only thing above the data layer that
/// touches it: the viewmodels and the CLI/IPC both come through here, so a button click and a
/// CLI <c>reset</c> run the same code. Thread-safe; one instance per running app.
/// </summary>
public sealed class TimerCommandHandler
{
    private readonly ITimerStorage _storage;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    private ITimerRepository? _repository;
    private Error? _openError;

    public TimerCommandHandler(ITimerStorage storage, TimeProvider clock)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Why the database couldn't be opened last time, or null. When its code is
    /// <see cref="ErrorCode.StorageCorrupt"/>, the UI should offer <see cref="TimerCommand.RecoverStorage"/>.
    /// </summary>
    public Error? StorageProblem
    {
        get { lock (_gate) return _openError; }
    }

    public Result<CommandReply> Execute(TimerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command is TimerCommand.RecoverStorage)
            return Recover();

        var repository = Repository();
        if (repository.IsFailure)
            return Result<CommandReply>.Fail(repository.Error);
        var repo = repository.Value;
        var now = _clock.GetUtcNow();

        return command switch
        {
            TimerCommand.List => Reply(repo.List(), now),
            TimerCommand.Get get => Reply(Resolve(repo, get.Target), now),
            TimerCommand.Add add => Reply(repo.Create(add.Name), now),
            TimerCommand.Rename rename => Reply(Then(Resolve(repo, rename.Target), t => repo.Rename(t.Id, rename.NewName)), now),
            TimerCommand.Delete delete => Reply(Then(Resolve(repo, delete.Target), t =>
            {
                var deleted = repo.Delete(t.Id);
                return deleted.IsSuccess ? t : Result<TimerEntry>.Fail(deleted.Error);
            }), now),
            TimerCommand.Start start => Change(repo, start.Target, s => s.Start(now), now),
            TimerCommand.Pause pause => Change(repo, pause.Target, s => s.Pause(now), now),
            TimerCommand.Reset reset => Change(repo, reset.Target, s => s.Reset(now), now),
            _ => throw new InvalidOperationException($"Unhandled command {command.GetType().Name}."),
        };
    }

    private Result<ITimerRepository> Repository()
    {
        lock (_gate)
        {
            if (_repository is not null)
                return Result<ITimerRepository>.Ok(_repository);

            // Not cached on failure: the user may fix the problem (free disk space, permissions)
            // and the next command should just work.
            var opened = _storage.Open();
            if (opened.IsSuccess)
            {
                _repository = opened.Value;
                _openError = null;
            }
            else
            {
                _openError = opened.Error;
            }
            return opened;
        }
    }

    private Result<CommandReply> Recover()
    {
        lock (_gate)
        {
            if (_repository is not null)
                return Result<CommandReply>.Fail(ErrorCode.Conflict, "The database is fine; there is nothing to recover.");

            // Look again rather than trusting an old error: only a database that is damaged
            // right now may be moved aside. A newer time-arr's database never is.
            var opened = _storage.Open();
            if (opened.IsSuccess)
            {
                _repository = opened.Value;
                _openError = null;
                return Result<CommandReply>.Fail(ErrorCode.Conflict, "The database is fine; there is nothing to recover.");
            }
            if (opened.Error.Code != ErrorCode.StorageCorrupt)
            {
                _openError = opened.Error;
                return Result<CommandReply>.Fail(ErrorCode.Conflict,
                    $"Only a damaged database can be replaced; this one has a different problem: {opened.Error.Message}");
            }

            var now = _clock.GetUtcNow();
            var moved = _storage.MoveAside(now);
            if (moved.IsFailure)
                return Result<CommandReply>.Fail(moved.Error);

            var fresh = _storage.Open();
            if (fresh.IsFailure)
            {
                _openError = fresh.Error;
                return Result<CommandReply>.Fail(fresh.Error);
            }
            _repository = fresh.Value;
            _openError = null;

            var timers = fresh.Value.List();
            return timers.IsFailure
                ? Result<CommandReply>.Fail(timers.Error)
                : new CommandReply(timers.Value, now, $"The damaged database was moved to '{moved.Value}'. Started a fresh one.");
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

    private static Result<CommandReply> Change(ITimerRepository repo, TimerRef target, Func<TimerState, TimerState> change, DateTimeOffset now) =>
        Reply(Then(Resolve(repo, target), t => repo.Update(t.Id, s => change(s))), now);

    private static Result<TimerEntry> Then(Result<TimerEntry> first, Func<TimerEntry, Result<TimerEntry>> next) =>
        first.IsSuccess ? next(first.Value) : first;

    private static Result<CommandReply> Reply(Result<TimerEntry> timer, DateTimeOffset now) =>
        timer.IsSuccess ? new CommandReply([timer.Value], now) : Result<CommandReply>.Fail(timer.Error);

    private static Result<CommandReply> Reply(Result<IReadOnlyList<TimerEntry>> timers, DateTimeOffset now) =>
        timers.IsSuccess ? new CommandReply(timers.Value, now) : Result<CommandReply>.Fail(timers.Error);
}
