using System.Net.Sockets;
using Avalonia;
using DrawArr.App.Ipc;
using DrawArr.Commands;
using DrawArr.Core;

namespace DrawArr.App;

/// <summary>
/// Startup, where opening drawings becomes the one running instance if none is running and otherwise
/// hands the command line to it, and every other command goes to the running instance and exits.
/// </summary>
public static class Program
{
    /// <summary>How long to keep trying to reach an instance that is still starting up.</summary>
    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(5);

    /// <summary>How long the CLI waits for the running instance's reply, a little longer than the broker allows itself.</summary>
    public static readonly TimeSpan ReplyTimeout = Broker.Timeout + TimeSpan.FromSeconds(5);

    [STAThread]
    public static int Main(string[] args)
    {
        // Help and bad arguments need no instance.
        var parsed = Cli.Parse(args);
        if (parsed.IsFailure)
            return Print(new IpcReply(2, parsed.Error.Message));
        if (parsed.Value.Request is CliRequest.Help)
            return Print(new IpcReply(0, Cli.Usage));

        var folder = DataFolder();
        if (folder.IsFailure)
            return Print(new IpcReply(1, folder.Error.Message));
        var prepared = InstanceChannel.PrepareFolder(folder.Value);
        if (prepared.IsFailure)
            return Print(new IpcReply(1, prepared.Error.Message));
        var socketPath = InstanceChannel.SocketPath(folder.Value);
        if (socketPath.IsFailure)
            return Print(new IpcReply(1, socketPath.Error.Message));
        var here = Environment.CurrentDirectory;

        if (parsed.Value.Request is not (CliRequest.NewDrawing or CliRequest.Open))
            return Forward(args, here, folder.Value, socketPath.Value);
        return OpenWindows(args, here, folder.Value, socketPath.Value, parsed.Value.Request);
    }

    /// <summary>Sends a command to the running instance, which is the only place drawings are edited, waiting for one that is still starting.</summary>
    private static int Forward(string[] args, string here, string folder, string socketPath)
    {
        var deadline = DateTime.UtcNow + StartupWait;
        while (true)
        {
            var sent = InstanceChannel.Send(socketPath, args, here, ReplyTimeout);
            if (sent.IsSuccess)
                return Print(sent.Value);
            if (sent.Error.Code != ErrorCode.NotFound)
                return Print(new IpcReply(1, $"Lost contact with the running draw-arr ({sent.Error.Message})."));

            // Nobody listening and nobody holding the lock means nothing is running; a lock holder is still starting, so give it a moment.
            var locked = InstanceChannel.TryLock(folder);
            if (locked.IsSuccess)
            {
                locked.Value.Dispose();
                return Print(new IpcReply(1, "draw-arr isn't running; open the drawing first with: draw-arr <file>"));
            }
            if (locked.Error.Code != ErrorCode.Conflict || DateTime.UtcNow > deadline)
            {
                return Print(new IpcReply(1, locked.Error.Code == ErrorCode.Conflict
                    ? "Another draw-arr holds the lock but isn't answering."
                    : locked.Error.Message));
            }
            Thread.Sleep(100);
        }
    }

    /// <summary>Hands the files to the running instance, or becomes the instance and opens them itself.</summary>
    private static int OpenWindows(string[] args, string here, string folder, string socketPath, CliRequest request)
    {
        using var owner = BecomeOwnerOrForward(folder, socketPath, args, here, out var forwarded);
        if (owner is null)
            return forwarded;

        var listening = InstanceChannel.Listen(socketPath);
        if (listening.IsFailure)
            Console.Error.WriteLine(Printable.Text($"draw-arr: {listening.Error.Message} (other draw-arr commands won't reach this instance)"));
        App.Listener = listening.IsSuccess ? listening.Value : null;
        App.StartupFiles = request is CliRequest.Open open ? [.. open.Files.Select(f => Path.GetFullPath(f, here))] : [];
        try
        {
            // draw-arr's own arguments are not for Avalonia.
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
        }
        finally
        {
            TryDelete(socketPath);
        }
    }

    /// <summary>Returns the lock if this process is now the owner, or else forwards the command line to the owner and returns null with its exit code.</summary>
    private static FileStream? BecomeOwnerOrForward(string folder, string socketPath, string[] args, string here, out int exitCode)
    {
        var deadline = DateTime.UtcNow + StartupWait;
        while (true)
        {
            var sent = InstanceChannel.Send(socketPath, args, here, ReplyTimeout);
            if (sent.IsSuccess)
            {
                exitCode = Print(sent.Value);
                return null;
            }
            if (sent.Error.Code != ErrorCode.NotFound)
            {
                exitCode = Print(new IpcReply(1, $"Lost contact with the running draw-arr ({sent.Error.Message})."));
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
                    ? "Another draw-arr holds the lock but isn't answering."
                    : locked.Error.Message));
                return null;
            }
            Thread.Sleep(100);
        }
    }

    /// <summary>The user's own draw-arr folder, which holds the socket, the lock and the autosave folder.</summary>
    private static Result<string> DataFolder()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        return Path.IsPathFullyQualified(local)
            ? Path.Combine(local, "draw-arr")
            : Result<string>.Fail(ErrorCode.FileUnavailable, "There is no local data folder for draw-arr to use.");
    }

    private static int Print(IpcReply reply)
    {
        if (reply.Output.Length > 0)
            (reply.ExitCode == 0 ? Console.Out : Console.Error).WriteLine(Printable.Text(reply.Output));
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
