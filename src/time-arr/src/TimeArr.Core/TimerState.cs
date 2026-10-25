namespace TimeArr.Core;

/// <summary>Which of its three states a timer is in.</summary>
public enum TimerPhase
{
    /// <summary>Not running, with nothing on the clock.</summary>
    Stopped = 1,
    /// <summary>Counting time.</summary>
    Running = 2,
    /// <summary>Not running, with time on the clock that a resume carries on from.</summary>
    Paused = 3,
}

/// <summary>
/// The timer as stored, whether it runs, since when and how much was banked before, from which
/// elapsed time is computed against "now" so it survives restarts and counts while the app is closed.
/// </summary>
/// <remarks>
/// It is immutable, <see cref="StartedAtUtc"/> is set (in UTC) exactly when running, and
/// <see cref="Accumulated"/> is non-negative whole milliseconds as storage keeps it.
/// </remarks>
public sealed record TimerState
{
    public static readonly TimerState Zero = new(false, null, TimeSpan.Zero);

    private TimerState(bool isRunning, DateTimeOffset? startedAtUtc, TimeSpan accumulated)
    {
        Phase = isRunning ? TimerPhase.Running : accumulated > TimeSpan.Zero ? TimerPhase.Paused : TimerPhase.Stopped;
        StartedAtUtc = startedAtUtc;
        Accumulated = accumulated;
    }

    /// <summary>Whether the timer is stopped, running or paused.</summary>
    public TimerPhase Phase { get; }

    /// <summary>Shorthand for <see cref="Phase"/> being <see cref="TimerPhase.Running"/>, which is what storage keeps.</summary>
    public bool IsRunning => Phase == TimerPhase.Running;

    public DateTimeOffset? StartedAtUtc { get; }
    public TimeSpan Accumulated { get; }

    /// <summary>Builds a state from raw parts, such as a row read from storage, checking the invariants.</summary>
    /// <param name="isRunning">The stored running flag.</param>
    public static Result<TimerState> Create(bool isRunning, DateTimeOffset? startedAtUtc, TimeSpan accumulated)
    {
        if (accumulated < TimeSpan.Zero)
            return Result<TimerState>.Fail(ErrorCode.InvalidInput, "Accumulated time is negative.");
        if (accumulated.Ticks % TimeSpan.TicksPerMillisecond != 0)
            return Result<TimerState>.Fail(ErrorCode.InvalidInput, "Accumulated time is not whole milliseconds.");
        if (isRunning && startedAtUtc is null)
            return Result<TimerState>.Fail(ErrorCode.InvalidInput, "Running timer has no start time.");
        if (!isRunning && startedAtUtc is not null)
            return Result<TimerState>.Fail(ErrorCode.InvalidInput, "Paused timer has a start time.");
        if (startedAtUtc is { Offset: var offset } && offset != TimeSpan.Zero)
            return Result<TimerState>.Fail(ErrorCode.InvalidInput, "Start time is not UTC.");

        return new TimerState(isRunning, startedAtUtc, accumulated);
    }

    /// <summary>
    /// Time on the clock at <paramref name="nowUtc"/>, counting the running stretch as zero rather
    /// than negative if the system clock has been set back past the start time.
    /// </summary>
    public TimeSpan ElapsedAt(DateTimeOffset nowUtc)
    {
        if (StartedAtUtc is not { } started)
            return Accumulated;

        var running = nowUtc - started;
        if (running <= TimeSpan.Zero)
            return Accumulated;

        return running >= TimeSpan.MaxValue - Accumulated ? TimeSpan.MaxValue : Accumulated + running;
    }

    /// <summary>Starts counting from <paramref name="nowUtc"/>, leaving a running timer unchanged.</summary>
    public TimerState Start(DateTimeOffset nowUtc) =>
        IsRunning ? this : new TimerState(true, nowUtc.ToUniversalTime(), Accumulated);

    /// <summary>Banks the elapsed time and stops counting, leaving a timer that isn't running unchanged.</summary>
    public TimerState Pause(DateTimeOffset nowUtc) =>
        IsRunning ? new TimerState(false, null, TruncateToMilliseconds(ElapsedAt(nowUtc))) : this;

    /// <summary>Goes back to zero, with a running timer carrying on from <paramref name="nowUtc"/>.</summary>
    public TimerState Reset(DateTimeOffset nowUtc) =>
        IsRunning ? new TimerState(true, nowUtc.ToUniversalTime(), TimeSpan.Zero) : Zero;

    private static TimeSpan TruncateToMilliseconds(TimeSpan value) =>
        TimeSpan.FromTicks(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond);
}
