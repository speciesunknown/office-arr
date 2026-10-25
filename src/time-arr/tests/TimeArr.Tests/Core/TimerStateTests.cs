using TimeArr.Core;

namespace TimeArr.Tests.Core;

public class TimerStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Zero_is_paused_with_nothing_elapsed()
    {
        Assert.False(TimerState.Zero.IsRunning);
        Assert.Null(TimerState.Zero.StartedAtUtc);
        Assert.Equal(TimeSpan.Zero, TimerState.Zero.ElapsedAt(T0));
    }

    [Fact]
    public void Running_timer_counts_from_its_start_time()
    {
        var state = TimerState.Zero.Start(T0);

        Assert.True(state.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(5), state.ElapsedAt(T0.AddMinutes(5)));
    }

    [Fact]
    public void Pause_banks_elapsed_time_and_stops_counting()
    {
        var paused = TimerState.Zero.Start(T0).Pause(T0.AddSeconds(90));

        Assert.False(paused.IsRunning);
        Assert.Equal(TimeSpan.FromSeconds(90), paused.Accumulated);
        Assert.Equal(TimeSpan.FromSeconds(90), paused.ElapsedAt(T0.AddHours(3)));
    }

    [Fact]
    public void Resume_adds_to_banked_time()
    {
        var state = TimerState.Zero.Start(T0).Pause(T0.AddSeconds(10)).Start(T0.AddSeconds(100));

        Assert.Equal(TimeSpan.FromSeconds(15), state.ElapsedAt(T0.AddSeconds(105)));
    }

    [Fact]
    public void Start_while_running_and_pause_while_paused_change_nothing()
    {
        var running = TimerState.Zero.Start(T0);
        Assert.Same(running, running.Start(T0.AddMinutes(1)));

        var paused = running.Pause(T0.AddMinutes(1));
        Assert.Same(paused, paused.Pause(T0.AddMinutes(2)));
    }

    [Fact]
    public void Reset_while_paused_goes_to_zero()
    {
        var state = TimerState.Zero.Start(T0).Pause(T0.AddMinutes(1)).Reset(T0.AddMinutes(2));

        Assert.Equal(TimerState.Zero, state);
    }

    [Fact]
    public void Reset_while_running_keeps_running_from_now()
    {
        var state = TimerState.Zero.Start(T0).Reset(T0.AddMinutes(10));

        Assert.True(state.IsRunning);
        Assert.Equal(TimeSpan.Zero, state.Accumulated);
        Assert.Equal(TimeSpan.FromMinutes(1), state.ElapsedAt(T0.AddMinutes(11)));
    }

    [Fact]
    public void Clock_set_back_before_start_never_goes_negative()
    {
        var state = TimerState.Zero.Start(T0).Pause(T0.AddMinutes(1)).Start(T0.AddMinutes(2));

        Assert.Equal(TimeSpan.FromMinutes(1), state.ElapsedAt(T0.AddHours(-5)));
        Assert.Equal(TimeSpan.FromMinutes(1), state.Pause(T0.AddHours(-5)).Accumulated);
    }

    [Fact]
    public void Pause_keeps_whole_milliseconds_only()
    {
        var paused = TimerState.Zero.Start(T0).Pause(T0.AddTicks(12_345_678));

        Assert.Equal(TimeSpan.FromMilliseconds(1234), paused.Accumulated);
    }

    [Fact]
    public void Start_time_is_stored_as_utc()
    {
        var local = new DateTimeOffset(2026, 10, 24, 14, 0, 0, TimeSpan.FromHours(2));

        var state = TimerState.Zero.Start(local);

        Assert.Equal(TimeSpan.Zero, state.StartedAtUtc!.Value.Offset);
        Assert.Equal(T0, state.StartedAtUtc);
    }

    [Fact]
    public void Elapsed_saturates_instead_of_overflowing()
    {
        var state = TimerState.Create(true, DateTimeOffset.MinValue, TimeSpan.FromDays(10_000_000)).Value;

        Assert.Equal(TimeSpan.MaxValue, state.ElapsedAt(DateTimeOffset.MaxValue));
    }

    [Theory]
    [InlineData(false, true, 0L, "start time")]
    [InlineData(true, false, 0L, "no start time")]
    [InlineData(false, false, -1L, "negative")]
    [InlineData(false, false, 1L, "whole milliseconds")]
    public void Create_rejects_broken_invariants(bool running, bool hasStart, long accumulatedTicks, string reason)
    {
        var result = TimerState.Create(running, hasStart ? T0 : null, TimeSpan.FromTicks(accumulatedTicks));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.InvalidInput, result.Error.Code);
        Assert.Contains(reason, result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_rejects_non_utc_start()
    {
        var result = TimerState.Create(true, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(1)), TimeSpan.Zero);

        Assert.True(result.IsFailure);
    }
}
