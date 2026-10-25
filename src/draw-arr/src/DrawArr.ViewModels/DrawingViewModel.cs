using System.Collections.ObjectModel;
using DrawArr.Commands;
using DrawArr.Core;

namespace DrawArr.ViewModels;

/// <summary>One window's drawing: what to show, and what pointer, keyboard and sidebar input turns into as commands.</summary>
/// <remarks>Call it on the UI thread only; replies from the queue come back through the post action it is given.</remarks>
public sealed class DrawingViewModel : ViewModelBase
{
    /// <summary>The size of a box taken from the sidebar.</summary>
    public const int NewBoxWidth = 40;
    public const int NewBoxHeight = 20;

    /// <summary>How close in pixels the pointer must be to a line or handle to grab it.</summary>
    public const double GrabPixels = 6;

    private readonly Func<DrawCommand, Task<Result<DocumentReply>>> _send;
    private readonly Func<Task<Result>> _save;
    private readonly Action<Action> _post;
    private Drawing _committed;
    private Drawing? _preview;
    private long _revision;
    private Gesture _gesture = new Gesture.None();
    private Tool _tool = Tool.Select;
    private Colour _colour = Colour.White;
    private long? _selectedId;
    private string _selectedText = "";
    private string _status = "";
    private string _title;
    private ViewTransform _view = ViewTransform.Default;
    private GhostLine? _ghostLine;
    private GhostBox? _ghostBox;
    private HistoryView _history;
    // Commands sent and not yet answered, so tests and closing can wait for them.
    private readonly HashSet<Task> _inFlight = [];
    private InputState _input = InputState.Open;

    private enum InputState { Open = 1, Stopped = 2 }

    /// <param name="title">The window title, usually the file name.</param>
    /// <param name="initial">The document as it is when the window opens.</param>
    /// <param name="send">Sends a command for this window's document through the queue.</param>
    /// <param name="save">Saves the document now, as the Save menu does.</param>
    /// <param name="post">Runs an action on the UI thread later.</param>
    public DrawingViewModel(string title, DocumentReply initial, Func<DrawCommand, Task<Result<DocumentReply>>> send, Func<Task<Result>> save, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _title = title ?? throw new ArgumentNullException(nameof(title));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _committed = initial.Drawing;
        _revision = initial.Revision;
        _history = initial.History;
        UndoCommand = new RelayCommand(() => Run(new DrawCommand.Undo()), () => _history.CanUndo);
        RedoCommand = new RelayCommand(() => Run(new DrawCommand.Redo()), () => _history.CanRedo);
        DeleteCommand = new RelayCommand(DeleteSelected, () => _selectedId is not null);
        SaveCommand = new RelayCommand(() => _ = Save());
        BoxToolCommand = new RelayCommand(() => Tool = Tool.Box);
        LineToolCommand = new RelayCommand(() => Tool = Tool.Line);
        SelectToolCommand = new RelayCommand(() => Tool = Tool.Select);
        FillHistory();
    }

    /// <summary>Raised when anything the canvas draws has changed.</summary>
    public event EventHandler? Redraw;

    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand BoxToolCommand { get; }
    public RelayCommand LineToolCommand { get; }
    public RelayCommand SelectToolCommand { get; }

    /// <summary>The colours of the palette, in the order the sidebar shows them.</summary>
    public static IReadOnlyList<Colour> Palette { get; } = [Colour.White, Colour.Red, Colour.Yellow, Colour.Blue, Colour.Green];

    /// <summary>The drawing to show, which during a drag is a preview of where things will end up.</summary>
    public Drawing Drawing => _preview ?? _committed;

    public GhostLine? GhostLine => _ghostLine;
    public GhostBox? GhostBox => _ghostBox;

    public ObservableCollection<HistoryItem> History { get; } = [];

