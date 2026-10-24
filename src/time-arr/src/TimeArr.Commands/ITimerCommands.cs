using TimeArr.Core;

namespace TimeArr.Commands;

/// <summary>Where viewmodels (and the IPC server) send commands. Implemented by <see cref="TimerCommandHandler"/>.</summary>
public interface ITimerCommands
{
    Result<CommandReply> Execute(TimerCommand command);

    /// <summary>Why storage can't be used right now, or null. Never blocks.</summary>
    Error? StorageProblem { get; }
}
