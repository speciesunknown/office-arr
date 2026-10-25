using DrawArr.Commands;
using DrawArr.Core;

namespace DrawArr.Tests.Commands;

public sealed class CliTests
{
    private static CliCall Parse(params string[] args)
    {
        var parsed = Cli.Parse(args);
        Assert.True(parsed.IsSuccess, parsed.ToString());
        return parsed.Value;
    }

    private static DrawCommand Command(params string[] args) => Assert.IsType<CliRequest.Run>(Parse(args).Request).Command;

    private static Error Fail(params string[] args) => Cli.Parse(args).Error;

    [Fact]
    public void Nothing_opens_a_new_drawing_and_bare_files_open_those_files()
    {
        Assert.IsType<CliRequest.NewDrawing>(Parse().Request);
        Assert.Equal(["a.xml", "b.xml"], Assert.IsType<CliRequest.Open>(Parse("a.xml", "b.xml").Request).Files);
        Assert.Equal(["a.xml"], Assert.IsType<CliRequest.Open>(Parse("open", "a.xml").Request).Files);
    }

    [Fact]
    public void Agent_marks_the_source_and_everything_else_is_the_cli()
    {
        Assert.Equal(CommandSource.Agent, Parse("--agent", "show", "a.xml").Source);
        Assert.Equal(CommandSource.Cli, Parse("show", "a.xml").Source);
    }

    [Fact]
    public void Edits_parse_into_commands_for_their_file()
    {
        var run = Assert.IsType<CliRequest.Run>(Parse("add-box", "a.xml", "10", "-5", "40", "20", "--colour", "red", "--text", "two\\nlines").Request);
        Assert.Equal("a.xml", run.File);
        Assert.Equal(new DrawCommand.AddBox(10, -5, 40, 20, Colour.Red, "two\nlines"), run.Command);
        Assert.Equal(new DrawCommand.MoveBox(3, 1, 2), Command("move-box", "a.xml", "3", "1", "2"));
        Assert.Equal(new DrawCommand.ResizeBox(3, 0, 0, 5, 5), Command("resize-box", "a.xml", "3", "0", "0", "5", "5"));
        Assert.Equal(new DrawCommand.SetText(3, "back\\slash"), Command("set-text", "a.xml", "3", "back\\\\slash"));
        Assert.Equal(new DrawCommand.SetColour(3, Colour.Green), Command("colour", "a.xml", "3", "green"));
        Assert.Equal(new DrawCommand.Delete(3), Command("delete", "a.xml", "3"));
        Assert.Equal(new DrawCommand.Undo(), Command("undo", "a.xml"));
        Assert.Equal(new DrawCommand.Redo(), Command("redo", "a.xml"));
    }

    [Fact]
    public void Line_ends_are_points_boxes_or_points_on_boxes()
    {
        Assert.Equal(new DrawCommand.AddLine(new EndAt.Free(1, -2), new EndAt.FacingOther(3), Colour.Blue),
            Command("add-line", "a.xml", "1,-2", "box:3", "--colour", "blue"));
        Assert.Equal(new DrawCommand.MoveLineEnd(4, LineEndName.End, new EndAt.OnBox(3, 10, 20)),
            Command("move-end", "a.xml", "4", "end", "box:3@10,20"));
    }

    [Theory]
    [InlineData("add-box", "a.xml", "1", "2", "3")]
    [InlineData("add-box", "a.xml", "1", "2", "3", "x")]
    [InlineData("add-box", "a.xml", "1", "2", "3", "4", "--colour", "purple")]
    [InlineData("move-box", "a.xml", "1", "2")]
    [InlineData("move-box", "a.xml", "1", "2", "99999999999")]
    [InlineData("add-line", "a.xml", "1,2", "3")]
    [InlineData("add-line", "a.xml", "1,2", "box:x")]
    [InlineData("move-end", "a.xml", "1", "middle", "1,2")]
    [InlineData("show")]
    [InlineData("--verbose")]
    [InlineData("a.xml", "--force")]
    public void Bad_command_lines_are_input_errors(params string[] args)
    {
        Assert.Equal(ErrorCode.InvalidInput, Fail(args).Code);
    }

    [Fact]
    public void Too_many_or_too_long_arguments_are_refused()
    {
        Assert.Equal(ErrorCode.InvalidInput, Fail([.. Enumerable.Repeat("a.xml", Cli.MaxArgs + 1)]).Code);
        Assert.Equal(ErrorCode.InvalidInput, Fail("set-text", "a.xml", "1", new string('x', Cli.MaxArgLength + 1)).Code);
    }

    [Fact]
    public void A_drawing_is_described_one_item_per_line()
    {
        var drawing = Drawing.Empty
            .With(Make.Box(1, 0, 0, 40, 20, Colour.Red, "a\nb"))
            .With(Make.Line(2, Make.On(1, Edge.Right, 10), Make.Free(100, 10), Colour.Blue));
        Assert.Equal("box 1 at (0, 0) 40x20 red text \"a\\nb\"\nline 2 from (40, 10) on box 1 to (100, 10) blue",
            Cli.Describe(drawing).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal("The drawing is empty.", Cli.Describe(Drawing.Empty));
    }

    [Fact]
    public void A_box_facing_the_other_end_attaches_where_it_faces()
    {
        var doc = new Document(Drawing.Empty.With(Make.Box(1, 0, 0, 40, 20)));
        var reply = doc.Execute(new DrawCommand.AddLine(new EndAt.FacingOther(1), new EndAt.Free(100, 10), Colour.White), CommandSource.Cli).Value;
        Assert.Equal(new Point(40, 10), reply.Drawing.Resolve(reply.Drawing.Lines[2].Start));
    }

    [Fact]
    public void Two_boxes_facing_each_other_attach_on_their_near_sides()
    {
        var doc = new Document(Drawing.Empty.With(Make.Box(1, 0, 0, 40, 20)).With(Make.Box(2, 100, 0, 40, 20)));
        var reply = doc.Execute(new DrawCommand.AddLine(new EndAt.FacingOther(1), new EndAt.FacingOther(2), Colour.White), CommandSource.Cli).Value;
        var line = reply.Drawing.Lines[3];
        Assert.Equal((Edge.Right, Edge.Left), (((LineEnd.Attached)line.Start).Edge, ((LineEnd.Attached)line.End).Edge));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("quote \" and backslash \\ and \\n literal")]
    [InlineData("two\nlines")]
    public void Escaped_text_reads_back_exactly(string text)
    {
        Assert.Equal(text, Cli.Unescape(Cli.Escape(text)));
    }

    [Fact]
    public void Unescape_keeps_backslashes_that_start_nothing()
    {
        Assert.Equal("C:\\temp\\x", Cli.Unescape("C:\\temp\\x"));
        Assert.Equal("a" + '\0' + "b", Cli.Unescape("a" + '\0' + "b"));
    }

    [Fact]
    public void Redo_with_nothing_to_redo_says_nothing_changed()
    {
        var reply = new DocumentReply(Drawing.Empty, new HistoryView([new HistoryEntry("Add box 1", CommandSource.Cli)], 1), 1, null, DocumentChange.None);
        Assert.Equal("Nothing changed.", Cli.Format(new CliRequest.Run("a.xml", new DrawCommand.Redo()), reply));
    }
}
