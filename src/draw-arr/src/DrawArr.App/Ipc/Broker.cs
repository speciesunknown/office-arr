using System.Globalization;
using System.Text;
using DrawArr.Commands;
using DrawArr.Core;
using DrawArr.Files;

namespace DrawArr.App.Ipc;

/// <summary>Runs command lines from the CLI and agents against the open drawings, through the same command queue as the windows.</summary>
public sealed class Broker
{
    /// <summary>How long one command line may take, including opening its file.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly AppHost _host;
    private readonly Action<DocumentSession> _raise;

    /// <param name="host">The running instance's documents.</param>
    /// <param name="raise">Brings a session's window to the front, from any thread.</param>
    public Broker(AppHost host, Action<DocumentSession> raise)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _raise = raise ?? throw new ArgumentNullException(nameof(raise));
    }

    /// <summary>Handles one command line typed in a folder, the same way whether it came over IPC or from this process.</summary>
    public IpcReply Handle(string[] args, string workingDirectory)
    {
        var parsed = Cli.Parse(args);
        if (parsed.IsFailure)
            return new IpcReply(2, parsed.Error.Message);
        // Past the deadline nothing new is started, and a command already sent is waited for, so "took too long" always means nothing changed.
        using var deadline = new CancellationTokenSource(Timeout);
        return Serve(parsed.Value, workingDirectory, deadline.Token).GetAwaiter().GetResult();
    }

    private static readonly IpcReply TookTooLong = new(1, "draw-arr took too long to answer, so nothing was changed.");

    private async Task<IpcReply> Serve(CliCall call, string folder, CancellationToken deadline)
    {
        switch (call.Request)
        {
            case CliRequest.Help:
                return new IpcReply(0, Cli.Usage);
            case CliRequest.NewDrawing:
                var created = _host.New();
                return created.IsSuccess ? new IpcReply(0, $"Opened a new drawing, autosaving to {created.Value.FilePath}.") : Failed(created.Error);
            case CliRequest.Open open:
                return await OpenAll(open.Files, folder, deadline).ConfigureAwait(false);
            case CliRequest.List:
                return new IpcReply(0, List());
            case CliRequest.Save save:
                var toSave = Find(save.File, folder);
                if (toSave.IsFailure)
                    return Failed(toSave.Error);
                var saved = await toSave.Value.Flush().ConfigureAwait(false);
                return saved.IsSuccess ? new IpcReply(0, $"Saved {toSave.Value.FilePath}.") : Failed(saved.Error);
            case CliRequest.History history:
                return await Read(history.File, folder, call, deadline).ConfigureAwait(false);
            case CliRequest.Run { Command: DrawCommand.Show } show:
                return await Read(show.File, folder, call, deadline).ConfigureAwait(false);
            case CliRequest.Run { Command: DrawCommand.CloseDocument }:
                return new IpcReply(2, "Close a drawing from its window.");
            case CliRequest.Run run:
                return await Run(run.File, folder, run.Command, call, deadline).ConfigureAwait(false);
            default:
                throw new InvalidOperationException($"Unhandled request {call.Request.GetType().Name}.");
        }
    }

    private async Task<IpcReply> Run(string file, string folder, DrawCommand command, CliCall call, CancellationToken deadline)
    {
        // Editing a file that isn't open opens it first, so the change goes through the app and shows in a window.
        var session = await OpenOne(file, folder, deadline).ConfigureAwait(false);
        if (deadline.IsCancellationRequested)
            return TookTooLong;
        if (session.IsFailure)
            return Failed(session.Error);
        using var requester = _host.Queue.Join(call.Source);
        var reply = await requester.Send(session.Value.DocumentId, command).ConfigureAwait(false);
        return reply.IsSuccess ? new IpcReply(0, Cli.Format(call.Request, reply.Value)) : Failed(reply.Error);
    }

    /// <summary>Answers show or history from the open drawing, or reads the file without opening a window if it isn't open.</summary>
    private async Task<IpcReply> Read(string file, string folder, CliCall call, CancellationToken deadline)
    {
        var path = FullPath(file, folder);
        if (path.IsFailure)
            return Failed(path.Error);
        var open = _host.FindOpen(path.Value);
        if (open.IsSuccess)
        {
            using var requester = _host.Queue.Join(call.Source);
            var reply = await requester.Send(open.Value.DocumentId, new DrawCommand.Show()).ConfigureAwait(false);
            return reply.IsSuccess ? new IpcReply(0, Cli.Format(call.Request, reply.Value)) : Failed(reply.Error);
        }
        if (open.Error.Code != ErrorCode.NotFound)
            return Failed(open.Error);
        if (call.Request is CliRequest.History)
            return new IpcReply(0, "No changes yet: the drawing isn't open.");
        try
        {
            var loaded = await Task.Run(() => DrawingFile.Load(path.Value), CancellationToken.None).WaitAsync(deadline).ConfigureAwait(false);
            return loaded.IsSuccess ? new IpcReply(0, Cli.Describe(loaded.Value)) : Failed(loaded.Error);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return TookTooLong;
        }
    }

    private async Task<IpcReply> OpenAll(IReadOnlyList<string> files, string folder, CancellationToken deadline)
    {
        var output = new StringBuilder();
        var failed = false;
        foreach (var file in files)
        {
            var session = await OpenOne(file, folder, deadline).ConfigureAwait(false);
            if (session.IsSuccess)
            {
                _raise(session.Value);
                output.AppendLine(CultureInfo.InvariantCulture, $"Opened {session.Value.FilePath}.");
            }
            else
            {
                failed = true;
                output.AppendLine(session.Error.Message);
            }
        }
        return new IpcReply(failed ? 1 : 0, output.ToString().TrimEnd());
    }

    private async Task<Result<DocumentSession>> OpenOne(string file, string folder, CancellationToken deadline)
    {
        var path = FullPath(file, folder);
        return path.IsSuccess ? await _host.Open(path.Value, deadline).ConfigureAwait(false) : Result<DocumentSession>.Fail(path.Error);
    }

    private Result<DocumentSession> Find(string file, string folder)
    {
        var path = FullPath(file, folder);
        return path.IsSuccess ? _host.FindOpen(path.Value) : Result<DocumentSession>.Fail(path.Error);
    }

    private string List()
    {
        var sessions = _host.Sessions;
        if (sessions.Count == 0)
            return "No drawings are open.";
        var text = new StringBuilder();
        foreach (var session in sessions)
        {
            var where = session.Location == DocumentLocation.Autosave ? "  (unnamed, autosaved)" : "";
            text.AppendLine(CultureInfo.InvariantCulture, $"{session.FilePath}  {session.Latest.Boxes.Count} boxes, {session.Latest.Lines.Count} lines{where}");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>A file name made absolute against the folder the command was typed in.</summary>
    private static Result<string> FullPath(string file, string folder)
    {
        if (file.Length == 0 || file.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return Result<string>.Fail(ErrorCode.InvalidInput, "That isn't a usable file name.");
        try
        {
            return Path.GetFullPath(file, folder);
        }
        catch (ArgumentException)
        {
            // GetFullPath refuses a folder that isn't absolute, which the protocol has already checked, so this is the file name's fault.
            return Result<string>.Fail(ErrorCode.InvalidInput, $"'{file}' isn't a usable file name.");
        }
    }

    private static IpcReply Failed(Error error) => new(1, error.Message);
}
