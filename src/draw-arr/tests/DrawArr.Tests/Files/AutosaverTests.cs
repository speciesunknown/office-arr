using DrawArr.Core;
using DrawArr.Files;

namespace DrawArr.Tests.Files;

public sealed class AutosaverTests
{
    private readonly ManualClock _clock = new();
    private readonly List<Drawing> _saved = [];
    private Result _next = Result.Ok();

    private Autosaver Make()
    {
        return new Autosaver(_clock, Autosaver.DefaultDelay, drawing =>
        {
            _saved.Add(drawing);
            return _next;
        });
    }

    private static Drawing Version(int boxes)
    {
        var drawing = Drawing.Empty;
        for (var i = 1; i <= boxes; i++)
            drawing = drawing.With(Tests.Make.Box(i, 0, 0, 5, 5));
        return drawing;
    }

    [Fact]
    public void A_burst_of_changes_is_saved_once_after_the_delay()
    {
        using var saver = Make();
        saver.Changed(Version(1), 1);
        _clock.Advance(TimeSpan.FromSeconds(1));
        saver.Changed(Version(2), 2);
        _clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Empty(_saved);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([2], _saved.Select(d => d.Count));
        Assert.False(saver.HasUnsaved);
    }

    [Fact]
    public void Flush_saves_at_once_and_only_when_there_is_something_new()
    {
        using var saver = Make();
        Assert.True(saver.Flush().IsSuccess);
        saver.Changed(Version(1), 1);
        saver.Flush();
        saver.Flush();
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Single(_saved);
    }

    [Fact]
    public void An_older_version_arriving_late_is_ignored()
    {
        using var saver = Make();
        saver.Changed(Version(2), 2);
        saver.Changed(Version(1), 1);
        saver.Flush();
        Assert.Equal([2], _saved.Select(d => d.Count));
    }

    [Fact]
    public void A_failed_save_is_reported_and_tried_again_on_the_next_flush()
    {
        using var saver = Make();
        var events = new List<AutosaveEventArgs>();
        saver.Saved += (_, e) => events.Add(e);
        _next = Result.Fail(ErrorCode.FileUnavailable, "disk full");

        saver.Changed(Version(1), 7);
        _clock.Advance(TimeSpan.FromSeconds(3));
        Assert.True(saver.HasUnsaved);

        _next = Result.Ok();
        Assert.True(saver.Flush().IsSuccess);
        Assert.Equal([(7L, false), (7L, true)], events.Select(e => (e.Revision, e.Result.IsSuccess)));
        Assert.False(saver.HasUnsaved);
    }

    [Fact]
    public void A_disposed_saver_no_longer_saves_on_its_own()
    {
        var saver = Make();
        saver.Changed(Version(1), 1);
        saver.Dispose();
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(_saved);
    }
}
