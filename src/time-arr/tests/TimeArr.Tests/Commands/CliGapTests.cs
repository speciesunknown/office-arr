using System.Text.RegularExpressions;
using TimeArr.Commands;
using TimeArr.Core;

namespace TimeArr.Tests.Commands;

/// <summary>Edge cases for <see cref="Cli"/> not covered by <see cref="CliTests"/>.</summary>
public partial class CliGapTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private static TimerCommand Command(params string[] args)
    {
        var parsed = Cli.Parse(args);
        Assert.True(parsed.IsSuccess, parsed.ToString());
        return Assert.IsType<CliRequest.Run>(parsed.Value).Command;
    }

    private static Error Failure(params string[] args)
    {
        var parsed = Cli.Parse(args);
        Assert.True(parsed.IsFailure, parsed.ToString());
        return parsed.Error;
    }

    [Fact]
    public void Every_verb_in_the_usage_text_is_understood()
    {
        var verbs = VerbLine().Matches(Cli.Usage).Select(m => m.Groups[1].Value).ToList();
        Assert.Contains("rename", verbs);

        foreach (var verb in verbs)
        {
            var parsed = Cli.Parse([verb]);
            Assert.False(parsed.IsFailure && parsed.Error.Message.StartsWith("Unknown command", StringComparison.Ordinal),
                $"Usage lists '{verb}' but Parse doesn't know it.");
        }
    }

    [GeneratedRegex(@"^\s{2}([a-z]+)\b", RegexOptions.Multiline)]
    private static partial Regex VerbLine();

    [Fact]
    public void Verbs_are_case_sensitive()
    {
        Assert.StartsWith("Unknown command", Failure("START").Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("show", "extra")]
    [InlineData("help", "extra")]
    [InlineData("delete", "--id")]
    [InlineData("delete", "--id", "3", "extra")]
    [InlineData("status", "--id", "3", "extra")]
    [InlineData("rename", "a", "b", "c")]
    [InlineData("rename", "work", "--id", "5")]
    [InlineData("--id", "3")]
    [InlineData("start", "--")]
    [InlineData("start", "-")]
    public void More_malformed_command_lines_are_invalid_input(params string[] args)
    {
        Assert.Equal(ErrorCode.InvalidInput, Failure(args).Code);
    }

    [Fact]
    public void Leading_zeros_in_an_id_are_accepted()
    {
        Assert.Equal(new TimerRef.ById(7), ((TimerCommand.Start)Command("start", "--id", "007")).Target);
    }

    [Fact]
    public void Id_works_for_every_timer_verb()
    {
        var id = new TimerRef.ById(3);

        Assert.Equal(id, ((TimerCommand.Get)Command("status", "--id", "3")).Target);
        Assert.Equal(id, ((TimerCommand.Pause)Command("pause", "--id", "3")).Target);
        Assert.Equal(id, ((TimerCommand.Reset)Command("reset", "--id", "3")).Target);
        Assert.Equal(id, ((TimerCommand.Delete)Command("delete", "--id", "3")).Target);
        Assert.Equal(new TimerCommand.Rename(id, "new"), Command("rename", "--id", "3", "new"));
    }

    [Fact]
    public void The_largest_id_is_accepted()
    {
        Assert.Equal(new TimerRef.ById(long.MaxValue), ((TimerCommand.Get)Command("status", "--id", long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture))).Target);
    }

    [Theory]
    [InlineData("rename", "work", "--x")]
    [InlineData("rename", "work", "")]
    [InlineData("rename", "", "new")]
    [InlineData("add", "-x")]
    [InlineData("add", "")]
    [InlineData("add", "   ")]
    [InlineData("add", "a\tb")]
    [InlineData("start", "")]
    [InlineData("status", "a\nb")]
    [InlineData("delete", "‮evil")]
    public void Bad_names_fail_while_parsing(params string[] args)
    {
        Assert.Equal(ErrorCode.InvalidInput, Failure(args).Code);
    }

    [Fact]
    public void Names_on_the_command_line_are_trimmed_and_normalized_while_parsing()
    {
        Assert.Equal(new TimerCommand.Add("café"), Command("add", "  café "));
        Assert.Equal(new TimerRef.ByName("work"), ((TimerCommand.Start)Command("start", " work ")).Target);
        Assert.Equal(new TimerCommand.Rename(new TimerRef.ByName("a"), "b"), Command("rename", " a", "b "));
    }

    [Theory]
    [InlineData]
    [InlineData("show")]
    public void Show_and_no_arguments_are_the_show_window_case(params string[] args)
    {
        Assert.IsType<CliRequest.ShowWindow>(Cli.Parse(args).Value);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_is_the_help_case(string arg)
    {
        Assert.IsType<CliRequest.Help>(Cli.Parse([arg]).Value);
    }

    [Fact]
    public void Printable_replaces_control_characters_but_keeps_newline_and_tab()
    {
        Assert.Equal("a?[31mb?c\nd\te?", Cli.Printable("a\u001b[31mb\u0007c\nd\te\u009b"));
        Assert.Equal("café \U0001F600", Cli.Printable("café \U0001F600"));
        Assert.Equal("", Cli.Printable(""));
    }

    [Fact]
    public void Printable_with_null_is_a_bug()
    {
        Assert.Throws<ArgumentNullException>(() => Cli.Printable(null!));
    }

    [Fact]
    public void Unknown_command_message_echoes_the_verb_which_printable_then_defuses()
    {
        var message = Failure("\u001b[2J").Message;

        Assert.DoesNotContain('\u001b', Cli.Printable(message));
    }

    [Fact]
    public void Exactly_the_limits_are_allowed()
    {
        // An argument at MaxArgLength gets past the length check and fails as a name; one more fails the length check.
        Assert.DoesNotContain($"longer than {Cli.MaxArgLength}", Failure("add", new string('x', Cli.MaxArgLength)).Message, StringComparison.Ordinal);
        Assert.Contains($"longer than {Cli.MaxArgLength}", Failure("add", new string('x', Cli.MaxArgLength + 1)).Message, StringComparison.Ordinal);
        // MaxArgs arguments pass the count check (then fail the grammar); one more fails the count.
        var atLimit = Enumerable.Repeat("x", Cli.MaxArgs - 1).Prepend("add").ToArray();
        var overLimit = atLimit.Append("x").ToArray();
        Assert.DoesNotContain("Too many arguments (", Failure(atLimit).Message, StringComparison.Ordinal);
        Assert.StartsWith("Too many arguments (", Failure(overLimit).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_argument_list_is_a_bug_and_a_null_argument_is_invalid()
    {
        Assert.Throws<ArgumentNullException>(() => Cli.Parse(null!));
        Assert.Equal(ErrorCode.InvalidInput, Failure("add", null!).Code);
    }

    [Fact]
    public void Format_add_and_delete_name_the_timer_and_its_id()
    {
        var entry = new TimerEntry(12, "work", TimerState.Zero);
        var reply = new CommandReply([entry], T0);

        Assert.Equal("Added 'work' (id 12).", Cli.Format(new TimerCommand.Add("work"), reply));
        Assert.Equal("Deleted 'work' (id 12).", Cli.Format(new TimerCommand.Delete(new TimerRef.ById(12)), reply));
    }

    [Fact]
    public void Format_of_one_timer_shows_state_elapsed_and_name()
    {
        var running = TimerState.Create(true, T0, TimeSpan.Zero).Value;
        var reply = new CommandReply([new TimerEntry(3, "deep work", running)], T0.AddDays(1).AddMinutes(1));

        var line = Cli.Format(new TimerCommand.Get(new TimerRef.Default()), reply);

        Assert.Contains("running", line, StringComparison.Ordinal);
        Assert.Contains("1d 00:01:00", line, StringComparison.Ordinal);
        Assert.EndsWith("deep work", line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void Format_names_each_phase()
    {
        var stopped = new TimerEntry(1, "a", TimerState.Zero);
        var paused = new TimerEntry(2, "b", TimerState.Zero.Start(T0).Pause(T0.AddMinutes(1)));
        var running = new TimerEntry(3, "c", TimerState.Zero.Start(T0));

        var lines = Cli.Format(new TimerCommand.List(), new CommandReply([stopped, paused, running], T0.AddMinutes(2))).Split('\n');

        Assert.Contains("stopped", lines[0], StringComparison.Ordinal);
        Assert.Contains("paused", lines[1], StringComparison.Ordinal);
        Assert.Contains("running", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Format_of_an_empty_list_says_how_to_add_one()
    {
        Assert.Contains("time-arr add", Cli.Format(new TimerCommand.List(), new CommandReply([], T0)), StringComparison.Ordinal);
    }

    [Fact]
    public void Format_with_a_null_reply_is_a_bug()
    {
        Assert.Throws<ArgumentNullException>(() => Cli.Format(new TimerCommand.List(), null!));
    }
}
