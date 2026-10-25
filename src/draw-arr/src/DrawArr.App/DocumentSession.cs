using DrawArr.Commands;
using DrawArr.Core;
using DrawArr.Files;
using DrawArr.ViewModels;

namespace DrawArr.App;

/// <summary>Where a document is saved: a file the user named, or the autosave folder until it gets a name.</summary>
public enum DocumentLocation
{
    Named = 1,
    Autosave = 2,
}

/// <summary>Whether a session is open or being closed.</summary>
internal enum SessionState
{
    Open = 1,
    Closing = 2,
}

/// <summary>One open document: its id in the workspace, its file, its autosaver and the viewmodel of its window.</summary>
public sealed class DocumentSession : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Requester _window;
    private readonly Autosaver _autosaver;
    private readonly Action<Action> _post;
    private readonly Func<DocumentSession, string, Task<Result>> _saveAs;
    private string _path;
    private DocumentLocation _location;
    private Drawing _latest;
    private long _latestRevision;

    internal DocumentSession(long documentId, Document document, string path, DocumentLocation location,
        CommandQueue queue, TimeProvider clock, Action<Action> post, Func<DocumentSession, string, Task<Result>> saveAs)
    {
        _saveAs = saveAs;
        DocumentId = documentId;
        Document = document;
        // Pinned to the real file now, and saved without following links later, so a link planted in its place can't redirect autosaves.
        _path = DrawingFile.RealPath(path) ?? Path.GetFullPath(path);
        _location = location;
        _post = post;
        _latest = document.Drawing;
        _latestRevision = document.Revision;
        _window = queue.Join(CommandSource.Window);
        _autosaver = new Autosaver(clock, Autosaver.DefaultDelay, drawing => DrawingFile.Save(drawing, FilePath, LinkHandling.Refuse));
        _autosaver.Saved += OnSaved;
        ViewModel = new DrawingViewModel(Title(), new DocumentReply(document.Drawing, document.History, document.Revision, null, DocumentChange.None),
            command => _window.Send(documentId, command), Save, post);
    }

    public long DocumentId { get; }

    /// <summary>The document itself, which only the queue's worker may touch, kept so a close that fails can put it back.</summary>
    internal Document Document { get; }

    /// <summary>Whether the session is on its way out, during which it takes no new work.</summary>
    internal SessionState State { get; set; } = SessionState.Open;

    public DrawingViewModel ViewModel { get; }

    /// <summary>Asks the user for a file to save under, or null if they cancel, set by the window.</summary>
    public Func<Task<string?>>? AskForPath { get; set; }

    /// <summary>The full path saves go to now.</summary>
    public string FilePath
    {
        get
        {
            lock (_gate)
                return _path;
        }
    }

    public DocumentLocation Location
    {
        get
        {
            lock (_gate)
                return _location;
        }
    }

    /// <summary>The newest version of the drawing this session has seen.</summary>
    public Drawing Latest
    {
        get
        {
            lock (_gate)
                return _latest;
        }
    }

    /// <summary>Takes in a change made by anyone, on the queue's worker thread.</summary>
    internal void Changed(DocumentReply reply)
    {
        lock (_gate)
        {
            if (reply.Revision <= _latestRevision)
                return;
            (_latest, _latestRevision) = (reply.Drawing, reply.Revision);
        }
        _autosaver.Changed(reply.Drawing, reply.Revision);
        _post(() => ViewModel.Apply(reply));
    }

    /// <summary>Saves now, asking for a name first if the drawing only lives in the autosave folder.</summary>
    public async Task<Result> Save()
    {
        if (Location == DocumentLocation.Named)
            return await Task.Run(_autosaver.Flush).ConfigureAwait(false);
        var path = AskForPath is null ? null : await AskForPath().ConfigureAwait(false);
        return path is null
            ? Result.Fail(ErrorCode.InvalidInput, "Not saved: no file was chosen.")
            : await _saveAs(this, path).ConfigureAwait(false);
    }

    /// <summary>Saves under a new name, which later saves and autosaves then go to, called by <see cref="AppHost.SaveAs"/> once it has checked no other window has that file.</summary>
    internal async Task<Result> SaveAs(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string oldPath;
        DocumentLocation oldLocation;
        Drawing drawing;
        long revision;
        lock (_gate)
        {
            (oldPath, oldLocation) = (_path, _location);
            (_path, _location) = (DrawingFile.RealPath(path) ?? Path.GetFullPath(path), DocumentLocation.Named);
            (drawing, revision) = (_latest, _latestRevision);
        }
        _autosaver.MarkUnsaved(drawing, revision);
        var saved = await Task.Run(_autosaver.Flush).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            lock (_gate)
                (_path, _location) = (oldPath, oldLocation);
            // The failed attempt marked the title as not saved, though the old file is still up to date.
            _post(() => ViewModel.Title = Title());
            return saved;
        }
        // The autosave copy has done its job once the drawing is saved under a name.
        if (oldLocation == DocumentLocation.Autosave)
            TryDelete(oldPath);
        _post(() => ViewModel.Title = Title());
        return saved;
    }

    /// <summary>Saves whatever hasn't been saved yet, as closing the window does.</summary>
    public Task<Result> Flush() => Task.Run(_autosaver.Flush);

    private void OnSaved(object? sender, AutosaveEventArgs e)
    {
        if (e.Result.IsFailure)
            _post(() => ViewModel.Title = Title() + " (not saved)");
        else
            _post(() => ViewModel.Title = Title());
    }

    private string Title()
    {
        lock (_gate)
            return _location == DocumentLocation.Named ? Path.GetFileName(_path) : "Untitled (autosaved)";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A stale autosave copy is harmless, and the save that matters has succeeded.
        }
    }

    public void Dispose()
    {
        _autosaver.Saved -= OnSaved;
        _autosaver.Dispose();
        _window.Dispose();
    }
}