    public string Title
    {
        get => _title;
        set => Set(ref _title, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>The last thing worth telling the user, such as why a command was refused.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public ViewTransform View
    {
        get => _view;
        set
        {
            if (Set(ref _view, value ?? throw new ArgumentNullException(nameof(value))))
                OnRedraw();
        }
    }

    public Tool Tool
    {
        get => _tool;
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Not a tool.");
            if (Set(ref _tool, value))
            {
                CancelGesture();
                Raise(nameof(IsBoxTool));
                Raise(nameof(IsLineTool));
            }
        }
    }

    public bool IsBoxTool => _tool == Tool.Box;
    public bool IsLineTool => _tool == Tool.Line;

    /// <summary>The colour new boxes and lines get, which choosing also gives to the selection.</summary>
    public Colour Colour
    {
        get => _colour;
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Not a palette colour.");
            Set(ref _colour, value);
            if (_selectedId is { } id && ColourOf(id) is { } current && current != value)
                Run(new DrawCommand.SetColour(id, value));
        }
    }

    /// <summary>The selected box or line, or null.</summary>
    public long? SelectedId
    {
        get => _selectedId;
        private set
        {
            if (!Set(ref _selectedId, value))
                return;
            Raise(nameof(IsBoxSelected));
            _selectedText = value is { } id && _committed.Boxes.TryGetValue(id, out var box) ? box.Text : "";
            Raise(nameof(SelectedText));
            DeleteCommand.RaiseCanExecuteChanged();
            OnRedraw();
        }
    }

    public bool IsBoxSelected => _selectedId is { } id && _committed.Boxes.ContainsKey(id);

    /// <summary>The text being edited for the selected box, sent as a command by <see cref="CommitText"/>.</summary>
    public string SelectedText
    {
        get => _selectedText;
        set => Set(ref _selectedText, value ?? "");
    }

    /// <summary>Sends the edited text for the selected box, if it differs from what the box has.</summary>
    public Task CommitText()
    {
        if (_selectedId is { } id && _committed.Boxes.TryGetValue(id, out var box) && box.Text != _selectedText)
            return Send(new DrawCommand.SetText(id, _selectedText));
        return Task.CompletedTask;
    }

    /// <summary>Shows a message to the user, such as why a file couldn't be opened.</summary>
    public void Report(string message) => Status = message ?? throw new ArgumentNullException(nameof(message));

    /// <summary>Takes in a newer state of the document, from a reply or from a change someone else made.</summary>
    public void Apply(DocumentReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        // Each change arrives twice, as a reply and as a change notice, in either order, so only a newer revision is taken in.
        if (reply.Revision <= _revision)
            return;
        var before = _committed;
        _revision = reply.Revision;
        _committed = reply.Drawing;
        _history = reply.History;
        // A drag carries on unless the very thing being dragged was changed underneath it.
        if (GestureItem() is { } dragged && !SameItem(before, _committed, dragged))
            CancelGesture();
        if (_selectedId is { } id && !_committed.Boxes.ContainsKey(id) && !_committed.Lines.ContainsKey(id))
        {
            SelectedId = null;
        }
        else if (_selectedId is { } boxId && _committed.Boxes.TryGetValue(boxId, out var box)
                 && before.Boxes.TryGetValue(boxId, out var old) && old.Text != box.Text && _selectedText == old.Text)
        {
            // The field follows the box's new text only if the user hadn't started editing it.
            SelectedText = box.Text;
        }
        Raise(nameof(IsBoxSelected));
        FillHistory();
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
        OnRedraw();
    }

    /// <summary>Refuses further input and sends, as closing the window does while it saves.</summary>
    public void StopInput()
    {
        _input = InputState.Stopped;
        CancelGesture();
    }

    /// <summary>Takes input again, after a close that couldn't save.</summary>
    public void ResumeInput() => _input = InputState.Open;

    /// <summary>Saves now, reporting how it went in <see cref="Status"/>.</summary>
    public async Task Save()
    {
        // Text still being typed is part of what the user means to save.
        await CommitText().ConfigureAwait(true);
        await WhenIdle().ConfigureAwait(true);
        // Still on the caller's thread, the UI thread in the app, because saving may open a file picker.
        var saved = await _save().ConfigureAwait(false);
        _post(() => Status = saved.IsSuccess ? "Saved." : saved.Error.Message);
    }

    // --- pointer input, in millimetres ---

