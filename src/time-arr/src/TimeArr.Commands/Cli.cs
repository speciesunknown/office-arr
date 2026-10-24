using System.Globalization;
using System.Text;
using TimeArr.Core;

namespace TimeArr.Commands;

/// <summary>What a command line asks for.</summary>
/// <param name="Command">The command to run, or null for "just show the window" / help.</param>
/// <param name="ShowWindow">Bring the window to the front (no arguments, or <c>show</c>).</param>
/// <param name="Help">Print <see cref="Cli.Usage"/>.</param>
public sealed record CliRequest(TimerCommand? Command, bool ShowWindow = false, bool Help = false);

/// <summary>
/// The command line: <c>time-arr [verb] [args]</c>. One parser for both a fresh start and
/// arguments forwarded over IPC, so they always mean the same thing.
/// </summary>
public static class Cli
{
    /// <summary>Most arguments a command line may have.</summary>
    public const int MaxArgs = 8;

    /// <summary>Longest single argument, in UTF-16 units (a name is at most 64; this leaves room for typos to be reported).</summary>
    public const int MaxArgLength = 256;

    public const string Usage = """
        usage: time-arr [command]

          (no command)              open the time-arr window
          show                      bring the window to the front
          list                      list all timers
          status [timer]            show one timer
          start [timer]             start a timer
          pause [timer]             pause a timer
          reset [timer]             set a timer back to zero (a running timer keeps running)
          add <name>                add a timer
          rename <timer> <name>     rename a timer
          delete <timer>            delete a timer
          help                      show this help

        <timer> is a timer's name, or --id <number> for its id.
        [timer] can be left out to mean the timer named "default".
        Names are always names, even if they are digits: use --id for ids.
        """;

    public static Result<CliRequest> Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count > MaxArgs)
            return Fail($"Too many arguments ({args.Count}; at most {MaxArgs}).");
        foreach (var arg in args)
        {
            if (arg is null)
                return Fail("An argument is missing.");
            if (arg.Length > MaxArgLength)
                return Fail($"An argument is longer than {MaxArgLength} characters.");
        }

        if (args.Count == 0)
            return new CliRequest(null, ShowWindow: true);

        var verb = args[0];
        var rest = args.Skip(1).ToArray();
        return verb switch
        {
            "help" or "--help" or "-h" => NoArgs(rest, new CliRequest(null, Help: true)),
            "show" => NoArgs(rest, new CliRequest(null, ShowWindow: true)),
            "list" => NoArgs(rest, new CliRequest(new TimerCommand.List())),
            "status" => WithOptionalTimer(rest, t => new TimerCommand.Get(t)),
            "start" => WithOptionalTimer(rest, t => new TimerCommand.Start(t)),
            "pause" => WithOptionalTimer(rest, t => new TimerCommand.Pause(t)),
            "reset" => WithOptionalTimer(rest, t => new TimerCommand.Reset(t)),
            "add" => rest.Length == 1
                ? new CliRequest(new TimerCommand.Add(rest[0]))
                : Fail("add needs exactly one name: time-arr add <name>"),
            "rename" => Rename(rest),
            "delete" => WithRequiredTimer(rest, "delete", t => new TimerCommand.Delete(t)),
            _ => Fail($"Unknown command '{verb}'. Try: time-arr help"),
        };
    }

    /// <summary>What to print for a successful command.</summary>
    public static string Format(TimerCommand command, CommandReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (command is TimerCommand.List)
        {
            if (reply.Timers.Count == 0)
                return "No timers. Add one with: time-arr add <name>";
            var text = new StringBuilder();
            foreach (var timer in reply.Timers)
                text.AppendLine(Line(timer, reply.NowUtc));
            return text.ToString().TrimEnd();
        }

        var one = reply.Timers.Single();
        return command switch
        {
            TimerCommand.Delete => $"Deleted {Describe(one)}.",
            TimerCommand.Add => $"Added {Describe(one)}.",
            _ => Line(one, reply.NowUtc),
        };
    }

    private static string Describe(TimerEntry timer) =>
        string.Create(CultureInfo.InvariantCulture, $"'{timer.Name}' (id {timer.Id})");

    private static string Line(TimerEntry timer, DateTimeOffset now) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{timer.Id,4}  {(timer.State.IsRunning ? "running" : "paused "),-7}  {ElapsedFormat.Format(timer.State.ElapsedAt(now)),14}  {timer.Name}");

    private static Result<CliRequest> NoArgs(string[] rest, CliRequest request) =>
        rest.Length == 0 ? request : Fail("This command takes no arguments.");

    private static Result<CliRequest> WithOptionalTimer(string[] rest, Func<TimerRef, TimerCommand> make)
    {
        if (rest.Length == 0)
            return new CliRequest(make(new TimerRef.Default()));
        var target = ParseTimer(rest, out var used);
        if (target.IsFailure)
            return Result<CliRequest>.Fail(target.Error);
        return used == rest.Length ? new CliRequest(make(target.Value)) : Fail("Too many arguments.");
    }

    private static Result<CliRequest> WithRequiredTimer(string[] rest, string verb, Func<TimerRef, TimerCommand> make) =>
        rest.Length == 0 ? Fail($"{verb} needs a timer: time-arr {verb} <name>  or  --id <number>") : WithOptionalTimer(rest, make);

    private static Result<CliRequest> Rename(string[] rest)
    {
        if (rest.Length < 2)
            return Fail("rename needs a timer and a new name: time-arr rename <timer> <new name>");
        var target = ParseTimer(rest, out var used);
        if (target.IsFailure)
            return Result<CliRequest>.Fail(target.Error);
        return rest.Length == used + 1
            ? new CliRequest(new TimerCommand.Rename(target.Value, rest[used]))
            : Fail("rename needs a timer and a new name: time-arr rename <timer> <new name>");
    }

    /// <summary>
    /// <c>--id N</c> (exactly ASCII digits, no sign or spaces) or a name. Anything else starting with
    /// '-' is an unknown option: names can't start with '-', so there's no ambiguity.
    /// </summary>
    private static Result<TimerRef> ParseTimer(string[] rest, out int used)
    {
        used = 0;
        if (rest[0] == "--id")
        {
            if (rest.Length < 2)
                return Result<TimerRef>.Fail(ErrorCode.InvalidInput, "--id needs a number.");
            if (!long.TryParse(rest[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                return Result<TimerRef>.Fail(ErrorCode.InvalidInput, $"'{rest[1]}' is not a timer id (a positive whole number).");
            used = 2;
            return new TimerRef.ById(id);
        }
        if (rest[0].StartsWith('-'))
            return Result<TimerRef>.Fail(ErrorCode.InvalidInput, $"Unknown option '{rest[0]}'.");
        used = 1;
        return new TimerRef.ByName(rest[0]);
    }

    private static Result<CliRequest> Fail(string message) => Result<CliRequest>.Fail(ErrorCode.InvalidInput, message);
}
