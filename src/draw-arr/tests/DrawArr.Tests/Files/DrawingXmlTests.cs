using System.Globalization;
using System.Text;
using DrawArr.Core;
using DrawArr.Files;

namespace DrawArr.Tests.Files;

public sealed class DrawingXmlTests
{
    private const string Ns = DrawingXml.Namespace;

    private static Result<Drawing> Read(string xml) => DrawingXml.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    private static string Doc(string body) => $"""<drawing xmlns="{Ns}" version="1">{body}</drawing>""";

    private static Drawing Sample() => Drawing.Empty
        .With(Make.Box(1, -20, -10, 40, 20, Colour.Red, "two\nlines & <angle> \"quotes\" \U0001F600 \u202Eabc"))
        .With(Make.Box(3, 100, 0, 5, 5, Colour.Blue))
        .With(Make.Line(2, Make.On(1, Edge.Right, 10), Make.Free(-100, 50), Colour.Yellow))
        .With(Make.Line(4, Make.On(3, Edge.Bottom, 500), Make.On(1, Edge.Top, 0), Colour.Green));

    private static Drawing RoundTrip(Drawing drawing)
    {
        var stream = new MemoryStream();
        DrawingXml.Write(drawing, stream);
        stream.Position = 0;
        var read = DrawingXml.Read(stream);
        Assert.True(read.IsSuccess, read.ToString());
        return read.Value;
    }

    [Fact]
    public void A_drawing_survives_writing_and_reading()
    {
        var drawing = Sample();
        var back = RoundTrip(drawing);
        Assert.Equal(drawing.Boxes.Values, back.Boxes.Values);
        Assert.Equal(drawing.Lines.Values, back.Lines.Values);
    }

    [Fact]
    public void An_empty_drawing_survives_writing_and_reading()
    {
        Assert.Equal(0, RoundTrip(Drawing.Empty).Count);
    }