    public void PointerPressed(double x, double y)
    {
        if (_input == InputState.Stopped)
            return;
        var at = Snap(x, y);
        switch (_tool)
        {
            case Tool.Box:
                PlaceBox(at);
                return;
            case Tool.Line:
                _gesture = new Gesture.DrawingLine(EndAtFor(x, y), PointFor(x, y));
                _ghostLine = new GhostLine(PointFor(x, y), PointFor(x, y), _colour);
                OnRedraw();
                return;
            case Tool.Select:
                Grab(x, y, at);
                return;
            default:
                throw new InvalidOperationException($"Unhandled tool {_tool}.");
        }
    }

    public void PointerMoved(double x, double y)
    {
        var at = Snap(x, y);
        switch (_gesture)
        {
            case Gesture.None when _tool == Tool.Box:
                _ghostBox = new GhostBox(new Rect(at.X, at.Y, NewBoxWidth, NewBoxHeight), _colour);
                OnRedraw();
                break;
            case Gesture.None:
                break;
            case Gesture.MovingBox move:
                // The corner snaps and the size is kept, as MoveBox does, so the preview is where the box will land.
                var b = move.Box.Bounds;
                Preview(move.Box, new Rect(Grid.Snap(at.X - move.GrabX), Grid.Snap(at.Y - move.GrabY), b.Width, b.Height));
                break;
            case Gesture.ResizingBox resize:
                var r = resize.Box.Bounds;
                // Dragging the corner past the opposite edges stops at the smallest box rather than doing nothing.
                var right = Math.Max(at.X, r.X + Grid.MinSize);
                var bottom = Math.Max(at.Y, r.Y + Grid.MinSize);
                if (Grid.Resize(r, r.X, r.Y, right - r.X, bottom - r.Y) is { IsSuccess: true } resized)
                    Preview(resize.Box, resized.Value);
                break;
            case Gesture.DrawingLine draw:
                _ghostLine = new GhostLine(draw.From, PointFor(x, y), _colour);
                OnRedraw();
                break;
            case Gesture.MovingLineEnd end:
                var moved = end.Line.With(end.Which, LineEndFor(x, y, end.Line.Id));
                _preview = _committed.With(moved);
                OnRedraw();
                break;
            default:
                throw new InvalidOperationException($"Unhandled gesture {_gesture.GetType().Name}.");
        }
    }

    /// <summary>Ends a drag, sending one command for the whole of it.</summary>
    public Task PointerReleased(double x, double y)
    {
        var at = Snap(x, y);
        var gesture = _gesture;
        _gesture = new Gesture.None();
        DrawCommand? command = gesture switch
        {
            // The box lands where its preview shows, which stops at the limits when the pointer goes past them.
            Gesture.MovingBox move when _preview?.Boxes[move.Box.Id].Bounds is { } moved =>
                new DrawCommand.MoveBox(move.Box.Id, moved.X, moved.Y),
            Gesture.ResizingBox resize when _preview?.Boxes[resize.Box.Id].Bounds is { } shown =>
                new DrawCommand.ResizeBox(resize.Box.Id, shown.X, shown.Y, shown.Width, shown.Height),
            Gesture.DrawingLine draw when PointFor(x, y) != draw.From =>
                new DrawCommand.AddLine(draw.Start, EndAtFor(x, y), _colour),
            Gesture.MovingLineEnd end when _preview is not null =>
                new DrawCommand.MoveLineEnd(end.Line.Id, end.Which, EndAtFor(x, y)),
            _ => null,
        };
        _ghostLine = null;
        if (command is null)
        {
            _preview = null;
            OnRedraw();
            return Task.CompletedTask;
        }
        // The preview stays up until the reply arrives, so the drawing doesn't jump back for a moment.
        return Send(command, selectAdded: gesture is Gesture.DrawingLine);
    }

    /// <summary>Zooms in or out by a factor around a point in millimetres, as the mouse wheel does.</summary>
    public void Zoom(double factor, double x, double y) => View = _view.ZoomAround(factor, x, y);

    /// <summary>Moves the view by a distance in millimetres, as dragging with the middle button does.</summary>
    public void Pan(double dx, double dy) => View = _view.PanBy(dx, dy);

    /// <summary>Drops a drag in progress, as Escape does, leaving the drawing as it was.</summary>
    public void CancelGesture()
    {
        _gesture = new Gesture.None();
        _preview = null;
        _ghostLine = null;
        _ghostBox = null;
        OnRedraw();
    }

    // --- the work behind the input ---

