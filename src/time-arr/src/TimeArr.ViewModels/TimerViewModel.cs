using System.Windows.Input;
using TimeArr.Commands;
using TimeArr.Core;

namespace TimeArr.ViewModels;

/// <summary>Whether a timer row shows its name or a box for renaming it.</summary>
public enum NameMode
{
    Showing = 1,
    Editing = 2,
}

/// <summary>One timer row: its name, the time on it, and its buttons.</summary>
public sealed class TimerViewModel : ViewModelBase
{
    private readonly MainViewModel _owner;
    private TimerEntry _entry;
    private string _elapsedText = "";
    private string _editName;
    private NameMode _nameMode = NameMode.Showing;
    private WindowMode _windowMode = WindowMode.Normal;
    private StorageStatus _storage = StorageStatus.Available;

    internal TimerViewModel(MainViewModel owner, TimerEntry entry, DateTimeOffset nowUtc)
    {
        _owner = owner;
        _entry = entry;
        _editName = entry.Name;
        ToggleCommand = new RelayCommand(() => MainViewModel.Observe(_owner.Send(IsRunning ? new TimerCommand.Pause(Ref) : new TimerCommand.Start(Ref))));
        ResetCommand = new RelayCommand(() => _owner.ConfirmReset(this, new TimerCommand.Reset(Ref)));
        DeleteCommand = new RelayCommand(() => _owner.ConfirmDelete(this, new TimerCommand.Delete(Ref)));
        BeginRenameCommand = new RelayCommand(BeginRename, () => _windowMode == WindowMode.Normal);
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(() => NameMode = NameMode.Showing);
        UpdateElapsed(nowUtc);
    }

    public long Id => _entry.Id;
    public string Name => _entry.Name;

    /// <summary>Whether the timer is stopped, running or paused.</summary>
    public TimerPhase Phase => _entry.State.Phase;

    /// <summary>View helper for <see cref="Phase"/> being <see cref="TimerPhase.Running"/>.</summary>
    public bool IsRunning => Phase == TimerPhase.Running;

    /// <summary>View helper for <see cref="Phase"/> being <see cref="TimerPhase.Paused"/>.</summary>
    public bool IsPaused => Phase == TimerPhase.Paused;

    /// <summary>The label of the start/pause button for the current <see cref="Phase"/>.</summary>
    public string ToggleText => Phase switch
    {
        TimerPhase.Running => "Pause",
        TimerPhase.Paused => "Resume",
        TimerPhase.Stopped => "Start",
        _ => throw new InvalidOperationException($"Unhandled timer phase {Phase}."),
    };

    /// <summary>Time on the clock, such as <c>0:04:07</c> or <c>2d 03:15:00</c>.</summary>
    public string ElapsedText
    {
        get => _elapsedText;
        private set => Set(ref _elapsedText, value);
    }

    /// <summary>Whether the row shows its name or the rename box.</summary>
    public NameMode NameMode
    {
        get => _nameMode;
        private set
        {
            if (Set(ref _nameMode, value))
                Raise(nameof(IsEditing));
        }
    }

    /// <summary>View helper for <see cref="NameMode"/> being <see cref="ViewModels.NameMode.Editing"/>.</summary>
    public bool IsEditing => NameMode == NameMode.Editing;

    /// <summary>The name in the rename box while editing.</summary>
    public string EditName
    {
        get => _editName;
        set => Set(ref _editName, value);
    }

    /// <summary>View helper for the window being in mini mode, where only Pause/Resume is offered.</summary>
    public bool IsMini => _windowMode == WindowMode.Mini;

    /// <summary>Whether the row is on screen, which in mini mode means it isn't stopped.</summary>
    public bool IsShown => _windowMode == WindowMode.Normal || Phase != TimerPhase.Stopped;

    /// <summary>Whether the row's buttons work, which they don't while the database can't be used.</summary>
    public bool IsEnabled => _storage == StorageStatus.Available;

    public ICommand ToggleCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand DeleteCommand { get; }
    public RelayCommand BeginRenameCommand { get; }
    public ICommand CommitRenameCommand { get; }
    public ICommand CancelRenameCommand { get; }

    // By id, so a rename elsewhere (from the CLI, say) can't make a button hit another timer.
    private TimerRef Ref => new TimerRef.ById(Id);

    private void BeginRename()
    {
        EditName = Name;
        NameMode = NameMode.Editing;
    }

    private void CommitRename() => MainViewModel.Observe(CommitRenameAsync());

    private async Task CommitRenameAsync()
    {
        if (EditName == Name)
        {
            NameMode = NameMode.Showing;
            return;
        }
        if (await _owner.Send(new TimerCommand.Rename(Ref, EditName)))
            NameMode = NameMode.Showing;
    }

    internal void SetWindowMode(WindowMode mode)
    {
        if (_windowMode == mode)
            return;
        _windowMode = mode;
        if (mode == WindowMode.Mini)
            NameMode = NameMode.Showing;
        Raise(nameof(IsMini));
        Raise(nameof(IsShown));
        BeginRenameCommand.RaiseCanExecuteChanged();
    }

    internal void SetStorage(StorageStatus storage)
    {
        if (_storage == storage)
            return;
        _storage = storage;
        Raise(nameof(IsEnabled));
    }

    internal void Update(TimerEntry entry, DateTimeOffset nowUtc)
    {
        var old = _entry;
        _entry = entry;
        if (old.Name != entry.Name)
            Raise(nameof(Name));
        if (old.State.Phase != entry.State.Phase)
        {
            Raise(nameof(Phase));
            Raise(nameof(IsRunning));
            Raise(nameof(IsPaused));
            Raise(nameof(ToggleText));
            Raise(nameof(IsShown));
        }
        UpdateElapsed(nowUtc);
    }

    internal TimeSpan ElapsedAt(DateTimeOffset nowUtc) => _entry.State.ElapsedAt(nowUtc);

    internal void UpdateElapsed(DateTimeOffset nowUtc) => ElapsedText = Format(ElapsedAt(nowUtc));

    /// <summary>Formats elapsed time the same way as the CLI, using <see cref="ElapsedFormat.Format"/>.</summary>
    public static string Format(TimeSpan elapsed) => ElapsedFormat.Format(elapsed);
}
