namespace TimeArr.Core;

/// <summary>
/// The timer as stored: whether it runs, since when, and how much time was banked before that.
/// Elapsed time is always computed from these against "now", never counted, so it is right
/// after a restart and keeps counting while the app is closed (unless paused).
/// </summary>
/// <remarks>
/// Immutable. Invariants: <see cref="StartedAtUtc"/> is set exactly when running and is UTC;
/// <see cref="Accumulated"/> is non-negative and whole milliseconds (what storage keeps).
/// </remarks>
public sealed record TimerState
{
    public static readonly TimerState Zero = new(false, null, TimeSpan.Zero);

    private TimerState(bool isRunning, DateTimeOffset? startedAtUtc, TimeSpan accumulated)
    {
        IsRunning = isRunning;
        StartedAtUtc = startedAtUtc;
        Accumulated = accumulated;
    }

    public bool IsRunning { get; }
    public DateTimeOffset? StartedAtUtc { get; }
    public TimeSpan Accumulated { get; }

    /// <summary>Builds a state from raw parts (e.g. read from storage), checking the invariants.</summary>
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
    /// Time on the clock at <paramref name="nowUtc"/>. If the system clock is now earlier than the
    /// start time (clock set back), the running stretch counts as zero rather than going negative.
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

    /// <summary>Starts counting from <paramref name="nowUtc"/>. Already running: unchanged.</summary>
    public TimerState Start(DateTimeOffset nowUtc) =>
        IsRunning ? this : new TimerState(true, nowUtc.ToUniversalTime(), Accumulated);

    /// <summary>Banks the elapsed time and stops counting. Already paused: unchanged.</summary>
    public TimerState Pause(DateTimeOffset nowUtc) =>
        IsRunning ? new TimerState(false, null, TruncateToMilliseconds(ElapsedAt(nowUtc))) : this;

    /// <summary>Back to zero. A running timer keeps running, counting from <paramref name="nowUtc"/>.</summary>
    public TimerState Reset(DateTimeOffset nowUtc) =>
        IsRunning ? new TimerState(true, nowUtc.ToUniversalTime(), TimeSpan.Zero) : Zero;

    private static TimeSpan TruncateToMilliseconds(TimeSpan value) =>
        TimeSpan.FromTicks(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond);
}