    private void PlaceBox(Point at)
    {
        _ghostBox = null;
        Tool = Tool.Select;
        Observe(Send(new DrawCommand.AddBox(at.X, at.Y, NewBoxWidth, NewBoxHeight, _colour), selectAdded: true));
    }

    private void Grab(double x, double y, Point at)
    {
        var grab = GrabPixels / _view.Scale;
        if (_selectedId is { } lineId && _committed.Lines.TryGetValue(lineId, out var selectedLine))
        {
            foreach (var which in new[] { LineEndName.Start, LineEndName.End })
            {
                if (Distance(_committed.Resolve(selectedLine[which]), x, y) <= grab)
                {
                    _gesture = new Gesture.MovingLineEnd(selectedLine, which);
                    return;
                }
            }
        }
        if (_selectedId is { } boxId && _committed.Boxes.TryGetValue(boxId, out var selectedBox)
            && Distance(new Point(selectedBox.Bounds.Right, selectedBox.Bounds.Bottom), x, y) <= grab)
        {
            _gesture = new Gesture.ResizingBox(selectedBox);
            return;
        }
        if (LineAt(x, y, grab) is { } line)
        {
            SelectedId = line.Id;
            return;
        }
        if (BoxAt(x, y) is { } box)
        {
            SelectedId = box.Id;
            _gesture = new Gesture.MovingBox(box, at.X - box.Bounds.X, at.Y - box.Bounds.Y);
            return;
        }
        SelectedId = null;
    }

    private void Preview(Box box, Rect bounds)
    {
        var moved = Box.Create(box.Id, bounds, box.Colour, box.Text);
        if (moved.IsFailure)
            return;
        _preview = _committed.With(moved.Value);
        OnRedraw();
    }

    private void DeleteSelected()
    {
        if (_selectedId is { } id)
            Run(new DrawCommand.Delete(id));
    }

    private void Run(DrawCommand command) => Observe(Send(command));

