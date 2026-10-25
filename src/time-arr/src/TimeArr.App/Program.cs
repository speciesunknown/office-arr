using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using TimeArr.App.Ipc;
using TimeArr.Commands;
using TimeArr.Core;
using TimeArr.Data;

namespace TimeArr.App;

/// <summary>
/// Startup, where a command runs and exits (through the open window if there is one) and only no
/// arguments or <c>show</c> opens the window, becoming the one instance if none is running.
/// </summary>
public static class Program
{
    /// <summary>How long to keep trying to reach an instance that is still starting up.</summary>
    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(5);

    [STAThread]
    public static int Main(string[] args)
    {
        // Help and bad arguments need no instance and no database.
        var parsed = Cli.Parse(args);
        if (parsed.IsFailure)
            return Print(new IpcReply(2, parsed.Error.Message));
        if (parsed.Value is CliRequest.Help)
            return Print(new IpcReply(0, Cli.Usage));

        var dbPath = SqliteTimerRepository.DefaultPath();
        if (dbPath.IsFailure)
            return Print(new IpcReply(1, dbPath.Error.Message));
        var folder = Path.GetDirectoryName(dbPath.Value)!;

        var prepared = InstanceChannel.PrepareFolder(folder);
        if (prepared.IsFailure)
            return Print(new IpcReply(1, prepared.Error.Message));
        var socketPath = InstanceChannel.SocketPath(folder);
        if (socketPath.IsFailure)
            return Print(new IpcReply(1, socketPath.Error.Message));
        CrashLog.Install(folder);

        return parsed.Value is CliRequest.Run
            ? RunCommand(args, socketPath.Value, dbPath.Value)
            : ShowWindow(args, folder, socketPath.Value, dbPath.Value);
    }

    /// <summary>Runs a command through the open window if there is one, or straight against the database if not, and exits.</summary>
    private static int RunCommand(string[] args, string socketPath, string dbPath)
    {
        var sent = InstanceChannel.Send(socketPath, args, IpcServer.IoTimeout * 2);
        if (sent.IsSuccess)
            return Print(sent.Value);
        if (sent.Error.Code != ErrorCode.NotFound)
            return Print(new IpcReply(1, LostContact(sent.Error)));

        // The database copes with this process and a window starting meanwhile, and the window re-reads every second.
        var commands = new TimerCommandHandler(new SqliteTimerStorage(dbPath), TimeProvider.System);
        return Print(IpcServer.Handle(args, commands, () => { }));
    }

    /// <summary>Brings the running window forward, or becomes the instance and opens the window.</summary>
    private static int ShowWindow(string[] args, string folder, string socketPath, string dbPath)
    {
        using var owner = BecomeOwnerOrForward(folder, socketPath, args, out var forwarded);
        if (owner is null)
            return forwarded;

        var commands = new TimerCommandHandler(new SqliteTimerStorage(dbPath), TimeProvider.System);
        App.Commands = commands;

        var listening = InstanceChannel.Listen(socketPath);
        if (listening.IsFailure)
            Console.Error.WriteLine(Cli.Printable($"time-arr: {listening.Error.Message} (other time-arr commands won't reach this window)"));
        using var server = listening.IsSuccess ? new IpcServer(listening.Value, commands, RaiseWindow) : null;
        server?.Start();

        try
        {
            // time-arr's own arguments are not for Avalonia.
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
        }
        finally
        {
            server?.Dispose();
            TryDelete(socketPath);
        }
    }

    /// <summary>
    /// Returns the lock if this process is now the owner, or else forwards the command line to the
    /// owner, sets <paramref name="exitCode"/> from its reply and returns null.
    /// </summary>
    private static FileStream? BecomeOwnerOrForward(string folder, string socketPath, string[] args, out int exitCode)
    {
        var deadline = DateTime.UtcNow + StartupWait;
        while (true)
        {
            var sent = InstanceChannel.Send(socketPath, args, IpcServer.IoTimeout * 2);
            if (sent.IsSuccess)
            {
                exitCode = Print(sent.Value);
                return null;
            }
            if (sent.Error.Code != ErrorCode.NotFound)
            {
                exitCode = Print(new IpcReply(1, LostContact(sent.Error)));
                return null;
            }

            var locked = InstanceChannel.TryLock(folder);
            if (locked.IsSuccess)
            {
                exitCode = 0;
                return locked.Value;
            }
            // A lock holder that isn't listening yet is starting up, so give it a moment.
            if (locked.Error.Code != ErrorCode.Conflict || DateTime.UtcNow > deadline)
            {
                exitCode = Print(new IpcReply(1, locked.Error.Code == ErrorCode.Conflict
                    ? "Another time-arr holds the lock but isn't answering."
                    : locked.Error.Message));
                return null;
            }
            Thread.Sleep(100);
        }
    }

    /// <summary>The message for losing contact with the running instance, which points at the crash log in case it stopped.</summary>
    private static string LostContact(Error error) =>
        $"Lost contact with the running time-arr ({error.Message}). If it stopped unexpectedly, the details are in {CrashLog.FilePath}.";

    private static void RaiseWindow() => Dispatcher.UIThread.Post(() =>
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            window.Show();
            if (window.WindowState == Avalonia.Controls.WindowState.Minimized)
                window.WindowState = Avalonia.Controls.WindowState.Normal;
            window.Activate();
        }
    });

    private static int Print(IpcReply reply)
    {
        if (reply.Output.Length > 0)
            (reply.ExitCode == 0 ? Console.Out : Console.Error).WriteLine(Cli.Printable(reply.Output));
        return reply.ExitCode;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A socket file left behind is replaced by the next owner.
        }
    }

    /// <summary>The Avalonia setup, also used by the designer and by headless tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
