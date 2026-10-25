using System.Text;
using DrawArr.Core;
using DrawArr.Files;

namespace DrawArr.Tests.Files;

/// <summary>Round trips and reader edge cases for <see cref="DrawingXml"/> not covered by <see cref="DrawingXmlTests"/>.</summary>
public sealed class XmlGapTests
{
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);

    private static byte[] Bytes(Drawing drawing)
    {
        using var stream = new MemoryStream();
        DrawingXml.Write(drawing, stream);
        return stream.ToArray();
    }

    private static Result<Drawing> Read(string xml) => DrawingXml.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    private static Result<Drawing> Read(byte[] bytes)
    {
        Result<Drawing>? read = null;
        Assert.Null(Record.Exception(() => read = DrawingXml.Read(new MemoryStream(bytes))));
        return read!;
    }

    private static Drawing RoundTrip(Drawing drawing)
    {
        var read = Read(Bytes(drawing));
        Assert.True(read.IsSuccess, read.ToString());
        return read.Value;
    }

    private static void AssertSame(Drawing expected, Drawing actual)
    {
        Assert.Equal(expected.Boxes.Values, actual.Boxes.Values);
        Assert.Equal(expected.Lines.Values, actual.Lines.Values);
    }

    private static string Wrap(string body) => $"""<drawing xmlns="{DrawingXml.Namespace}" version="1">{body}</drawing>""";

    // --- round trips ---

    public static TheoryData<string> Texts => new()
    {
        "two\nlines",
        "\n",
        "\n\nblank lines around\n\n",
        "  leading and trailing spaces  ",
        "&amp; <tag> \"quotes\" 'apostrophes' ]]> &#10;",
        "x",
    };

    [Theory]
    [MemberData(nameof(Texts))]
    public void Box_text_survives_a_round_trip(string text)
    {
        var drawing = Drawing.Empty.With(Make.Box(1, 0, 0, 5, 5, Colour.White, text));

        Assert.Equal(text, RoundTrip(drawing).Boxes[1].Text);
    }

    [Fact]
    public void Text_outside_the_basic_plane_and_at_the_length_limit_survives()
    {
        var emoji = string.Concat(Enumerable.Repeat(Emoji, BoxText.MaxLength / 2));
        var drawing = Drawing.Empty
            .With(Make.Box(1, 0, 0, 5, 5, Colour.White, emoji))
            .With(Make.Box(2, 0, 0, 5, 5, Colour.White, new string('&', BoxText.MaxLength)));

        AssertSame(drawing, RoundTrip(drawing));
    }

    [Fact]
    public void Extremes_of_every_value_survive_a_round_trip()
    {
        var drawing = Drawing.Empty
            .With(Make.Box(Drawing.MaxId, -Grid.Limit, -Grid.Limit, Grid.MaxSize, Grid.MaxSize, Colour.Green))
            .With(Make.Box(2, Grid.Limit - 5, Grid.Limit - 5, 5, 5, Colour.Blue))
            .With(Make.Line(3, Make.On(Drawing.MaxId, Edge.Right, Grid.MaxSize), Make.On(2, Edge.Bottom, Grid.MaxSize), Colour.Yellow))
            .With(Make.Line(Drawing.MaxId - 1, Make.Free(-Grid.Limit, Grid.Limit), Make.Free(Grid.Limit, -Grid.Limit), Colour.Red));

        AssertSame(drawing, RoundTrip(drawing));
    }

    [Fact]
    public void Every_colour_and_edge_survives_a_round_trip()
    {
        var drawing = Drawing.Empty.With(Make.Box(1, 0, 0, 50, 50));
        long id = 2;
        foreach (var colour in Enum.GetValues<Colour>())
        {
            foreach (var edge in Enum.GetValues<Edge>())
                drawing = drawing.With(Make.Line(id++, Make.On(1, edge, 25), Make.Free(0, 0), colour));
        }

        AssertSame(drawing, RoundTrip(drawing));
    }

    [Fact]
    public void Writing_twice_gives_the_same_bytes()
    {
        var drawing = Drawing.Empty.With(Make.Box(1, 0, 0, 5, 5, Colour.Red, "a\nb")).With(Make.Line(2, Make.On(1, Edge.Top, 0), Make.Free(10, 10)));

        Assert.Equal(Bytes(drawing), Bytes(RoundTrip(drawing)));
    }

    [Fact]
    public void A_full_drawing_with_the_longest_texts_can_be_loaded_again()
    {
        // 10,000 boxes of 1000 ampersands are a valid drawing, but each '&' is written as "&amp;", which is 50 million characters on disk.
        var drawing = Drawing.Empty;
        var text = new string('&', BoxText.MaxLength);
        for (var id = 1; id <= Drawing.MaxItems; id++)
            drawing = drawing.With(Make.Box(id, 0, 0, 5, 5, Colour.White, text));
        var bytes = Bytes(drawing);

        var read = Read(bytes);

        Assert.True(read.IsSuccess, $"A {bytes.Length / (1024 * 1024)} MB file of a valid drawing won't load: {read}");
    }

    [Fact]
    public void A_full_drawing_of_emoji_texts_fits_the_file_size_limit()
    {
        // 500 emoji are 1000 UTF-16 units (the text limit) but 2000 UTF-8 bytes, so 10,000 such boxes are about 20 MB.
        var drawing = Drawing.Empty;
        var text = string.Concat(Enumerable.Repeat(Emoji, BoxText.MaxLength / 2));
        for (var id = 1; id <= Drawing.MaxItems; id++)
            drawing = drawing.With(Make.Box(id, 0, 0, 5, 5, Colour.White, text));

        var size = Bytes(drawing).LongLength;

        Assert.True(size <= DrawingFile.MaxBytes, $"A valid drawing saves to {size} bytes, over the {DrawingFile.MaxBytes} byte load limit.");
    }

    // --- reading hand-written files ---

    [Fact]
    public void Unknown_attributes_are_ignored()
    {
        var read = Read(Wrap("""<box id="1" x="0" y="0" width="5" height="5" colour="white" shadow="yes"/>"""));

        Assert.True(read.IsSuccess, read.ToString());
    }

    [Fact]
    public void A_literal_line_break_in_a_text_attribute_reads_as_a_space()
    {
        // Pins XML attribute normalisation: only &#10; gives a line break, which is what Write produces.
        var read = Read(Wrap("<box id=\"1\" x=\"0\" y=\"0\" width=\"5\" height=\"5\" colour=\"white\" text=\"a\nb\"/>"));

        Assert.Equal("a b", read.Value.Boxes[1].Text);
    }

    [Fact]
    public void Carriage_returns_and_line_separators_in_a_file_read_as_newlines()
    {
        var read = Read(Wrap("""<box id="1" x="0" y="0" width="5" height="5" colour="white" text="a&#13;b&#13;&#10;c&#x2028;d"/>"""));

        Assert.Equal("a\nb\nc\nd", read.Value.Boxes[1].Text);
    }

    [Fact]
    public void Items_may_come_in_any_order_and_whitespace_and_comments_are_ignored()
    {
        var xml = Wrap("""

            <!-- a comment -->
            <line id="9" colour="red"><start box="7" edge="left" offset="5"/><end x="0" y="0"/></line>
            <?pi ignored?>
            <box id="7" x="10" y="10" width="20" height="20" colour="blue"/>

            """);

        var read = Read(xml);

        Assert.True(read.IsSuccess, read.ToString());
        Assert.Equal(new Point(10, 15), read.Value.Resolve(read.Value.Lines[9].Start));
    }

    [Fact]
    public void A_file_in_utf16_reads()
    {
        var xml = "<?xml version=\"1.0\" encoding=\"utf-16\"?>" + Wrap("""<box id="1" x="0" y="0" width="5" height="5" colour="white" text="ok"/>""");
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();

        Assert.Equal("ok", Read(bytes).Value.Boxes[1].Text);
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\" encoding=\"x-no-such-encoding\"?>")]
    [InlineData("<?xml version=\"1.1\"?>")]
    public void Odd_declarations_are_refused_as_invalid_files_not_exceptions(string declaration)
    {
        var read = Read(Encoding.UTF8.GetBytes(declaration + Wrap("")));

        Assert.True(read.IsFailure);
        Assert.Equal(ErrorCode.FileInvalid, read.Error.Code);
    }

    [Fact]
    public void Bytes_that_are_not_utf8_are_refused_as_invalid()
    {
        var bytes = Encoding.UTF8.GetBytes(Wrap("""<box id="1" x="0" y="0" width="5" height="5" colour="white" text="XX"/>"""));
        var at = Array.IndexOf(bytes, (byte)'X');
        bytes[at] = 0xC3;
        bytes[at + 1] = 0x28;

        Assert.Equal(ErrorCode.FileInvalid, Read(bytes).Error.Code);
    }

    [Theory]
    [InlineData("""<box id="1" x="0" y="0" width="5" height="5" colour="white" text="ab&#xD800;"/>""")]
    [InlineData("""<box id="1" x="0" y="0" width="5" height="5" colour="white" text="a&#xFFFE;b"/>""")]
    public void Characters_xml_forbids_are_refused(string body)
    {
        Assert.Equal(ErrorCode.FileInvalid, Read(Wrap(body)).Error.Code);
    }

    [Fact]
    public void Text_one_character_over_the_limit_is_refused()
    {
        var read = Read(Wrap($"""<box id="1" x="0" y="0" width="5" height="5" colour="white" text="{new string('x', BoxText.MaxLength + 1)}"/>"""));

        Assert.Equal(ErrorCode.FileInvalid, read.Error.Code);
    }

    [Fact]
    public void A_line_attached_to_a_line_is_refused()
    {
        var read = Read(Wrap("""
            <line id="1" colour="red"><start x="0" y="0"/><end x="5" y="0"/></line>
            <line id="2" colour="red"><start box="1" edge="top" offset="0"/><end x="5" y="0"/></line>
            """));

        Assert.Equal(ErrorCode.FileInvalid, read.Error.Code);
        Assert.StartsWith("Line 2:", read.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exactly_the_item_cap_reads()
    {
        var body = new StringBuilder();
        for (var id = 1; id <= Drawing.MaxItems; id++)
            body.Append(System.Globalization.CultureInfo.InvariantCulture, $"""<box id="{id}" x="0" y="0" width="5" height="5" colour="white"/>""");

        Assert.Equal(Drawing.MaxItems, Read(Wrap(body.ToString())).Value.Count);
    }

    [Fact]
    public void A_negative_zero_or_signed_number_is_read_as_its_value()
    {
        // Pins the reader's leniency: "+5" and "-0" are accepted as 5 and 0.
        var read = Read(Wrap("""<box id="+1" x="-0" y="+5" width="5" height="5" colour="white"/>"""));

        Assert.Equal(new Rect(0, 5, 5, 5), read.Value.Boxes[1].Bounds);
    }
}
