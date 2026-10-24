namespace TimeArr.Commands;

/// <summary>
/// Everything the UI and the CLI can ask for. A closed set: the handler, the IPC protocol and
/// the CLI parser all switch over these.
/// </summary>
public abstract record TimerCommand
{
    private TimerCommand() { }

    /// <summary>All timers.</summary>
    public sealed record List : TimerCommand;

    /// <summary>One timer (CLI <c>status</c>).</summary>
    public sealed record Get(TimerRef Target) : TimerCommand;

    public sealed record Add(string Name) : TimerCommand;

    public sealed record Rename(TimerRef Target, string NewName) : TimerCommand;

    public sealed record Delete(TimerRef Target) : TimerCommand;

    public sealed record Start(TimerRef Target) : TimerCommand;

    public sealed record Pause(TimerRef Target) : TimerCommand;

    public sealed record Reset(TimerRef Target) : TimerCommand;
}

/// <summary>What a command produced.</summary>
/// <param name="Timers">
/// The timers the command is about: all of them for <see cref="TimerCommand.List"/>, otherwise the
/// one affected (for <see cref="TimerCommand.Delete"/>, as it was before deletion).
/// </param>
/// <param name="NowUtc">The time the command took effect; show elapsed time as of this instant.</param>
public sealed record CommandReply(IReadOnlyList<TimeArr.Core.TimerEntry> Timers, DateTimeOffset NowUtc);
