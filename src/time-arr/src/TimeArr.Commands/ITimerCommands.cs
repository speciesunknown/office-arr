using TimeArr.Core;

namespace TimeArr.Commands;

/// <summary>Where commands go, with <see cref="Requester"/> safe from any thread and <see cref="TimerCommandHandler"/> for one thread at a time.</summary>
public interface ITimerCommands
{
    Result<CommandReply> Execute(TimerCommand command);

    /// <summary>Why storage can't be used right now, or null, read without ever blocking.</summary>
    Error? StorageProblem { get; }
}
