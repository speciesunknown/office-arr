using System.Globalization;
using System.Text;
using DrawArr.Core;

namespace DrawArr.Commands;

/// <summary>What a command line asks for: one of a closed set of cases.</summary>
public abstract record CliRequest
{
    private CliRequest() { }

    /// <summary>Print <see cref="Cli.Usage"/>.</summary>
    public sealed record Help : CliRequest;

    /// <summary>Open a window on a new, empty drawing.</summary>
    public sealed record NewDrawing : CliRequest;

    /// <summary>Open each file in a window of its own, or bring its window forward if it is open.</summary>
    public sealed record Open(IReadOnlyList<string> Files) : CliRequest;

    /// <summary>List the open drawings.</summary>
    public sealed record List : CliRequest;

    /// <summary>Save an open drawing now.</summary>
    public sealed record Save(string File) : CliRequest;

    /// <summary>Print an open drawing's undo history.</summary>
    public sealed record History(string File) : CliRequest;

    /// <summary>Run a command against a drawing, opening it in a window first if it isn't open.</summary>
    public sealed record Run(string File, DrawCommand Command) : CliRequest;
}

/// <summary>A parsed command line: the request, and who the history should say it came from.</summary>
public sealed record CliCall(CliRequest Request, CommandSource Source);

/// <summary>The command line, parsed the same way on a fresh start and when forwarded over IPC.</summary>
public static class Cli
{
    /// <summary>Most arguments a command line may have, which leaves room for several files to open at once.</summary>
    public const int MaxArgs = 64;

    /// <summary>Longest single argument in UTF-16 units, which fits a box's longest text after escaping.</summary>
    public const int MaxArgLength = 4096;

    public const string Usage = """
        usage: draw-arr [--agent] [command]

          (nothing) or new                      open a window on a new drawing
          <file>... or open <file>...           open drawings, each in its own window
          list                                  list the open drawings
          show <file>                           print a drawing's boxes and lines
          history <file>                        print a drawing's history
          add-box <file> <x> <y> <w> <h> [--colour C] [--text T]
          move-box <file> <id> <x> <y>
          resize-box <file> <id> <x> <y> <w> <h>
          set-text <file> <id> <text>           an empty text clears it; \n makes a new line
          colour <file> <id> <colour>
          add-line <file> <end> <end> [--colour C]
          move-end <file> <line id> start|end <end>
          delete <file> <id>
          undo <file> / redo <file>             takes back or redoes the last change, whoever made it
          save <file>
          help

        Units are millimetres, with 0,0 in the middle of the view and y growing downwards; values snap to the 5 mm grid.
        <end> is x,y for a free end, box:<id> for the point on that box facing the other end, or box:<id>@x,y for the
        point on the box nearest x,y. Colours: white, red, yellow, blue, green.
        Edits go to the running draw-arr, which opens the file in a window if it isn't open; files are never edited behind its back.
        --agent marks the change as an agent's in the history (a label only).
        """;

    private static readonly Dictionary<string, Colour> Colours = new(StringComparer.Ordinal)
    {
        ["white"] = Colour.White,
        ["red"] = Colour.Red,
        ["yellow"] = Colour.Yellow,
        ["blue"] = Colour.Blue,
        ["green"] = Colour.Green,
    };

