using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DrawArr.App;
using DrawArr.Commands;
using DrawArr.Core;
using DrawArr.Files;
using DrawArr.Tests;

namespace DrawArr.UiTests;

/// <summary>Documents, autosave timing and Save As in <see cref="AppHost"/> and <see cref="DocumentSession"/>, on a clock the tests move.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class SessionGapTests : IDisposable
{
    private static readonly UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string _folder = Directory.CreateTempSubdirectory("draw-arr-session-").FullName;
    private readonly ManualClock _clock = new();
    private readonly AppHost _host;
    private readonly Requester _agent;

    public SessionGapTests()
    {
        _host = new AppHost(_clock, new AutosaveFolder(Path.Combine(_folder, "autosave")), action => Dispatcher.UIThread.Post(action));
        _agent = _host.Queue.Join(CommandSource.Agent);
    }

    public void Dispose()
    {
        _host.Dispose();
        foreach (var directory in Directory.GetDirectories(_folder, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(directory, Private);
        Directory.Delete(_folder, recursive: true);
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private void Change(DocumentSession session, int x = 0)
    {
        var reply = _agent.Send(session.DocumentId, new DrawCommand.AddBox(x, 0, 10, 10, Colour.White)).GetAwaiter().GetResult();
        Assert.True(reply.IsSuccess, reply.ToString());
    }

    private DocumentSession New()
    {
        var created = _host.New();
        Assert.True(created.IsSuccess, created.ToString());
        return created.Value;
    }

    private string WriteFile(string name, int boxes = 1)
    {
        var path = Path.Combine(_folder, name);
        var drawing = Drawing.Empty;
        for (var id = 1; id <= boxes; id++)
            drawing = drawing.With(Box.Create(id, new Rect(id * 20, 0, 10, 10), Colour.White, "").Value);
        Assert.True(DrawingFile.Save(drawing, path).IsSuccess);
        return path;
    }

    private DocumentSession Open(string path)
    {
        var opened = _host.Open(path).GetAwaiter().GetResult();
        Assert.True(opened.IsSuccess, opened.ToString());
        return opened.Value;
    }

    // --- autosave timing ---

    [AvaloniaFact]
    public void A_change_is_autosaved_two_seconds_later_and_not_before()
    {
        var session = New();
        Change(session);

        _clock.Advance(TimeSpan.FromSeconds(1.9));
        Assert.False(File.Exists(session.FilePath));

        _clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(1, DrawingFile.Load(session.FilePath).Value.Count);
    }

    [AvaloniaFact]
    public void Another_change_within_the_delay_restarts_it()
    {
        var session = New();
        Change(session, 0);
        _clock.Advance(TimeSpan.FromSeconds(1.5));
        Change(session, 50);

        _clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.False(File.Exists(session.FilePath));

        _clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(2, DrawingFile.Load(session.FilePath).Value.Count);
    }

    [AvaloniaFact]
    public void Each_document_autosaves_to_its_own_file()
    {
        var first = New();
        var second = New();
        Change(first, 0);
        Change(second, 0);
        Change(second, 50);

        _clock.Advance(Autosaver.DefaultDelay);

        Assert.NotEqual(first.FilePath, second.FilePath);
        Assert.Equal(1, DrawingFile.Load(first.FilePath).Value.Count);
        Assert.Equal(2, DrawingFile.Load(second.FilePath).Value.Count);
    }

    [AvaloniaFact]
    public void A_failed_autosave_marks_the_title_and_a_later_success_clears_the_mark()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_folder, "named")).FullName;
        var session = New();
        Assert.True(_host.SaveAs(session, Path.Combine(folder, "a.xml")).GetAwaiter().GetResult().IsSuccess);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Change(session);

        _clock.Advance(Autosaver.DefaultDelay);
        Pump();
        Assert.Equal("a.xml (not saved)", session.ViewModel.Title);

        File.SetUnixFileMode(folder, Private);
        _clock.Advance(Autosaver.RetryDelays[0]);
        Pump();
        Assert.Equal("a.xml", session.ViewModel.Title);
        Assert.Equal(1, DrawingFile.Load(session.FilePath).Value.Count);
    }

    [AvaloniaFact]
    public void Disposing_the_host_saves_changes_the_delay_hadnt_saved_yet()
    {
        var session = New();
        var path = session.FilePath;
        Change(session);

        _host.Dispose();

        Assert.Equal(1, DrawingFile.Load(path).Value.Count);
    }

    // --- Save As ---

    [AvaloniaFact]
    public void A_failed_save_as_keeps_the_old_file_title_and_autosave_copy()
    {
        var session = New();
        Change(session);
        Assert.True(session.Flush().GetAwaiter().GetResult().IsSuccess);
        var autosave = session.FilePath;

        var saved = _host.SaveAs(session, Path.Combine(_folder, "no-such-folder", "a.xml")).GetAwaiter().GetResult();
        Pump();

        Assert.True(saved.IsFailure);
        Assert.Equal(autosave, session.FilePath);
        Assert.Equal(DocumentLocation.Autosave, session.Location);
        Assert.Equal("Untitled (autosaved)", session.ViewModel.Title);
        Assert.True(File.Exists(autosave));
    }

    [AvaloniaFact]
    public void Save_as_of_a_named_drawing_leaves_the_old_file_as_it_was()
    {
        var original = WriteFile("original.xml", boxes: 1);
        var session = Open(original);
        Change(session, 200);

        Assert.True(_host.SaveAs(session, Path.Combine(_folder, "copy.xml")).GetAwaiter().GetResult().IsSuccess);

        Assert.Equal(1, DrawingFile.Load(original).Value.Count);
        Assert.Equal(2, DrawingFile.Load(Path.Combine(_folder, "copy.xml")).Value.Count);
    }

    [AvaloniaFact]
    public void Save_as_onto_a_file_another_window_has_open_is_refused()
    {
        var other = WriteFile("other.xml", boxes: 3);
        var otherSession = Open(other);
        var session = New();
        Change(session);

        var saved = _host.SaveAs(session, other).GetAwaiter().GetResult();

        // Otherwise both windows autosave over each other, and opening the file again finds the wrong one.
        Assert.True(saved.IsFailure, "Save As replaced a file another window is editing.");
        Assert.Equal(3, DrawingFile.Load(other).Value.Count);
        Assert.Same(otherSession, _host.Find(other));
    }

    [AvaloniaFact]
    public void After_save_as_opening_the_new_name_finds_the_same_window()
    {
        var session = New();
        Change(session);
        var named = Path.Combine(_folder, "named.xml");
        Assert.True(_host.SaveAs(session, named).GetAwaiter().GetResult().IsSuccess);

        Assert.Same(session, Open(named));
        Assert.Single(_host.Sessions);
    }

    // --- opening ---

    [AvaloniaFact]
    public void Opening_a_file_through_a_symlink_finds_the_window_that_has_it()
    {
        var real = WriteFile("real.xml");
        var link = Path.Combine(_folder, "link.xml");
        File.CreateSymbolicLink(link, real);
        var first = Open(real);

        var second = Open(link);

        Assert.Same(first, second);
        Assert.Single(_host.Sessions);
    }

    [AvaloniaFact]
    public void Opening_a_file_by_a_relative_path_finds_the_window_that_has_it()
    {
        var real = WriteFile("real.xml");
        var first = Open(real);

        var relative = Path.GetRelativePath(Environment.CurrentDirectory, real);

        Assert.Same(first, Open(relative));
    }

    [AvaloniaFact]
    public async Task Opening_the_same_file_many_times_at_once_gives_one_window()
    {
        var path = WriteFile("busy.xml", boxes: 200);

        var opened = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _host.Open(path), TestContext.Current.CancellationToken)));

        Assert.All(opened, o => Assert.True(o.IsSuccess));
        Assert.Single(opened.Select(o => o.Value).Distinct());
        Assert.Single(_host.Sessions);
    }

    [AvaloniaFact]
    public void Opening_a_file_that_isnt_a_drawing_is_refused_and_opens_nothing()
    {
        var path = Path.Combine(_folder, "notes.xml");
        File.WriteAllText(path, "not xml at all");

        var opened = _host.Open(path).GetAwaiter().GetResult();

        Assert.True(opened.IsFailure);
        Assert.Empty(_host.Sessions);
    }

    [AvaloniaFact]
    public void Closing_a_document_frees_its_file_to_be_opened_again_as_a_new_session()
    {
        var path = WriteFile("again.xml");
        var first = Open(path);
        Assert.True(_host.Close(first).GetAwaiter().GetResult().IsSuccess);

        var second = Open(path);

        Assert.NotSame(first, second);
        Assert.Single(_host.Sessions);
    }

    [AvaloniaFact]
    public void A_change_to_a_closed_document_is_refused_not_lost_silently()
    {
        var session = New();
        Assert.True(_host.Close(session).GetAwaiter().GetResult().IsSuccess);

        var reply = _agent.Send(session.DocumentId, new DrawCommand.AddBox(0, 0, 10, 10, Colour.White)).GetAwaiter().GetResult();

        Assert.True(reply.IsFailure);
    }
}
