using System.Globalization;
using System.Text;
using DrawArr.Commands;
using DrawArr.Core;

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
        var call = parsed.Value;
        var work = Serve(call, workingDirectory);
        return work.Wait(Timeout) ? work.Result : new IpcReply(1, "draw-arr took too long to answer.");
    }

    private async Task<IpcReply> Serve(CliCall call, string folder)
    {
        switch (call.Request)
        {
            case CliRequest.Help:
                return new IpcReply(0, Cli.Usage);
            case CliRequest.NewDrawing:
                var created = _host.New();
                return created.IsSuccess ? new IpcReply(0, $"Opened a new drawing, autosaving to {created.Value.FilePath}.") : Failed(created.Error);
            case CliRequest.Open open:
                return await OpenAll(open.Files, folder).ConfigureAwait(false);
            case CliRequest.List:
                return new IpcReply(0, List());
            case CliRequest.Save save:
                var toSave = Find(save.File, folder);
                if (toSave.IsFailure)
                    return Failed(toSave.Error);
                var saved = await toSave.Value.Flush().ConfigureAwait(false);
                return saved.IsSuccess ? new IpcReply(0, $"Saved {toSave.Value.FilePath}.") : Failed(saved.Error);
            case CliRequest.History history:
                return await Run(history.File, folder, new DrawCommand.Show(), call).ConfigureAwait(false);
            case CliRequest.Run run:
                return await Run(run.File, folder, run.Command, call).ConfigureAwait(false);
            default:
                throw new InvalidOperationException($"Unhandled request {call.Request.GetType().Name}.");
        }
    }

    private async Task<IpcReply> Run(string file, string folder, DrawCommand command, CliCall call)
    {
        // Editing a file that isn't open opens it first, so the change goes through the app and shows in a window.
        var session = await OpenOne(file, folder).ConfigureAwait(false);
        if (session.IsFailure)
            return Failed(session.Error);
        using var requester = _host.Queue.Join(call.Source);
        var reply = await requester.Send(session.Value.DocumentId, command).ConfigureAwait(false);
        return reply.IsSuccess ? new IpcReply(0, Cli.Format(call.Request, reply.Value)) : Failed(reply.Error);
    }

    private async Task<IpcReply> OpenAll(IReadOnlyList<string> files, string folder)
    {
        var output = new StringBuilder();
        var failed = false;
        foreach (var file in files)
        {
            var session = await OpenOne(file, folder).ConfigureAwait(false);
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

    private async Task<Result<DocumentSession>> OpenOne(string file, string folder)
    {
        var path = FullPath(file, folder);
        return path.IsSuccess ? await _host.Open(path.Value).ConfigureAwait(false) : Result<DocumentSession>.Fail(path.Error);
    }

    private Result<DocumentSession> Find(string file, string folder)
    {
        var path = FullPath(file, folder);
        if (path.IsFailure)
            return Result<DocumentSession>.Fail(path.Error);
        return _host.Find(path.Value) is { } session
            ? session
            : Result<DocumentSession>.Fail(ErrorCode.NotFound, $"{path.Value} isn't open in draw-arr.");
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
