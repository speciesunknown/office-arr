using DrawArr.Core;
using DrawArr.Files;

namespace DrawArr.Tests.Files;

/// <summary>Timing, failure and thread edge cases for <see cref="Autosaver"/> beyond <see cref="AutosaverTests"/>.</summary>
public sealed class AutosaverGapTests
{
    private static readonly TimeSpan Delay = Autosaver.DefaultDelay;

    private static Drawing Version(int n) => Drawing.Empty.With(Make.Box(1, 0, 0, 5, 5, Colour.White, $"v{n}"));

    /// <summary>A save function that records what it was given and fails while told to.</summary>
    private sealed class Recorder
    {
        public List<string> Saved { get; } = [];
        public int Attempts { get; private set; }
        public bool Failing { get; set; }
        public Action? During { get; set; }

        public Result Save(Drawing drawing)
        {
            Attempts++;
            During?.Invoke();
            if (Failing)
                return Result.Fail(ErrorCode.FileUnavailable, "disk full");
            Saved.Add(drawing.Boxes[1].Text);
            return Result.Ok();
        }
    }

    [Fact]
    public void Each_change_restarts_the_delay_and_only_the_newest_is_saved()
    {
        var clock = new ManualClock();
        var saves = new Recorder();
        using var saver = new Autosaver(clock, Delay, saves.Save);

        saver.Changed(Version(1), 1);
        clock.Advance(Delay - TimeSpan.FromMilliseconds(1));
        saver.Changed(Version(2), 2);
        clock.Advance(Delay - TimeSpan.FromMilliseconds(1));
        Assert.Empty(saves.Saved);

        clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(["v2"], saves.Saved);
        Assert.False(saver.HasUnsaved);
    }

    [Fact]
    public void A_save_happens_exactly_at_the_delay()
    {
        var clock = new ManualClock();
        var saves = new Recorder();
        using var saver = new Autosaver(clock, Delay, saves.Save);

        saver.Changed(Version(1), 1);
        clock.Advance(Delay);

        Assert.Equal(["v1"], saves.Saved);
    }

    [Fact]
    public void The_same_revision_twice_is_saved_once()
    {
        var clock = new ManualClock();
        var saves = new Recorder();
        using var saver = new Autosaver(clock, Delay, saves.Save);

        saver.Changed(Version(1), 1);
        clock.Advance(Delay);
        saver.Changed(Version(1), 1);
        clock.Advance(Delay);

        Assert.Single(saves.Saved);
    }

    [Fact]
    public void A_failed_autosave_is_tried_again_without_waiting_for_another_edit()
    {
        // After a failure the drawing stays pending, but nothing re-arms the timer, so the last edits wait for the next edit or a manual save.
        var clock = new ManualClock();
        var saves = new Recorder { Failing = true };
        using var saver = new Autosaver(clock, Delay, saves.Save);
        saver.Changed(Version(1), 1);
        clock.Advance(Delay);
        Assert.True(saver.HasUnsaved);

        saves.Failing = false;
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(["v1"], saves.Saved);
        Assert.False(saver.HasUnsaved);
    }

    [Fact]
    public void A_change_made_while_a_save_runs_is_saved_after_its_own_delay()
    {
        var clock = new ManualClock();
        var saves = new Recorder();
        using var saver = new Autosaver(clock, Delay, saves.Save);
        saves.During = () =>
        {
            saves.During = null;
            saver.Changed(Version(2), 2);
        };

        saver.Changed(Version(1), 1);
        clock.Advance(Delay);
        Assert.Equal(["v1"], saves.Saved);
        Assert.True(saver.HasUnsaved);
        clock.Advance(Delay);

        Assert.Equal(["v1", "v2"], saves.Saved);
    }

    [Fact]
    public void A_newer_change_during_a_failed_save_is_not_replaced_by_the_old_one()
    {
        var clock = new ManualClock();
        var saves = new Recorder { Failing = true };
        using var saver = new Autosaver(clock, Delay, saves.Save);
        saves.During = () =>
        {
            saves.During = null;
            saver.Changed(Version(2), 2);
        };
        saver.Changed(Version(1), 1);
        clock.Advance(Delay);

        saves.Failing = false;
        Assert.True(saver.Flush().IsSuccess);

        Assert.Equal(["v2"], saves.Saved);
    }

    [Fact]
    public void Each_attempt_is_announced_with_its_revision_and_result()
    {
        var clock = new ManualClock();
        var saves = new Recorder { Failing = true };
        using var saver = new Autosaver(clock, Delay, saves.Save);
        var events = new List<(long Revision, bool Ok)>();
        saver.Saved += (_, e) => events.Add((e.Revision, e.Result.IsSuccess));

        saver.Changed(Version(3), 3);
        clock.Advance(Delay);
        saves.Failing = false;
        saver.Flush();

        Assert.Equal([(3L, false), (3L, true)], events);
    }

    [Fact]
    public void A_change_after_dispose_does_not_throw_on_the_real_clock()
    {
        // The app wires Workspace.Changed to the saver, and a change can still arrive just after its window closes.
        var saves = new Recorder();
        var saver = new Autosaver(TimeProvider.System, Delay, saves.Save);
        saver.Dispose();

        Assert.Null(Record.Exception(() => saver.Changed(Version(1), 1)));
        Assert.True(saver.Flush().IsSuccess);
        Assert.Equal(["v1"], saves.Saved);
    }

    [Fact]
    public void Changes_from_many_threads_end_with_the_newest_saved()
    {
        var clock = new ManualClock();
        var saves = new Recorder();
        using var saver = new Autosaver(clock, Delay, saves.Save);
        var versions = Enumerable.Range(1, 500).Select(i => (Drawing: Version(i), Revision: (long)i)).ToArray();

        Parallel.ForEach(versions.Reverse(), v => saver.Changed(v.Drawing, v.Revision));
        saver.Flush();

        Assert.Equal("v500", saves.Saved[^1]);
    }

    [Fact]
    public void Flushing_from_two_threads_saves_once()
    {
        var clock = new ManualClock();
        var saves = new Recorder();
        using var saver = new Autosaver(clock, Delay, saves.Save);
        saver.Changed(Version(1), 1);

        Parallel.Invoke(() => saver.Flush(), () => saver.Flush());

        Assert.Equal(1, saves.Attempts);
    }

    [Fact]
    public void Null_arguments_are_bugs()
    {
        var clock = new ManualClock();
        using var saver = new Autosaver(clock, Delay, _ => Result.Ok());

        Assert.Throws<ArgumentNullException>(() => new Autosaver(null!, Delay, _ => Result.Ok()));
        Assert.Throws<ArgumentNullException>(() => new Autosaver(clock, Delay, null!));
        Assert.Throws<ArgumentNullException>(() => saver.Changed(null!, 1));
    }
}
