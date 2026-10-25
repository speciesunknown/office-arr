using System.Collections.ObjectModel;
using System.Windows.Input;
using TimeArr.Commands;
using TimeArr.Core;

namespace TimeArr.ViewModels;

/// <summary>Which layout the window is in.</summary>
public enum WindowMode
{
    /// <summary>All timers with every button, plus the add box.</summary>
    Normal = 1,
    /// <summary>A small borderless window with the running and paused timers and their Pause/Resume buttons.</summary>
    Mini = 2,
}

/// <summary>Whether the last look at the database worked.</summary>
public enum StorageStatus
{
    Available = 1,
    Unavailable = 2,
}

/// <summary>A question waiting for a yes or no before a command runs, such as "Delete 'work'?".</summary>
public sealed record Confirmation(string Question, string ConfirmText, TimerCommand Command);

/// <summary>Runs a command's work somewhere and hands back its result, inline in tests and on a worker thread in the app.</summary>
public delegate Task<Result<CommandReply>> CommandRunner(Func<Result<CommandReply>> work);

/// <summary>The main window, with the timer list, the add box and any problem to show, talking only to <see cref="ITimerCommands"/>.</summary>
/// <remarks>Call it from the UI thread, with the view calling <see cref="Tick"/> four times a second to move the clocks and re-read the timers.</remarks>
public sealed class MainViewModel : ViewModelBase
{
    /// <summary>Ticks between re-reading the timers, which at four ticks a second is once a second.</summary>
    public const int RefreshEveryTicks = 4;

    /// <summary>Ticks between re-reading while the database has a problem, which is every ten seconds.</summary>
    public const int RefreshEveryTicksWhileUnavailable = 40;

    /// <summary>A Reset only asks first when the timer has more than this on it.</summary>
    public static readonly TimeSpan ResetConfirmAbove = TimeSpan.FromMinutes(1);

    private enum Activity { Idle = 1, Refreshing = 2, RefreshingThenAgain = 3 }

    private readonly ITimerCommands _commands;
    private readonly TimeProvider _clock;
    private readonly CommandRunner _run;
    private readonly RelayCommand _add;
    private string _newTimerName = "";
    private string? _message;
    private int _ticks;
    private WindowMode _mode = WindowMode.Normal;
    private StorageStatus _storage = StorageStatus.Available;
    private Activity _activity = Activity.Idle;
    private Task _refreshing = Task.CompletedTask;
    private Confirmation? _confirmation;

