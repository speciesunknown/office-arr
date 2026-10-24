using System.Windows.Input;
using TimeArr.Commands;
using TimeArr.Core;

namespace TimeArr.ViewModels;

/// <summary>One timer row: its name, the time on it, and its buttons.</summary>
public sealed class TimerViewModel : ViewModelBase
{
    private readonly MainViewModel _owner;
    private TimerEntry _entry;
    private string _elapsedText = "";
    private string _editName;
    private bool _isEditing;
    private bool _isMini;
    private bool _isShown = true;

    internal TimerViewModel(MainViewModel owner, TimerEntry entry, DateTimeOffset nowUtc)
    {
        _owner = owner;
        _entry = entry;
        _editName = entry.Name;
        ToggleCommand = new RelayCommand(() => _owner.Run(IsRunning ? new TimerCommand.Pause(Ref) : new TimerCommand.Start(Ref)));
        ResetCommand = new RelayCommand(() => _owner.Run(new TimerCommand.Reset(Ref)));
        DeleteCommand = new RelayCommand(() => _owner.Run(new TimerCommand.Delete(Ref)));
        BeginRenameCommand = new RelayCommand(() => { EditName = Name; IsEditing = true; });
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(() => IsEditing = false);
        UpdateElapsed(nowUtc);
    }

    public long Id => _entry.Id;
    public string Name => _entry.Name;
    public bool IsRunning => _entry.State.IsRunning;

    /// <summary>Paused: not running, but with time on it. (At zero it is stopped.)</summary>
    public bool IsPaused => !IsRunning && _entry.State.Accumulated > TimeSpan.Zero;

    /// <summary>"Pause" while running, "Resume" while paused, "Start" when stopped at zero.</summary>
    public string ToggleText => IsRunning ? "Pause" : IsPaused ? "Resume" : "Start";

    /// <summary>The window is in mini mode: only Pause/Resume is offered.</summary>
    public bool IsMini
    {
        get => _isMini;
        private set => Set(ref _isMini, value);
    }

    /// <summary>Whether the row is on screen. Always in normal mode; in mini mode, see <see cref="MainViewModel.IsMiniMode"/>.</summary>
    public bool IsShown
    {
        get => _isShown;
        private set => Set(ref _isShown, value);
    }

    internal void SetMode(bool mini, bool shown)
    {
        IsMini = mini;
        IsShown = shown;
        if (mini)
            IsEditing = false;
    }

    /// <summary>Time on the clock, e.g. <c>0:04:07</c> or <c>2d 03:15:00</c>.</summary>
    public string ElapsedText
    {
        get => _elapsedText;
        private set => Set(ref _elapsedText, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        set => Set(ref _isEditing, value);
    }

    /// <summary>The name in the rename box while <see cref="IsEditing"/>.</summary>
    public string EditName
    {
        get => _editName;
        set => Set(ref _editName, value);
    }

    public ICommand ToggleCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand BeginRenameCommand { get; }
    public ICommand CommitRenameCommand { get; }
    public ICommand CancelRenameCommand { get; }

    // By id, so a rename elsewhere (e.g. from the CLI) can't make a button hit another timer.
    private TimerRef Ref => new TimerRef.ById(Id);

    private void CommitRename()
    {
        if (EditName == Name)
        {
            IsEditing = false;
            return;
        }
        if (_owner.Run(new TimerCommand.Rename(Ref, EditName)))
            IsEditing = false;
    }

    internal void Update(TimerEntry entry, DateTimeOffset nowUtc)
    {
        var old = _entry;
        _entry = entry;
        if (old.Name != entry.Name)
            Raise(nameof(Name));
        if (old.State.IsRunning != entry.State.IsRunning)
            Raise(nameof(IsRunning));
        if (old.State.IsRunning != entry.State.IsRunning || old.State.Accumulated != entry.State.Accumulated)
        {
            Raise(nameof(IsPaused));
            Raise(nameof(ToggleText));
        }
        UpdateElapsed(nowUtc);
    }

    internal void UpdateElapsed(DateTimeOffset nowUtc) => ElapsedText = Format(_entry.State.ElapsedAt(nowUtc));

    /// <summary>Same format as the CLI: see <see cref="ElapsedFormat.Format"/>.</summary>
    public static string Format(TimeSpan elapsed) => ElapsedFormat.Format(elapsed);
}