    public static Result<CliCall> Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count > MaxArgs)
            return Result<CliCall>.Fail(ErrorCode.InvalidInput, $"Too many arguments ({args.Count}; at most {MaxArgs}).");
        foreach (var arg in args)
        {
            if (arg is null)
                return Result<CliCall>.Fail(ErrorCode.InvalidInput, "An argument is missing.");
            if (arg.Length > MaxArgLength)
                return Result<CliCall>.Fail(ErrorCode.InvalidInput, $"An argument is longer than {MaxArgLength} characters.");
        }

        var source = CommandSource.Cli;
        var rest = args.ToList();
        if (rest.Count > 0 && rest[0] == "--agent")
        {
            source = CommandSource.Agent;
            rest.RemoveAt(0);
        }
        var request = ParseRequest(rest);
        return request.IsSuccess ? new CliCall(request.Value, source) : Result<CliCall>.Fail(request.Error);
    }

    private static Result<CliRequest> ParseRequest(List<string> args)
    {
        if (args.Count == 0)
            return new CliRequest.NewDrawing();
        var verb = args[0];
        var rest = args.Skip(1).ToArray();
        return verb switch
        {
            "help" or "--help" or "-h" => Exactly(rest, 0, "help", _ => new CliRequest.Help()),
            "new" => Exactly(rest, 0, "new", _ => new CliRequest.NewDrawing()),
            "list" => Exactly(rest, 0, "list", _ => new CliRequest.List()),
            "open" => rest.Length == 0 ? Fail("open needs at least one file.") : Files(rest),
            "show" => Exactly(rest, 1, "show <file>", a => new CliRequest.Run(a[0], new DrawCommand.Show())),
            "history" => Exactly(rest, 1, "history <file>", a => new CliRequest.History(a[0])),
            "save" => Exactly(rest, 1, "save <file>", a => new CliRequest.Save(a[0])),
            "undo" => Exactly(rest, 1, "undo <file>", a => new CliRequest.Run(a[0], new DrawCommand.Undo())),
            "redo" => Exactly(rest, 1, "redo <file>", a => new CliRequest.Run(a[0], new DrawCommand.Redo())),
            "add-box" => AddBox(rest),
            "move-box" => IdAndNumbers(rest, "move-box <file> <id> <x> <y>", 2, (id, n) => new DrawCommand.MoveBox(id, n[0], n[1])),
            "resize-box" => IdAndNumbers(rest, "resize-box <file> <id> <x> <y> <w> <h>", 4,
                (id, n) => new DrawCommand.ResizeBox(id, n[0], n[1], n[2], n[3])),
            "set-text" => SetText(rest),
            "colour" or "color" => SetColour(rest),
            "add-line" => AddLine(rest),
            "move-end" => MoveEnd(rest),
            "delete" => IdAndNumbers(rest, "delete <file> <id>", 0, (id, _) => new DrawCommand.Delete(id)),
            _ when verb.StartsWith('-') => Fail($"Unknown option '{verb}'. Try: draw-arr help"),
            // Anything else is taken as files to open, so draw-arr drawing.xml works.
            _ => Files([.. args]),
        };
    }

    /// <summary>What to print for a successful request that ran a command.</summary>
    public static string Format(CliRequest request, DocumentReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        switch (request)
        {
            case CliRequest.Run { Command: DrawCommand.Show }:
                return Describe(reply.Drawing);
            case CliRequest.History:
                var text = new StringBuilder();
                for (var i = 0; i < reply.History.Entries.Length; i++)
                {
                    var entry = reply.History.Entries[i];
                    text.AppendLine(CultureInfo.InvariantCulture,
                        $"{i + 1,4}  {(i < reply.History.Applied ? "     " : "undone")}  {SourceWord(entry.Source),-6}  {entry.Description}");
                }
                return text.Length == 0 ? "No changes yet." : text.ToString().TrimEnd();
            case CliRequest.Run { Command: DrawCommand.Undo or DrawCommand.Redo }:
                return reply.History.Applied == 0 ? "Nothing left to undo." : $"Now at: {reply.History.Entries[reply.History.Applied - 1].Description}";
            case CliRequest.Run when reply.Change == DocumentChange.None:
                return "Nothing changed.";
            case CliRequest.Run:
                return reply.History.Entries[^1].Description;
            default:
                throw new InvalidOperationException($"Unhandled request {request.GetType().Name}.");
        }
    }

    /// <summary>A drawing as lines of text, one per box and line, for people and agents to read.</summary>
    public static string Describe(Drawing drawing)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        if (drawing.Count == 0)
            return "The drawing is empty.";
        var text = new StringBuilder();
        foreach (var box in drawing.Boxes.Values)
        {
            text.Append(CultureInfo.InvariantCulture, $"box {box.Id} at {box.Bounds} {ColourWord(box.Colour)}");
            if (box.Text.Length > 0)
                text.Append(CultureInfo.InvariantCulture, $" text \"{box.Text.Replace("\n", "\\n", StringComparison.Ordinal)}\"");
            text.AppendLine();
        }
        foreach (var line in drawing.Lines.Values)
            text.AppendLine(CultureInfo.InvariantCulture, $"line {line.Id} from {End(drawing, line.Start)} to {End(drawing, line.End)} {ColourWord(line.Colour)}");
        return text.ToString().TrimEnd();
    }

    private static string End(Drawing drawing, LineEnd end) => end switch
    {
        LineEnd.Free free => free.At.ToString(),
        LineEnd.Attached attached => $"{drawing.Resolve(end)} on box {attached.BoxId}",
        _ => throw new InvalidOperationException($"Unhandled line end {end.GetType().Name}."),
    };

    private static string ColourWord(Colour colour) => Colours.First(p => p.Value == colour).Key;

    private static string SourceWord(CommandSource source) => source switch
    {
        CommandSource.Window => "window",
        CommandSource.Cli => "cli",
        CommandSource.Agent => "agent",
        _ => throw new InvalidOperationException($"Unhandled source {source}."),
    };

    private static Result<CliRequest> Files(string[] files)
    {
        foreach (var file in files)
        {
            if (file.StartsWith('-'))
                return Fail($"Unknown option '{file}'. Try: draw-arr help");
        }
        return new CliRequest.Open(files);
    }

    private static Result<CliRequest> Exactly(string[] rest, int count, string usage, Func<string[], CliRequest> make) =>
        rest.Length == count ? make(rest) : Fail($"Usage: draw-arr {usage}");

    /// <summary>Reads a file, an id and then a number of millimetre values.</summary>
    private static Result<CliRequest> IdAndNumbers(string[] rest, string usage, int count, Func<long, int[], DrawCommand> make)
    {
        if (rest.Length != count + 2)
            return Fail($"Usage: draw-arr {usage}");
        var id = Id(rest[1]);
        if (id.IsFailure)
            return Result<CliRequest>.Fail(id.Error);
        var numbers = new int[count];
        for (var i = 0; i < count; i++)
        {
            var n = Millimetres(rest[i + 2]);
            if (n.IsFailure)
                return Result<CliRequest>.Fail(n.Error);
            numbers[i] = n.Value;
        }
        return new CliRequest.Run(rest[0], make(id.Value, numbers));
    }

    private static Result<CliRequest> AddBox(string[] rest)
    {
        const string usage = "Usage: draw-arr add-box <file> <x> <y> <w> <h> [--colour C] [--text T]";
        if (rest.Length < 5)
            return Fail(usage);
        var numbers = new int[4];
        for (var i = 0; i < 4; i++)
        {
            var n = Millimetres(rest[i + 1]);
            if (n.IsFailure)
                return Result<CliRequest>.Fail(n.Error);
            numbers[i] = n.Value;
        }
        var colour = Colour.White;
        var text = "";
        for (var i = 5; i < rest.Length; i += 2)
        {
            if (i + 1 >= rest.Length)
                return Fail(usage);
            switch (rest[i])
            {
                case "--colour" or "--color":
                    var c = ColourNamed(rest[i + 1]);
                    if (c.IsFailure)
                        return Result<CliRequest>.Fail(c.Error);
                    colour = c.Value;
                    break;
                case "--text":
                    text = Unescape(rest[i + 1]);
                    break;
                default:
                    return Fail(usage);
            }
        }
        return new CliRequest.Run(rest[0], new DrawCommand.AddBox(numbers[0], numbers[1], numbers[2], numbers[3], colour, text));
    }

    private static Result<CliRequest> SetText(string[] rest)
    {
        if (rest.Length != 3)
            return Fail("Usage: draw-arr set-text <file> <id> <text>");
        var id = Id(rest[1]);
        return id.IsSuccess
            ? new CliRequest.Run(rest[0], new DrawCommand.SetText(id.Value, Unescape(rest[2])))
            : Result<CliRequest>.Fail(id.Error);
    }

    private static Result<CliRequest> SetColour(string[] rest)
    {
        if (rest.Length != 3)
            return Fail("Usage: draw-arr colour <file> <id> <colour>");
        var id = Id(rest[1]);
        if (id.IsFailure)
            return Result<CliRequest>.Fail(id.Error);
        var colour = ColourNamed(rest[2]);
        return colour.IsSuccess
            ? new CliRequest.Run(rest[0], new DrawCommand.SetColour(id.Value, colour.Value))
            : Result<CliRequest>.Fail(colour.Error);
    }

    private static Result<CliRequest> AddLine(string[] rest)
    {
        const string usage = "Usage: draw-arr add-line <file> <end> <end> [--colour C]";
        if (rest.Length != 3 && !(rest.Length == 5 && rest[3] is "--colour" or "--color"))
            return Fail(usage);
        var start = EndNamed(rest[1]);
        if (start.IsFailure)
            return Result<CliRequest>.Fail(start.Error);
        var end = EndNamed(rest[2]);
        if (end.IsFailure)
            return Result<CliRequest>.Fail(end.Error);
        var colour = rest.Length == 5 ? ColourNamed(rest[4]) : Colour.White;
        return colour.IsSuccess
            ? new CliRequest.Run(rest[0], new DrawCommand.AddLine(start.Value, end.Value, colour.Value))
            : Result<CliRequest>.Fail(colour.Error);
    }

    private static Result<CliRequest> MoveEnd(string[] rest)
    {
        if (rest.Length != 4)
            return Fail("Usage: draw-arr move-end <file> <line id> start|end <end>");
        var id = Id(rest[1]);
        if (id.IsFailure)
            return Result<CliRequest>.Fail(id.Error);
        LineEndName? which = rest[2] switch { "start" => LineEndName.Start, "end" => LineEndName.End, _ => null };
        if (which is null)
            return Fail($"'{rest[2]}' isn't start or end.");
        var to = EndNamed(rest[3]);
        return to.IsSuccess
            ? new CliRequest.Run(rest[0], new DrawCommand.MoveLineEnd(id.Value, which.Value, to.Value))
            : Result<CliRequest>.Fail(to.Error);
    }

    /// <summary>Reads x,y, box:id or box:id@x,y.</summary>
    private static Result<EndAt> EndNamed(string text)
    {
        if (text.StartsWith("box:", StringComparison.Ordinal))
        {
            var body = text[4..];
            var at = body.IndexOf('@', StringComparison.Ordinal);
            var id = Id(at < 0 ? body : body[..at]);
            if (id.IsFailure)
                return Result<EndAt>.Fail(id.Error);
            if (at < 0)
                return new EndAt.FacingOther(id.Value);
            var point = PointNamed(body[(at + 1)..]);
            return point.IsSuccess ? new EndAt.OnBox(id.Value, point.Value.X, point.Value.Y) : Result<EndAt>.Fail(point.Error);
        }
        var free = PointNamed(text);
        return free.IsSuccess ? new EndAt.Free(free.Value.X, free.Value.Y) : Result<EndAt>.Fail(free.Error);
    }

    private static Result<(int X, int Y)> PointNamed(string text)
    {
        var parts = text.Split(',');
        if (parts.Length != 2)
            return Result<(int, int)>.Fail(ErrorCode.InvalidInput, $"'{text}' isn't a point like 10,-5 or a box like box:3.");
        var x = Millimetres(parts[0]);
        if (x.IsFailure)
            return Result<(int, int)>.Fail(x.Error);
        var y = Millimetres(parts[1]);
        return y.IsSuccess ? (x.Value, y.Value) : Result<(int, int)>.Fail(y.Error);
    }

    /// <summary>A box or line id, from 1 to <see cref="Drawing.MaxId"/>.</summary>
    private static Result<long> Id(string text)
    {
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1 || value > Drawing.MaxId)
            return Result<long>.Fail(ErrorCode.InvalidInput, $"'{text}' isn't an id (a whole number from 1).");
        return value;
    }

    /// <summary>A whole number of millimetres that fits an int, where the commands then check the drawing's real limits.</summary>
    private static Result<int> Millimetres(string text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : Result<int>.Fail(ErrorCode.InvalidInput, $"'{text}' isn't a whole number of millimetres in range.");

    private static Result<Colour> ColourNamed(string text) =>
        Colours.TryGetValue(text, out var colour)
            ? colour
            : Result<Colour>.Fail(ErrorCode.InvalidInput, $"'{text}' isn't a colour; use one of: {string.Join(", ", Colours.Keys)}.");

    /// <summary>The text with <c>\n</c> made a line break and <c>\\</c> a backslash, so a shell argument can hold several lines.</summary>
    private static string Unescape(string text) =>
        text.Replace("\\\\", "\u0000", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal).Replace('\u0000', '\\');

    private static Result<CliRequest> Fail(string message) => Result<CliRequest>.Fail(ErrorCode.InvalidInput, message);
}
