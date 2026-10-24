using TimeArr.Commands;
using TimeArr.Core;

namespace TimeArr.Tests.Commands;

public class CliTests
{
    private static TimerCommand Command(params string[] args)
    {
        var parsed = Cli.Parse(args);
        Assert.True(parsed.IsSuccess, parsed.ToString());
        return parsed.Value.Command!;
    }

    private static Error Failure(params string[] args)
    {
        var parsed = Cli.Parse(args);
        Assert.True(parsed.IsFailure);
        return parsed.Error;
    }

    [Fact]
    public void No_arguments_shows_the_window()
    {
        Assert.Equal(new CliRequest(null, ShowWindow: true), Cli.Parse([]).Value);
        Assert.Equal(new CliRequest(null, ShowWindow: true), Cli.Parse(["show"]).Value);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help(string arg)
    {
        Assert.True(Cli.Parse([arg]).Value.Help);
    }

    [Fact]
    public void Verbs_without_a_timer_mean_the_default_timer()
    {
        Assert.Equal(new TimerCommand.Start(new TimerRef.Default()), Command("start"));
        Assert.Equal(new TimerCommand.Pause(new TimerRef.Default()), Command("pause"));
        Assert.Equal(new TimerCommand.Reset(new TimerRef.Default()), Command("reset"));
        Assert.Equal(new TimerCommand.Get(new TimerRef.Default()), Command("status"));
    }

    [Fact]
    public void A_name_is_always_a_name_even_digits()
    {
        Assert.Equal(new TimerCommand.Start(new TimerRef.ByName("42")), Command("start", "42"));
        Assert.Equal(new TimerCommand.Start(new TimerRef.ByName("deep work")), Command("start", "deep work"));
    }

    [Fact]
    public void Ids_only_come_with_dash_dash_id()
    {
        Assert.Equal(new TimerCommand.Start(new TimerRef.ById(42)), Command("start", "--id", "42"));
        Assert.Equal(new TimerCommand.Rename(new TimerRef.ById(3), "main"), Command("rename", "--id", "3", "main"));
        Assert.Equal(new TimerCommand.Delete(new TimerRef.ById(7)), Command("delete", "--id", "7"));
    }

    [Theory]
    [InlineData("+42")]
    [InlineData(" 42")]
    [InlineData("4 2")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0x2a")]
    [InlineData("99999999999999999999")]
    [InlineData("٤٢")] // Arabic-Indic 42
    public void Bad_ids_are_rejected(string id)
    {
        Assert.Equal(ErrorCode.InvalidInput, Failure("start", "--id", id).Code);
    }

    [Fact]
    public void Add_rename_delete()
    {
        Assert.Equal(new TimerCommand.Add("work"), Command("add", "work"));
        Assert.Equal(new TimerCommand.Rename(new TimerRef.ByName("work"), "play"), Command("rename", "work", "play"));
        Assert.Equal(new TimerCommand.Delete(new TimerRef.ByName("work")), Command("delete", "work"));
        Assert.Equal(new TimerCommand.List(), Command("list"));
    }

    [Theory]
    [InlineData("delete")]                        // delete needs an explicit timer
    [InlineData("add")]
    [InlineData("add", "a", "b")]
    [InlineData("rename", "only-one")]
    [InlineData("rename", "--id", "3")]
    [InlineData("start", "a", "b")]
    [InlineData("start", "--id")]
    [InlineData("start", "--name", "x")]
    [InlineData("list", "extra")]
    [InlineData("frobnicate")]
    public void Malformed_command_lines_are_invalid_input(params string[] args)
    {
        Assert.Equal(ErrorCode.InvalidInput, Failure(args).Code);
    }

    [Fact]
    public void Too_many_or_too_long_arguments_are_rejected()
    {
        Assert.Equal(ErrorCode.InvalidInput, Failure(Enumerable.Repeat("x", Cli.MaxArgs + 1).ToArray()).Code);
        Assert.Equal(ErrorCode.InvalidInput, Failure("add", new string('x', Cli.MaxArgLength + 1)).Code);
    }

    [Fact]
    public void Output_lists_timers_with_elapsed_time()
    {
        var now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var running = TimerState.Zero.Start(now.AddSeconds(-65));
        var reply = new CommandReply([new TimerEntry(1, "default", TimerState.Zero), new TimerEntry(2, "work", running)], now);

        var text = Cli.Format(new TimerCommand.List(), reply);

        Assert.Equal(
            "   1  paused          0:00:00  default" + Environment.NewLine +
            "   2  running         0:01:05  work", text);
        Assert.Equal("Deleted 'work' (id 2).", Cli.Format(new TimerCommand.Delete(new TimerRef.ById(2)), reply with { Timers = [reply.Timers[1]] }));
        Assert.Equal("No timers. Add one with: time-arr add <name>", Cli.Format(new TimerCommand.List(), reply with { Timers = [] }));
    }
}
