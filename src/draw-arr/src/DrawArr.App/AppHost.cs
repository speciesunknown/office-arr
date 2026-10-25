using DrawArr.Commands;
using DrawArr.Core;
using DrawArr.Files;

namespace DrawArr.App;

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
        return Find(path) ?? Add(new Document(loaded.Value), path, DocumentLocation.Named);
    }

    /// <summary>The session with a file open, comparing full paths.</summary>
    public DocumentSession? Find(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            // A path that can't be a file can't be open either.
            return null;
        }
        lock (_gate)
            return _sessions.FirstOrDefault(s => string.Equals(s.FilePath, full, StringComparison.Ordinal));
    }

    /// <summary>Saves a session's last changes and closes it, keeping it open if the save fails.</summary>
    public async Task<Result> Close(DocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var flushed = await session.Flush().ConfigureAwait(false);
        if (flushed.IsFailure)
            return flushed;
        lock (_gate)
            _sessions.Remove(session);
        Workspace.Close(session.DocumentId);
        session.Dispose();
        return Result.Ok();
    }

    private Result<DocumentSession> Add(Document document, string path, DocumentLocation location)
    {
        var id = Workspace.Open(document);
        var session = new DocumentSession(id, document, path, location, Queue, _clock, _post);
        lock (_gate)
            _sessions.Add(session);
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
