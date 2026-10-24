using System.Collections.ObjectModel;
using System.Windows.Input;
using TimeArr.Commands;
using TimeArr.Core;

namespace TimeArr.ViewModels;

/// <summary>
/// The main window: the list of timers, the "add timer" box, and any problem to show.
/// Everything goes through <see cref="ITimerCommands"/>; nothing here knows about SQL or controls.
/// </summary>
/// <remarks>
/// Not thread-safe: call it from the UI thread. The view calls <see cref="Tick"/> a few times a
/// second; that keeps the clocks moving and, every <see cref="RefreshEveryTicks"/> ticks, re-reads
/// the timers so changes made from the CLI show up.
/// </remarks>
public sealed class MainViewModel : ViewModelBase
{
    /// <summary>Ticks between re-reading the timers (at 4 ticks a second: once a second).</summary>
    public const int RefreshEveryTicks = 4;

    private readonly ITimerCommands _commands;
    private readonly TimeProvider _clock;
    private string _newTimerName = "";
    private string? _message;
    private int _ticks;

    public MainViewModel(ITimerCommands commands, TimeProvider clock)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        AddCommand = new RelayCommand(Add);
        DismissMessageCommand = new RelayCommand(() => Message = null);
        Refresh();
    }

    public ObservableCollection<TimerViewModel> Timers { get; } = [];

    public bool IsEmpty => Timers.Count == 0;

    public string NewTimerName
    {
        get => _newTimerName;
        set => Set(ref _newTimerName, value);
    }

    /// <summary>
    /// What went wrong, for the user: the last command's error, or a storage problem
    /// (database damaged, not time-arr's, from a newer version, can't be opened). Null when fine.
    /// </summary>
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

    public ICommand AddCommand { get; }
    public ICommand DismissMessageCommand { get; }

    /// <summary>Moves the clocks on; re-reads the timers every <see cref="RefreshEveryTicks"/> calls.</summary>
    public void Tick()
    {
        if (++_ticks % RefreshEveryTicks == 0)
        {
            Refresh();
            return;
        }
        var now = _clock.GetUtcNow();
        foreach (var timer in Timers)
            timer.UpdateElapsed(now);
    }

    /// <summary>Re-reads all timers. Keeps existing rows (matched by id) so focus and edits survive.</summary>
    public void Refresh()
    {
        var reply = _commands.Execute(new TimerCommand.List());
        if (reply.IsFailure)
        {
            Show(reply.Error);
            return;
        }

        // A storage problem that just went away (file fixed, disk freed) clears its message.
        if (_storageMessageShown)
        {
            _storageMessageShown = false;
            Message = null;
        }
        Merge(reply.Value.Timers, reply.Value.NowUtc);
    }

    private bool _storageMessageShown;

    /// <summary>Sends a command, shows its error if it fails, and refreshes. Returns whether it worked.</summary>
    internal bool Run(TimerCommand command)
    {
        var reply = _commands.Execute(command);
        if (reply.IsFailure)
        {
            Show(reply.Error);
            return false;
        }
        Message = null;
        _storageMessageShown = false;
        Refresh();
        return true;
    }

    private void Add()
    {
        if (Run(new TimerCommand.Add(NewTimerName)))
            NewTimerName = "";
    }

    private void Show(Error error)
    {
        _storageMessageShown = IsStorage(error.Code);
        Message = Describe(error);
    }

    private static bool IsStorage(ErrorCode code) =>
        code is ErrorCode.StorageBusy or ErrorCode.StorageCorrupt or ErrorCode.StorageForeign
            or ErrorCode.StorageTooNew or ErrorCode.StorageUnavailable;

    /// <summary>What to tell the user. Storage problems get a plain sentence first; the detail follows.</summary>
    public static string Describe(Error error) => error.Code switch
    {
        ErrorCode.StorageCorrupt => $"The timer database is damaged, so time-arr can't use it. Nothing has been changed. ({error.Message})",
        ErrorCode.StorageForeign => $"The file where time-arr keeps its timers isn't a time-arr database, so time-arr is leaving it alone. ({error.Message})",
        ErrorCode.StorageTooNew => $"The timer database was written by a newer time-arr. Please update time-arr. ({error.Message})",
        ErrorCode.StorageUnavailable => $"time-arr can't open its timer database. ({error.Message})",
        ErrorCode.StorageBusy => "The timer database is busy. Try again in a moment.",
        _ => error.Message,
    };

    private void Merge(IReadOnlyList<TimerEntry> entries, DateTimeOffset now)
    {
        var wasEmpty = IsEmpty;

        // Remove rows whose timer is gone.
        var ids = entries.Select(e => e.Id).ToHashSet();
        for (var i = Timers.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Timers[i].Id))
                Timers.RemoveAt(i);
        }

        // Update or insert, keeping the storage order (creation order).
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var existing = FindIndex(entry.Id);
            if (existing < 0)
            {
                Timers.Insert(i, new TimerViewModel(this, entry, now));
                continue;
            }
            if (existing != i)
                Timers.Move(existing, i);
            Timers[i].Update(entry, now);
        }

        if (wasEmpty != IsEmpty)
            Raise(nameof(IsEmpty));
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
