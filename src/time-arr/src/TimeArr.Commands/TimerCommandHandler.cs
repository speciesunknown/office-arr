using TimeArr.Core;

namespace TimeArr.Commands;

/// <summary>Opens storage for <see cref="TimerCommand"/>s and runs them, as the one path the window and the CLI share.</summary>
/// <remarks>Call it from one thread at a time, which in the running instance is the <see cref="CommandQueue"/>'s worker.</remarks>
// A damaged database is reported and never replaced or repaired, so the next command picks up whatever the user leaves there.
public sealed class TimerCommandHandler : ITimerCommands
{
    private readonly ITimerStorage _storage;
    private readonly TimeProvider _clock;

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

        var result = command.Execute(new CommandContext(repo, _clock));

        if (result.IsFailure && IsStorageFailure(result.Error.Code))
            Forget(result.Error);
        else if (result.IsSuccess)
            _storageProblem = null;
        return result;
    }

    private static bool IsStorageFailure(ErrorCode code) =>
        code is ErrorCode.StorageCorrupt or ErrorCode.StorageForeign or ErrorCode.StorageTooNew or ErrorCode.StorageUnavailable;

    private Result<ITimerRepository> Repository()
    {
        if (_repository is not null)
            return Result<ITimerRepository>.Ok(_repository);

        // Not cached on failure, because the user may fix the problem and the next command should just work.
        var opened = _storage.Open();
        _repository = opened.IsSuccess ? opened.Value : null;
        _storageProblem = opened.IsSuccess ? null : opened.Error;
        return opened;
    }

    /// <summary>Drops the open database after a storage failure, so the next command opens it afresh as a restart would.</summary>
    private void Forget(Error error)
    {
        _repository = null;
        _storageProblem = error;
    }
}