    [Fact]
    public void Colours_and_edges_are_stored_by_name()
    {
        var stream = new MemoryStream();
        DrawingXml.Write(Sample(), stream);
        var xml = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("colour=\"red\"", xml, StringComparison.Ordinal);
        Assert.Contains("edge=\"right\"", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFEFF", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hand_written_file_reads()
    {
        var drawing = Read(Doc("""
            <!-- a comment -->
            <box id="7" x="0" y="0" width="10" height="10" colour="white" text="a&#10;b"/>
            <line id="9" colour="red"><start x="-5" y="5"/><end box="7" edge="left" offset="5"/></line>
            """)).Value;
        Assert.Equal("a\nb", drawing.Boxes[7].Text);
        Assert.Equal(new Point(0, 5), drawing.Resolve(drawing.Lines[9].End));
    }

    [Theory]
    [InlineData("""<!DOCTYPE drawing [<!ENTITY a "aaaaaaaaaa">]><drawing xmlns="urn:office-arr:draw-arr" version="1"/>""")]
    [InlineData("""<!DOCTYPE drawing SYSTEM "http://example.com/x.dtd"><drawing xmlns="urn:office-arr:draw-arr" version="1"/>""")]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE d [<!ENTITY x SYSTEM "file:///etc/passwd">]><drawing xmlns="urn:office-arr:draw-arr" version="1"><box id="1" x="0" y="0" width="5" height="5" colour="white" text="&x;"/></drawing>""")]
    [InlineData("not xml")]
    [InlineData("")]
    [InlineData("""<drawing xmlns="urn:office-arr:draw-arr" version="1"/><drawing xmlns="urn:office-arr:draw-arr" version="1"/>""")]
    [InlineData("""<drawing xmlns="urn:office-arr:draw-arr" version="1">""")]
    public void Dtds_entities_and_broken_xml_are_refused(string xml)
    {
        Assert.Equal(ErrorCode.FileInvalid, Read(xml).Error.Code);
    }

    [Fact]
    public void Deep_nesting_is_refused_without_recursing()
    {
        var deep = string.Concat(Enumerable.Repeat("<a>", 100_000)) + string.Concat(Enumerable.Repeat("</a>", 100_000));
        Assert.Equal(ErrorCode.FileInvalid, Read(Doc(deep)).Error.Code);
    }

    [Theory]
    [InlineData("""<drawing version="1"/>""", "isn't a draw-arr drawing")]
    [InlineData("""<other xmlns="urn:office-arr:draw-arr" version="1"/>""", "isn't a draw-arr drawing")]
    [InlineData("""<drawing xmlns="urn:office-arr:draw-arr" version="2"/>""", "newer draw-arr")]
    [InlineData("""<drawing xmlns="urn:office-arr:draw-arr"/>""", "no version")]
    [InlineData("""<drawing xmlns="urn:office-arr:draw-arr" version="01"/>""", "no version")]
    public void Files_that_are_not_version_1_drawings_are_refused(string xml, string message)
    {
        var error = Read(xml).Error;
        Assert.Equal(ErrorCode.FileInvalid, error.Code);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<box id="1" x="0" y="0" width="5" height="5" colour="white"/><box id="1" x="0" y="0" width="5" height="5" colour="white"/>""", "used twice")]
    [InlineData("""<box id="0" x="0" y="0" width="5" height="5" colour="white"/>""", "isn't between")]
    [InlineData("""<box id="9007199254740992" x="0" y="0" width="5" height="5" colour="white"/>""", "isn't between")]
    [InlineData("""<box id="99999999999999999999999" x="0" y="0" width="5" height="5" colour="white"/>""", "whole number")]
    [InlineData("""<box id="1" x="3" y="0" width="5" height="5" colour="white"/>""", "grid")]
    [InlineData("""<box id="1" x="0" y="0" width="-5" height="5" colour="white"/>""", "grid")]
    [InlineData("""<box id="1" x="99995" y="0" width="10" height="5" colour="white"/>""", "grid")]
    [InlineData("""<box id="1" x="5000000000" y="0" width="5" height="5" colour="white"/>""", "out of range")]
    [InlineData("""<box id="1" x="1e3" y="0" width="5" height="5" colour="white"/>""", "whole number")]
    [InlineData("""<box id="1" x=" 5" y="0" width="5" height="5" colour="white"/>""", "whole number")]
    [InlineData("""<box id="1" x="0" y="0" width="5" height="5"/>""", "needs a 'colour'")]
    [InlineData("""<box id="1" x="0" y="0" width="5" height="5" colour="0"/>""", "must be one of")]
    [InlineData("""<box id="1" x="0" y="0" width="5" height="5" colour="White"/>""", "must be one of")]
    [InlineData("""<box id="1" x="0" y="0" width="5" height="5" colour="white" text="a&#1;"/>""", "valid XML")]
    [InlineData("""<box id="1" x="0" y="0" width="5" height="5" colour="white" text="a&#x7F;"/>""", "control characters")]
    [InlineData("""<box id="1" x="0" y="0" width="5" height="5" colour="white">text</box>""", "anything inside")]
    [InlineData("""<box id="1" x="0" y="0" width="5" height="5" colour="white"><box/></box>""", "anything inside")]
    [InlineData("""<circle/>""", "isn't something draw-arr knows")]
    [InlineData("""<box xmlns="other" id="1" x="0" y="0" width="5" height="5" colour="white"/>""", "isn't something draw-arr knows")]
    [InlineData("""loose text""", "Only boxes and lines")]
    [InlineData("""<line id="1" colour="red"/>""", "needs a start and an end")]
    [InlineData("""<line id="1" colour="red"><start x="0" y="0"/></line>""", "end is missing")]
    [InlineData("""<line id="1" colour="red"><end x="0" y="0"/><start x="0" y="0"/></line>""", "start is missing")]
    [InlineData("""<line id="1" colour="red"><start x="0" y="0"/><end x="0" y="0"/><end x="0" y="0"/></line>""", "only have a start and an end")]
    [InlineData("""<line id="1" colour="red"><start x="1" y="0"/><end x="0" y="0"/></line>""", "grid")]
    [InlineData("""<line id="1" colour="red"><start box="5" edge="top" offset="0"/><end x="0" y="0"/></line>""", "isn't in the file")]
    [InlineData("""<box id="5" x="0" y="0" width="5" height="5" colour="white"/><line id="1" colour="red"><start box="5" edge="0" offset="0"/><end x="0" y="0"/></line>""", "must be one of")]
    [InlineData("""<box id="5" x="0" y="0" width="5" height="5" colour="white"/><line id="1" colour="red"><start box="5" edge="top" offset="-5"/><end x="0" y="0"/></line>""", "offset")]
    [InlineData("""<box id="5" x="0" y="0" width="5" height="5" colour="white"/><line id="5" colour="red"><start x="0" y="0"/><end x="0" y="0"/></line>""", "used twice")]
    public void Bad_items_are_refused_with_a_message_saying_what_is_wrong(string body, string message)
    {
        var error = Read(Doc(body)).Error;
        Assert.Equal(ErrorCode.FileInvalid, error.Code);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<box id="1" x="0" y="0" width="5" height="5" colour="white" txt="typo"/>""", "'txt'")]
    [InlineData("""<line id="1" colour="red" dashed="yes"><start x="0" y="0"/><end x="0" y="0"/></line>""", "'dashed'")]
    [InlineData("""<line id="1" colour="red"><start x="0" y="0" box2="1"/><end x="0" y="0"/></line>""", "'box2'")]
    [InlineData("""<box id="5" x="0" y="0" width="5" height="5" colour="white"/><line id="1" colour="red"><start box="5" edge="top" offset="0" x="1"/><end x="0" y="0"/></line>""", "'x'")]
    public void Unknown_attributes_are_refused_so_nothing_is_dropped_on_the_next_save(string body, string name)
    {
        var error = Read(Doc(body)).Error;
        Assert.Equal(ErrorCode.FileInvalid, error.Code);
        Assert.Contains(name, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<box id="1" x="0" y="0" width="5" height="5" colour="white" txt="typo"/>""", "for 'box'")]
    [InlineData("""<line id="1" colour="red" dashed="yes"><start x="0" y="0"/><end x="0" y="0"/></line>""", "for 'line'")]
    [InlineData("""<line id="1" colour="red"><start x="0" y="0"/><end x="0" y="0" z="1"/></line>""", "for 'end'")]
    public void Unknown_attribute_errors_name_the_element_they_are_on(string body, string where)
    {
        Assert.Contains(where, Read(Doc(body)).Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Attributes_in_other_namespaces_and_namespace_declarations_are_allowed()
    {
        var drawing = Read($"""<d:drawing xmlns:d="{Ns}" xmlns:o="urn:other" d:x="1" o:note="hi" version="1"><d:box id="1" x="0" y="0" width="5" height="5" colour="white" o:tag="t"/></d:drawing>""");
        Assert.Equal(1, drawing.Value.Count);
    }

    [Fact]
    public void A_newer_file_with_new_attributes_is_reported_as_newer()
    {
        Assert.Contains("newer", Read($"""<drawing xmlns="{Ns}" version="2" theme="dark"/>""").Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_attached_to_a_box_later_in_the_file_reads()
    {
        var drawing = Read(Doc("""
            <line id="1" colour="red"><start box="2" edge="top" offset="0"/><end x="0" y="0"/></line>
            <box id="2" x="0" y="0" width="5" height="5" colour="white"/>
            """)).Value;
        Assert.Equal(2, drawing.Count);
    }

    [Fact]
    public void An_offset_past_the_end_of_its_edge_is_kept()
    {
        var drawing = Read(Doc("""
            <box id="2" x="0" y="0" width="5" height="5" colour="white"/>
            <line id="1" colour="red"><start box="2" edge="top" offset="50"/><end x="0" y="0"/></line>
            """)).Value;
        Assert.Equal(50, ((LineEnd.Attached)drawing.Lines[1].Start).Offset);
        Assert.Equal(new Point(5, 0), drawing.Resolve(drawing.Lines[1].Start));
    }

    [Fact]
    public void Errors_say_which_line_of_the_file_is_wrong()
    {
        var error = Read($"<drawing xmlns=\"{Ns}\" version=\"1\">\n\n<circle/></drawing>").Error;
        Assert.StartsWith("Line 3:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_items_than_a_drawing_can_hold_are_refused()
    {
        var body = new StringBuilder();
        for (var i = 1; i <= Drawing.MaxItems + 1; i++)
            body.Append(CultureInfo.InvariantCulture, $"""<box id="{i}" x="0" y="0" width="5" height="5" colour="white"/>""");
        Assert.Contains("at most", Read(Doc(body.ToString())).Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_huge_attribute_is_refused()
    {
        var huge = new string('a', (int)DrawingXml.MaxCharacters + 10);
        Assert.Equal(ErrorCode.FileInvalid, Read(Doc($"""<box id="1" x="0" y="0" width="5" height="5" colour="white" text="{huge}"/>""")).Error.Code);
    }
}
