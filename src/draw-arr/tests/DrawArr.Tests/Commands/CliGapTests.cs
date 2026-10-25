using DrawArr.Commands;
using DrawArr.Core;

namespace DrawArr.Tests.Commands;

/// <summary>Grammar edges and refusals of <see cref="Cli"/> not covered by <see cref="CliTests"/>.</summary>
public sealed class CliGapTests
{
    private static CliCall Parsed(params string[] args)
    {
        var parsed = Cli.Parse(args);
        Assert.True(parsed.IsSuccess, parsed.ToString());
        return parsed.Value;
    }

    private static DrawCommand Command(params string[] args) => Assert.IsType<CliRequest.Run>(Parsed(args).Request).Command;

    private static Error Refused(params string[] args)
    {
        var parsed = Cli.Parse(args);
        Assert.True(parsed.IsFailure, $"'{string.Join(' ', args)}' was accepted as {parsed}.");
        Assert.Equal(ErrorCode.InvalidInput, parsed.Error.Code);
        Assert.False(string.IsNullOrWhiteSpace(parsed.Error.Message));
        return parsed.Error;
    }

    private static string TextOf(params string[] args) => Assert.IsType<DrawCommand.SetText>(Command(args)).Text;

    // --- verbs, options and files ---

    [Theory]
    [InlineData("help", "x")]
    [InlineData("new", "a.xml")]
    [InlineData("list", "a.xml")]
    [InlineData("show", "a.xml", "b.xml")]
    [InlineData("history")]
    [InlineData("save")]
    [InlineData("undo")]
    [InlineData("redo", "a.xml", "1")]
    [InlineData("delete", "a.xml")]
    [InlineData("delete", "a.xml", "1", "2")]
    [InlineData("set-text", "a.xml", "1")]
    [InlineData("colour", "a.xml", "1")]
    [InlineData("move-end", "a.xml", "1", "start")]
    [InlineData("resize-box", "a.xml", "1", "0", "0", "10")]
    public void Wrong_argument_counts_are_refused_with_usage(params string[] args)
    {
        Assert.StartsWith("Usage: draw-arr", Refused(args).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_counts_only_as_the_first_argument()
    {
        Refused("list", "--agent");
        Refused("--agent", "--agent", "list");
        Refused("add-box", "a.xml", "0", "0", "10", "10", "--agent");
    }

    [Fact]
    public void Agent_alone_opens_a_new_drawing_as_the_agent()
    {
        var call = Parsed("--agent");
        Assert.IsType<CliRequest.NewDrawing>(call.Request);
        Assert.Equal(CommandSource.Agent, call.Source);
    }

    [Fact]
    public void Open_with_no_files_says_it_needs_one()
    {
        Assert.Contains("at least one file", Refused("open").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_named_like_a_verb_needs_open_and_a_dash_file_needs_a_path()
    {
        Assert.IsType<CliRequest.List>(Parsed("list").Request);
        Assert.Equal(["list"], Assert.IsType<CliRequest.Open>(Parsed("open", "list").Request).Files);
        Refused("open", "-plan.xml");
        Assert.Equal(["./-plan.xml"], Assert.IsType<CliRequest.Open>(Parsed("open", "./-plan.xml").Request).Files);
    }

    [Fact]
    public void Several_bare_files_open_together_in_order()
    {
        Assert.Equal(["b.xml", "a.xml", "b.xml"], Assert.IsType<CliRequest.Open>(Parsed("b.xml", "a.xml", "b.xml").Request).Files);
    }

    [Fact]
    public void Exactly_the_argument_limits_are_accepted()
    {
        Assert.IsType<CliRequest.Open>(Parsed([.. Enumerable.Repeat("a.xml", Cli.MaxArgs)]).Request);
        Assert.Equal(Cli.MaxArgLength, TextOf("set-text", "a.xml", "1", new string('x', Cli.MaxArgLength)).Length);
    }

    // --- numbers and ids ---

    [Theory]
    [InlineData("+5", 5)]
    [InlineData("-0", 0)]
    [InlineData("-2147483648", int.MinValue)]
    [InlineData("2147483647", int.MaxValue)]
    public void Millimetres_take_a_sign_and_any_int(string text, int expected)
    {
        Assert.Equal(expected, Assert.IsType<DrawCommand.MoveBox>(Command("move-box", "a.xml", "1", text, "0")).X);
    }

    [Theory]
    [InlineData("5.0")]
    [InlineData(" 5")]
    [InlineData("5 ")]
    [InlineData("5mm")]
    [InlineData("0x10")]
    [InlineData("1e3")]
    [InlineData("2147483648")]
    [InlineData("")]
    [InlineData("--5")]
    public void Millimetres_that_arent_plain_whole_numbers_are_refused(string text)
    {
        Assert.Contains($"'{text}'", Refused("move-box", "a.xml", "1", text, "0").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Digits_from_other_scripts_are_refused()
    {
        // Fullwidth 5 and Arabic-Indic 5.
        Refused("move-box", "a.xml", "1", ((char)0xFF15).ToString(), "0");
        Refused("move-box", "a.xml", "1", ((char)0x0665).ToString(), "0");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData("")]
    public void Ids_must_be_plain_numbers_from_one(string id)
    {
        Refused("delete", "a.xml", id);
    }

    [Fact]
    public void The_largest_id_is_accepted_and_one_more_is_refused()
    {
        Assert.Equal(Drawing.MaxId, Assert.IsType<DrawCommand.Delete>(Command("delete", "a.xml", Drawing.MaxId.ToString(System.Globalization.CultureInfo.InvariantCulture))).Id);
        Refused("delete", "a.xml", (Drawing.MaxId + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_leading_zero_in_an_id_is_read_as_the_number()
    {
        Assert.Equal(7, Assert.IsType<DrawCommand.Delete>(Command("delete", "a.xml", "007")).Id);
    }

    // --- colours and options ---

    [Fact]
    public void Colour_names_are_lower_case_and_the_message_lists_them()
    {
        var error = Refused("colour", "a.xml", "1", "Red");
        Assert.Contains("white, red, yellow, blue, green", error.Message, StringComparison.Ordinal);
        Assert.Equal(Colour.Red, Assert.IsType<DrawCommand.SetColour>(Command("color", "a.xml", "1", "red")).Colour);
    }

    [Fact]
    public void Add_box_options_may_come_in_any_order_and_the_last_one_wins()
    {
        var box = Assert.IsType<DrawCommand.AddBox>(Command("add-box", "a.xml", "0", "0", "10", "10",
            "--text", "first", "--color", "blue", "--text", "second", "--colour", "green"));

        Assert.Equal(("second", Colour.Green), (box.Text, box.Colour));
    }

    [Theory]
    [InlineData("add-box", "a.xml", "0", "0", "10", "10", "--text")]
    [InlineData("add-box", "a.xml", "0", "0", "10", "10", "--colour")]
    [InlineData("add-box", "a.xml", "0", "0", "10", "10", "--size", "3")]
    [InlineData("add-box", "a.xml", "--text", "x", "0", "0", "10", "10")]
    [InlineData("add-line", "a.xml", "--colour", "red", "0,0", "5,5")]
    [InlineData("add-line", "a.xml", "0,0", "5,5", "--colour")]
    [InlineData("add-line", "a.xml", "0,0", "5,5", "--text", "x")]
    [InlineData("add-line", "a.xml", "0,0", "5,5", "--colour", "red", "--colour", "blue")]
    public void Misplaced_or_unknown_options_are_refused(params string[] args)
    {
        Refused(args);
    }

    // --- line ends ---

    [Theory]
    [InlineData("box:")]
    [InlineData("box:1@")]
    [InlineData("box:@1,2")]
    [InlineData("box:1@1,2,3")]
    [InlineData("box:1@@1,2")]
    [InlineData("BOX:1")]
    [InlineData("1,2,3")]
    [InlineData(",")]
    [InlineData("1,")]
    [InlineData("1;2")]
    [InlineData("box:0")]
    public void Malformed_line_ends_are_refused(string end)
    {
        Refused("add-line", "a.xml", end, "5,5");
    }

    [Fact]
    public void Line_ends_parse_to_the_three_kinds()
    {
        var line = Assert.IsType<DrawCommand.AddLine>(Command("add-line", "a.xml", "box:3@-5,+10", "-1,2"));

        Assert.Equal(new EndAt.OnBox(3, -5, 10), line.Start);
        Assert.Equal(new EndAt.Free(-1, 2), line.End);
    }

    // --- text escapes ---

    [Theory]
    [InlineData(@"a\nb", "a\nb")]
    [InlineData(@"a\\nb", @"a\nb")]
    [InlineData(@"a\\\nb", "a\\\nb")]
    [InlineData(@"a\tb", @"a\tb")]
    [InlineData(@"ends in \", @"ends in \")]
    [InlineData(@"\\", @"\")]
    [InlineData("", "")]
    public void Backslash_n_is_a_line_break_and_a_doubled_backslash_is_one_backslash(string typed, string text)
    {
        Assert.Equal(text, TextOf("set-text", "a.xml", "1", typed));
    }

    [Fact]
    public void A_nul_in_the_text_is_kept_for_the_model_to_refuse_not_turned_into_a_backslash()
    {
        Assert.Equal("a\0b", TextOf("set-text", "a.xml", "1", "a\0b"));
    }

    // --- printing drawings back ---

    [Fact]
    public void Show_tells_a_line_break_from_a_typed_backslash_n()
    {
        var withBreak = Drawing.Empty.With(Make.Box(1, 0, 0, 10, 10, Colour.White, "a\nb"));
        var withBackslash = Drawing.Empty.With(Make.Box(1, 0, 0, 10, 10, Colour.White, @"a\nb"));

        // An agent reads show's output back, so it must be able to tell these apart, and set-text already treats \\ as one backslash.
        Assert.NotEqual(Cli.Describe(withBreak), Cli.Describe(withBackslash));
    }

    [Fact]
    public void Show_tells_where_a_text_with_quotes_ends()
    {
        var quoted = Drawing.Empty.With(Make.Box(1, 0, 0, 10, 10, Colour.White, "say \" white"));
        var plain = Drawing.Empty.With(Make.Box(1, 0, 0, 10, 10, Colour.White, "say "));

        Assert.False(Cli.Describe(quoted).StartsWith(Cli.Describe(plain), StringComparison.Ordinal),
            $"'{Cli.Describe(quoted)}' reads as a box with text \"say \" followed by more words.");
    }

    [Fact]
    public void Show_of_an_empty_drawing_says_so()
    {
        Assert.Equal("The drawing is empty.", Cli.Describe(Drawing.Empty));
    }

    [Fact]
    public void The_usage_mentions_every_verb_the_parser_takes()
    {
        foreach (var verb in new[] { "new", "open", "list", "show", "history", "add-box", "move-box", "resize-box", "set-text", "colour", "add-line", "move-end", "delete", "undo", "redo", "save", "help", "--agent" })
            Assert.Contains(verb, Cli.Usage, StringComparison.Ordinal);
    }
}