    /// <param name="run">Where command work runs, inline when not given, or <see cref="RunInBackground"/> to keep the UI thread free.</param>
    public MainViewModel(ITimerCommands commands, TimeProvider clock, CommandRunner? run = null)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _run = run ?? RunInline;
        _add = new RelayCommand(Add, () => !string.IsNullOrWhiteSpace(NewTimerName));
        DismissMessageCommand = new RelayCommand(() => Message = null);
        ToggleMiniModeCommand = new RelayCommand(() => Mode = Mode == WindowMode.Mini ? WindowMode.Normal : WindowMode.Mini);
        ConfirmCommand = new RelayCommand(Confirm);
        CancelConfirmationCommand = new RelayCommand(() => Confirmation = null);
        _ = Refresh();
    }

    /// <summary>Runs command work on a thread-pool thread, so a database wait never freezes the window.</summary>
    public static Task<Result<CommandReply>> RunInBackground(Func<Result<CommandReply>> work) => Task.Run(work);

    private static Task<Result<CommandReply>> RunInline(Func<Result<CommandReply>> work) => Task.FromResult(work());

    public ObservableCollection<TimerViewModel> Timers { get; } = [];

    public bool IsEmpty => Timers.Count == 0;

    /// <summary>Whether to say "No timers", which is only true when the database was read and really has none.</summary>
    public bool ShowEmptyHint => IsEmpty && Storage == StorageStatus.Available && Mode == WindowMode.Normal;

    public string NewTimerName
    {
        get => _newTimerName;
        set
        {
            if (Set(ref _newTimerName, value))
                _add.RaiseCanExecuteChanged();
        }
    }

    /// <summary>What went wrong for the user to read, either the last command's error or a database problem, or null when all is well.</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value))
                Raise(nameof(HasMessage));
        }
    }

    public bool HasMessage => Message is not null;

    /// <summary>Whether the window shows everything or just the running and paused timers.</summary>
    public WindowMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value))
                return;
            Raise(nameof(IsMiniMode));
            Confirmation = null;
            foreach (var timer in Timers)
                timer.SetWindowMode(value);
            RaiseListState();
        }
    }

    /// <summary>View helper for <see cref="Mode"/> being <see cref="WindowMode.Mini"/>.</summary>
    public bool IsMiniMode => Mode == WindowMode.Mini;

    /// <summary>Mini mode with nothing to show, because every timer is stopped at zero.</summary>
    public bool IsMiniEmpty => IsMiniMode && Storage == StorageStatus.Available && !Timers.Any(t => t.IsShown);

    /// <summary>Whether the last look at the database worked, with rows greyed out and frozen while it didn't.</summary>
    public StorageStatus Storage
    {
        get => _storage;
        private set
        {
            if (!Set(ref _storage, value))
                return;
            foreach (var timer in Timers)
                timer.SetStorage(value);
            RaiseListState();
        }
    }

    /// <summary>The question waiting for an answer before a Delete or Reset runs, or null.</summary>
    public Confirmation? Confirmation
    {
        get => _confirmation;
        private set
        {
            if (Set(ref _confirmation, value))
                Raise(nameof(HasConfirmation));
        }
    }

    public bool HasConfirmation => Confirmation is not null;

    public ICommand AddCommand => _add;
    public ICommand DismissMessageCommand { get; }
    public ICommand ToggleMiniModeCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand CancelConfirmationCommand { get; }

    /// <summary>Moves the clocks on, and re-reads the timers every few ticks (less often while the database has a problem).</summary>
    public void Tick()
    {
        var every = Storage == StorageStatus.Available ? RefreshEveryTicks : RefreshEveryTicksWhileUnavailable;
        if (++_ticks % every == 0)
        {
            _ = Refresh();
            return;
        }
        // Frozen while the database can't be read, rather than ticking on from stale state.
        if (Storage == StorageStatus.Unavailable)
            return;
        var now = _clock.GetUtcNow();
        foreach (var timer in Timers)
            timer.UpdateElapsed(now);
    }

    /// <summary>Re-reads all timers, keeping existing rows (matched by id) so focus and edits survive, and joining a read already under way with one more read after it.</summary>
    /// <returns>A task that completes once the timers on screen are at least as new as this call.</returns>
    public Task Refresh()
    {
        if (_activity != Activity.Idle)
        {
            _activity = Activity.RefreshingThenAgain;
            return _refreshing;
        }
        _activity = Activity.Refreshing;
        _refreshing = RefreshLoop();
        return _refreshing;
    }

    private async Task RefreshLoop()
    {
        try
        {
            do
            {
                _activity = Activity.Refreshing;
                await RefreshOnce();
            }
            while (_activity == Activity.RefreshingThenAgain);
        }
        finally
        {
            _activity = Activity.Idle;
        }
    }

    private async Task RefreshOnce()
    {
        var reply = await _run(() => _commands.Execute(new TimerCommand.List()));
        if (reply.IsFailure)
        {
            Show(reply.Error);
            return;
        }
        if (Storage == StorageStatus.Unavailable)
        {
            // The database problem went away, so its message goes too.
            Storage = StorageStatus.Available;
            Message = null;
        }
        Merge(reply.Value.Timers, reply.Value.NowUtc);
    }

    /// <summary>Sends a command, shows its error if it fails, and refreshes, returning whether it worked.</summary>
    internal async Task<bool> Send(TimerCommand command)
    {
        var reply = await _run(() => _commands.Execute(command));
        if (reply.IsFailure)
        {
            Show(reply.Error);
            return false;
        }
        Message = null;
        Storage = StorageStatus.Available;
        await Refresh();
        return true;
    }

    internal void ConfirmDelete(TimerViewModel timer, TimerCommand delete) =>
        Confirmation = new Confirmation($"Delete '{timer.Name}'? Its time can't be brought back.", "Delete", delete);

    internal void ConfirmReset(TimerViewModel timer, TimerCommand reset)
    {
        var elapsed = timer.ElapsedAt(_clock.GetUtcNow());
        if (elapsed <= ResetConfirmAbove)
        {
            _ = Send(reset);
            return;
        }
        Confirmation = new Confirmation(
            $"Reset '{timer.Name}' to zero? It has {ElapsedFormat.Format(elapsed)} on it.", "Reset", reset);
    }

    private void Confirm()
    {
        if (Confirmation is not { } pending)
            return;
        Confirmation = null;
        _ = Send(pending.Command);
    }

    private async void Add()
    {
        if (await Send(new TimerCommand.Add(NewTimerName)))
            NewTimerName = "";
    }

    private void Show(Error error)
    {
        if (IsStorage(error.Code))
            Storage = StorageStatus.Unavailable;
        Message = Describe(error);
    }

    private static bool IsStorage(ErrorCode code) =>
        code is ErrorCode.StorageCorrupt or ErrorCode.StorageForeign or ErrorCode.StorageTooNew or ErrorCode.StorageUnavailable;

    /// <summary>What to tell the user, with a plain sentence first for database problems and the detail after.</summary>
    public static string Describe(Error error) => error.Code switch
    {
        ErrorCode.StorageCorrupt => $"The timer database is damaged, so time-arr can't use it. Nothing has been changed. ({error.Message})",
        ErrorCode.StorageForeign => $"The file where time-arr keeps its timers isn't a time-arr database, so time-arr is leaving it alone. ({error.Message})",
        ErrorCode.StorageTooNew => $"The timer database was written by a newer time-arr. Please update time-arr. ({error.Message})",
        ErrorCode.StorageUnavailable => $"time-arr can't open its timer database. ({error.Message})",
        ErrorCode.StorageBusy => "The timer database is busy. Try again in a moment.",
        _ => error.Message,
    };

    private void RaiseListState()
    {
        Raise(nameof(IsEmpty));
        Raise(nameof(ShowEmptyHint));
        Raise(nameof(IsMiniEmpty));
    }

    private void Merge(IReadOnlyList<TimerEntry> entries, DateTimeOffset now)
    {
        // Remove rows whose timer is gone.
        var ids = entries.Select(e => e.Id).ToHashSet();
        for (var i = Timers.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Timers[i].Id))
                Timers.RemoveAt(i);
        }

        // Update or insert, keeping the storage order, which is creation order.
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var existing = FindIndex(entry.Id);
            if (existing < 0)
            {
                var row = new TimerViewModel(this, entry, now);
                row.SetWindowMode(Mode);
                row.SetStorage(Storage);
                Timers.Insert(i, row);
                continue;
            }
            if (existing != i)
                Timers.Move(existing, i);
            Timers[i].Update(entry, now);
        }

        RaiseListState();
    }

    private int FindIndex(long id)
    {
        for (var i = 0; i < Timers.Count; i++)
        {
            if (Timers[i].Id == id)
                return i;
        }
        return -1;
    }
}