    /// <summary>Lets a send nobody awaits still crash the app if it hits a bug, by rethrowing on the UI thread (CODING.md #4).</summary>
    private void Observe(Task task) =>
        task.ContinueWith(t => _post(() => t.GetAwaiter().GetResult()), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    /// <summary>Completes once every command this window has sent so far has been answered and applied.</summary>
    public Task WhenIdle()
    {
        lock (_inFlight)
            return Task.WhenAll([.. _inFlight]);
    }

    private Task Send(DrawCommand command, bool selectAdded = false)
    {
        if (_input == InputState.Stopped)
        {
            CancelGesture();
            return Task.CompletedTask;
        }
        var sending = SendAndApply(command, selectAdded);
        lock (_inFlight)
            _inFlight.Add(sending);
        _ = sending.ContinueWith(done =>
        {
            lock (_inFlight)
                _inFlight.Remove(done);
        }, TaskScheduler.Default);
        return sending;
    }

    private async Task SendAndApply(DrawCommand command, bool selectAdded)
    {
        var reply = await _send(command).ConfigureAwait(false);
        // Completed inside the posted action, so waiting on this means the reply has been applied, not just received.
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            try
            {
                ApplyReply(reply, selectAdded);
            }
            finally
            {
                applied.SetResult();
            }
        });
        await applied.Task.ConfigureAwait(false);
    }

    private void ApplyReply(Result<DocumentReply> reply, bool selectAdded)
    {
        if (reply.IsFailure)
        {
            Status = reply.Error.Message;
            _preview = null;
            OnRedraw();
            return;
        }
        Status = "";
        _preview = null;
        Apply(reply.Value);
        if (selectAdded && reply.Value.ItemId is { } added)
            SelectedId = added;
    }

    /// <summary>The box or line a drag in progress is about, or null.</summary>
    private long? GestureItem() => _gesture switch
    {
        Gesture.MovingBox move => move.Box.Id,
        Gesture.ResizingBox resize => resize.Box.Id,
        Gesture.MovingLineEnd end => end.Line.Id,
        _ => null,
    };

    private static bool SameItem(Drawing before, Drawing after, long id) =>
        before.Boxes.TryGetValue(id, out var box) ? after.Boxes.TryGetValue(id, out var now) && now == box
        : before.Lines.TryGetValue(id, out var line) && after.Lines.TryGetValue(id, out var nowLine) && nowLine == line;

    private void FillHistory()
    {
        History.Clear();
        for (var i = 0; i < _history.Entries.Length; i++)
        {
            var entry = _history.Entries[i];
            History.Add(new HistoryItem(entry.Description, SourceWord(entry.Source), i < _history.Applied));
        }
    }

    private static string SourceWord(CommandSource source) => source switch
    {
        CommandSource.Window => "window",
        CommandSource.Cli => "CLI",
        CommandSource.Agent => "agent",
        _ => throw new InvalidOperationException($"Unhandled source {source}."),
    };

    private Colour? ColourOf(long id) =>
        _committed.Boxes.TryGetValue(id, out var box) ? box.Colour
        : _committed.Lines.TryGetValue(id, out var line) ? line.Colour
        : null;

    private void OnRedraw() => Redraw?.Invoke(this, EventArgs.Empty);

    // --- geometry in millimetres ---

    private static Point Snap(double x, double y) => new(Grid.Snap(x), Grid.Snap(y));

    /// <summary>The box under a point, or within a margin of it, choosing the topmost (last drawn) where boxes overlap.</summary>
    private Box? BoxAt(double x, double y, double margin = 0)
    {
        Box? found = null;
        foreach (var box in _committed.Boxes.Values)
        {
            var b = box.Bounds;
            if (x >= b.X - margin && x <= b.Right + margin && y >= b.Y - margin && y <= b.Bottom + margin)
                found = box;
        }
        return found;
    }

    /// <summary>The box a line end at a point attaches to, which includes a point just outside its outline so an end dropped on the edge catches.</summary>
    private Box? AttachableBoxAt(double x, double y) => BoxAt(x, y, GrabPixels / _view.Scale);

    private Line? LineAt(double x, double y, double grab)
    {
        Line? found = null;
        foreach (var line in _committed.Lines.Values)
        {
            if (SegmentDistance(_committed.Resolve(line.Start), _committed.Resolve(line.End), x, y) <= grab)
                found = line;
        }
        return found;
    }

    /// <summary>Where a line end at the pointer will go: onto the box under it if there is one, otherwise free on the grid.</summary>
    private EndAt EndAtFor(double x, double y)
    {
        var at = Snap(x, y);
        return AttachableBoxAt(x, y) is { } box ? new EndAt.OnBox(box.Id, at.X, at.Y) : new EndAt.Free(at.X, at.Y);
    }

    private LineEnd LineEndFor(double x, double y, long lineId)
    {
        var at = Snap(x, y);
        if (AttachableBoxAt(x, y) is { } box)
            return box.Nearest(at);
        var free = LineEnd.Free.Create(at);
        return free.IsSuccess ? free.Value : _committed.Lines[lineId].End;
    }

    /// <summary>Where a line end at the pointer is drawn: on the outline of the box it would attach to, otherwise on the grid.</summary>
    private Point PointFor(double x, double y)
    {
        var at = Snap(x, y);
        if (AttachableBoxAt(x, y) is not { } box)
            return at;
        var nearest = box.Nearest(at);
        return box.On(nearest.Edge, nearest.Offset);
    }

    private static double Distance(Point p, double x, double y) => Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));

    private static double SegmentDistance(Point a, Point b, double x, double y)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / lengthSquared, 0, 1);
        var px = a.X + t * dx - x;
        var py = a.Y + t * dy - y;
        return Math.Sqrt(px * px + py * py);
    }

    /// <summary>What a drag in progress is doing.</summary>
    private abstract record Gesture
    {
        public sealed record None : Gesture;

        /// <summary>Moving a box, holding it at an offset from its top-left corner.</summary>
        public sealed record MovingBox(Box Box, int GrabX, int GrabY) : Gesture;

        /// <summary>Resizing a box by its bottom-right corner.</summary>
        public sealed record ResizingBox(Box Box) : Gesture;

        /// <summary>Drawing a new line from where the drag began.</summary>
        public sealed record DrawingLine(EndAt Start, Point From) : Gesture;

        /// <summary>Dragging one end of a line.</summary>
        public sealed record MovingLineEnd(Line Line, LineEndName Which) : Gesture;
    }
}
