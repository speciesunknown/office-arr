using TimeArr.Core;

namespace TimeArr.Tests.Core;

/// <summary>Edge cases for <see cref="TimerState"/> not covered by <see cref="TimerStateTests"/>.</summary>
public class TimerStateGapTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Elapsed_at_the_start_instant_is_the_banked_time()
    {
        var state = TimerState.Create(true, T0, TimeSpan.FromSeconds(7)).Value;

        Assert.Equal(TimeSpan.FromSeconds(7), state.ElapsedAt(T0));
    }

    [Fact]
    public void Elapsed_uses_the_instant_not_the_offset_of_now()
    {
        var state = TimerState.Zero.Start(T0);
        var fiveMinutesLaterInCet = new DateTimeOffset(2026, 10, 24, 14, 5, 0, TimeSpan.FromHours(2));

        Assert.Equal(TimeSpan.FromMinutes(5), state.ElapsedAt(fiveMinutesLaterInCet));
    }

    [Fact]
    public void Reset_while_running_with_clock_set_back_still_starts_from_now()
    {
        var state = TimerState.Zero.Start(T0).Reset(T0.AddHours(-1));

        Assert.True(state.IsRunning);
        Assert.Equal(TimeSpan.Zero, state.Accumulated);
        Assert.Equal(T0.AddHours(-1), state.StartedAtUtc);
    }

    [Fact]
    public void Reset_of_a_running_timer_stores_utc()
    {
        var local = new DateTimeOffset(2026, 10, 24, 14, 0, 0, TimeSpan.FromHours(2));

        var state = TimerState.Zero.Start(T0.AddHours(-3)).Reset(local);

        Assert.Equal(TimeSpan.Zero, state.StartedAtUtc!.Value.Offset);
        Assert.Equal(T0, state.StartedAtUtc);
    }

    [Fact]
    public void Reset_while_paused_at_zero_returns_zero()
    {
        Assert.Equal(TimerState.Zero, TimerState.Zero.Reset(T0));
    }

    [Fact]
    public void Clock_jumping_back_and_forward_counts_only_forward_time_from_start()
    {
        var state = TimerState.Zero.Start(T0);

        Assert.Equal(TimeSpan.Zero, state.ElapsedAt(T0.AddDays(-1)));
        Assert.Equal(TimeSpan.FromSeconds(1), state.ElapsedAt(T0.AddSeconds(1)));
    }

    [Fact]
    public void Pause_at_saturation_banks_whole_milliseconds_that_can_be_rebuilt()
    {
        var state = TimerState.Create(true, DateTimeOffset.MinValue, TimeSpan.Zero).Value;

        var paused = state.Pause(DateTimeOffset.MaxValue);

        Assert.Equal(0, paused.Accumulated.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.True(TimerState.Create(false, null, paused.Accumulated).IsSuccess);
    }

    [Fact]
    public void Many_start_pause_cycles_add_up_exactly()
    {
        var state = TimerState.Zero;
        var now = T0;
        for (var i = 0; i < 1000; i++)
        {
            state = state.Start(now);
            now = now.AddMilliseconds(3);
            state = state.Pause(now);
            now = now.AddSeconds(10);
        }

        Assert.Equal(TimeSpan.FromMilliseconds(3000), state.Accumulated);
    }

    [Fact]
    public void Created_state_equals_the_same_state_built_by_transitions()
    {
        var built = TimerState.Zero.Start(T0).Pause(T0.AddSeconds(5)).Start(T0.AddMinutes(1));
        var created = TimerState.Create(true, T0.AddMinutes(1), TimeSpan.FromSeconds(5)).Value;

        Assert.Equal(built, created);
    }

    [Fact]
    public void Create_accepts_the_largest_whole_millisecond_value()
    {
        var max = TimeSpan.FromTicks(TimeSpan.MaxValue.Ticks - TimeSpan.MaxValue.Ticks % TimeSpan.TicksPerMillisecond);

        Assert.True(TimerState.Create(false, null, max).IsSuccess);
    }

    [Fact]
    public void Create_paused_with_zero_is_equal_to_zero()
    {
        Assert.Equal(TimerState.Zero, TimerState.Create(false, null, TimeSpan.Zero).Value);
    }
}
