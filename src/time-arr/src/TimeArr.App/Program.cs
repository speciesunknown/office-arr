using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using TimeArr.App.Ipc;
using TimeArr.Commands;
using TimeArr.Core;
using TimeArr.Data;

namespace TimeArr.App;

/// <summary>
/// Startup. If a time-arr is already running, the command line goes to it and this process
/// exits with its answer. Otherwise this process becomes the one instance: it runs the command
/// line itself (if any), listens for others, and opens the window.
/// </summary>
public static class Program
{
    /// <summary>How long to keep trying to reach an instance that is still starting up.</summary>
    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(5);

    [STAThread]
    public static int Main(string[] args)
    {
        // Help and bad arguments need no instance at all.
        var parsed = Cli.Parse(args);
        if (parsed.IsFailure)
            return Print(new IpcReply(2, parsed.Error.Message));
        if (parsed.Value.Help)
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

        var owner = BecomeOwnerOrForward(folder, socketPath.Value, args, out var forwarded);
        if (owner is null)
            return forwarded;

        using var ownerLock = owner;
        var commands = new TimerCommandHandler(new SqliteTimerStorage(dbPath.Value), TimeProvider.System);
        App.Commands = commands;

        if (parsed.Value.Command is not null)
            Print(IpcServer.Handle(args, commands, () => { }));

        var listening = InstanceChannel.Listen(socketPath.Value);
        if (listening.IsFailure)
            Console.Error.WriteLine($"time-arr: {listening.Error.Message} (other time-arr commands won't reach this window)");
        using var server = listening.IsSuccess ? new IpcServer(listening.Value, commands, ShowWindow) : null;
        server?.Start();

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            server?.Dispose();
            TryDelete(socketPath.Value);
        }
    }

    /// <summary>
    /// Returns the lock if this process is now the owner. Otherwise forwards the command line to
    /// the owner, sets <paramref name="exitCode"/> from its reply, and returns null.
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
                exitCode = Print(new IpcReply(1, sent.Error.Message));
                return null;
            }

            var locked = InstanceChannel.TryLock(folder);
            if (locked.IsSuccess)
            {
                exitCode = 0;
                return locked.Value;
            }
            // Someone holds the lock but isn't listening yet: it's starting up. Give it a moment.
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


    private static void ShowWindow() => Dispatcher.UIThread.Post(() =>
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            window.Show();
            window.WindowState = window.WindowState == Avalonia.Controls.WindowState.Minimized
                ? Avalonia.Controls.WindowState.Normal
                : window.WindowState;
            window.Activate();
        }
    });

    private static int Print(IpcReply reply)
    {
        if (reply.Output.Length > 0)
            (reply.ExitCode == 0 ? Console.Out : Console.Error).WriteLine(reply.Output);
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
            // Left behind; the next owner replaces it.
        }
    }

    /// <summary>Also used by the Avalonia designer and by headless tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
