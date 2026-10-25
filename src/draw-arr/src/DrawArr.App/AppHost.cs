using DrawArr.Commands;
using DrawArr.Core;
using DrawArr.Files;

namespace DrawArr.App;

/// <summary>How a document is closed.</summary>
public enum CloseMode
{
    /// <summary>Save the last changes first, and stay open if that fails.</summary>
    SaveFirst = 1,
    /// <summary>Close without saving, which the user chooses after a save has failed.</summary>
    Discard = 2,
}

/// <summary>The running instance's documents, one session per window, all sharing one workspace and one command queue.</summary>
public sealed class AppHost : IDisposable
{
    private readonly TimeProvider _clock;
    private readonly AutosaveFolder _autosave;
    private readonly Action<Action> _post;
    private readonly Lock _gate = new();
    private readonly List<DocumentSession> _sessions = [];

    /// <param name="clock">The clock autosave delays run on.</param>
    /// <param name="autosave">Where unnamed drawings are autosaved.</param>
    /// <param name="post">Runs an action on the UI thread later.</param>
    public AppHost(TimeProvider clock, AutosaveFolder autosave, Action<Action> post)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _autosave = autosave ?? throw new ArgumentNullException(nameof(autosave));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        Workspace = new Workspace();
        Workspace.Changed += OnChanged;
        Queue = new CommandQueue(Workspace);
    }

    public Workspace Workspace { get; }

    public CommandQueue Queue { get; }

    /// <summary>Raised on the UI thread when a document opens, for the app to give it a window.</summary>
    public event EventHandler<DocumentSession>? Opened;

    public IReadOnlyList<DocumentSession> Sessions
    {
        get
        {
            lock (_gate)
                return [.. _sessions];
        }
    }

    /// <summary>Opens a new, empty drawing that autosaves until it is given a name.</summary>
    public Result<DocumentSession> New()
    {
        var path = _autosave.NewFile(_clock);
        return path.IsSuccess
            ? Add(new Document(Drawing.Empty), path.Value, DocumentLocation.Autosave)
            : Result<DocumentSession>.Fail(path.Error);
    }

    /// <summary>Opens a drawing file, or returns the session that already has it open.</summary>
    public async Task<Result<DocumentSession>> Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Find(path) is { } open)
            return open;
        // Loading runs on a thread of its own, so a file that blocks can't freeze the windows or the queue.
        var loaded = await Task.Run(() => DrawingFile.Load(path)).ConfigureAwait(false);
        if (loaded.IsFailure)
            return Result<DocumentSession>.Fail(loaded.Error);
        return Add(new Document(loaded.Value), path, DocumentLocation.Named);
    }

    /// <summary>The session with a file open, comparing the files paths finally point to, so a link and its target are the same file.</summary>
    public DocumentSession? Find(string path)
    {
        if (Key(path) is not { } key)
            return null;
        lock (_gate)
            return FindLocked(key);
    }

    /// <summary>Saves a session under a new name, unless another window already has that file open.</summary>
    public Task<Result> SaveAs(DocumentSession session, string path)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(path);
        if (Find(path) is { } other && other != session)
            return Task.FromResult(Result.Fail(ErrorCode.Conflict, $"{Path.GetFileName(path)} is open in another window."));
        return session.SaveAs(path);
    }

    private DocumentSession? FindLocked(string key) =>
        _sessions.FirstOrDefault(s => string.Equals(s.FilePath, key, StringComparison.Ordinal));

    /// <summary>The real file a path names, which sessions are pinned to, or null if it can't name a file.</summary>
    private static string? Key(string path) => DrawingFile.RealPath(path);

    /// <summary>Closes a session, saving its last changes first unless told to discard them, and keeping it open if that save fails.</summary>
    public async Task<Result> Close(DocumentSession session, CloseMode mode = CloseMode.SaveFirst)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a close mode.");
        if (mode == CloseMode.SaveFirst)
        {
            var flushed = await session.Flush().ConfigureAwait(false);
            if (flushed.IsFailure)
                return flushed;
        }
        lock (_gate)
            _sessions.Remove(session);
        Workspace.Close(session.DocumentId);
        session.Dispose();
        return Result.Ok();
    }

    private Result<DocumentSession> Add(Document document, string path, DocumentLocation location)
    {
        DocumentSession session;
        lock (_gate)
        {
            // Checked again under the lock, so two opens of one file that overlap end up with one window.
            if (Key(path) is { } key && FindLocked(key) is { } open)
                return open;
            var id = Workspace.Open(document);
            session = new DocumentSession(id, document, path, location, Queue, _clock, _post, SaveAs);
            _sessions.Add(session);
        }
        _post(() => Opened?.Invoke(this, session));
        return session;
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        DocumentSession? session;
        lock (_gate)
            session = _sessions.FirstOrDefault(s => s.DocumentId == e.DocumentId);
        session?.Changed(e.Reply);
    }

    /// <summary>Saves every document's last changes and stops the queue.</summary>
    public void Dispose()
    {
        foreach (var session in Sessions)
        {
            session.Flush().GetAwaiter().GetResult();
            session.Dispose();
        }
        Queue.Dispose();
    }
}
