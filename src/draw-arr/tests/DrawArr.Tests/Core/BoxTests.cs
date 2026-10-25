using DrawArr.Core;

namespace DrawArr.Tests.Core;

public sealed class BoxTests
{
    private static readonly Box Box = Make.Box(1, 0, 0, 40, 20);

    [Fact]
    public void Points_on_edges_are_measured_from_the_top_or_left_end()
    {
        Assert.Equal(new Point(10, 0), Box.On(Edge.Top, 10));
        Assert.Equal(new Point(10, 20), Box.On(Edge.Bottom, 10));
        Assert.Equal(new Point(0, 15), Box.On(Edge.Left, 15));
        Assert.Equal(new Point(40, 15), Box.On(Edge.Right, 15));
    }

    [Fact]
    public void An_offset_past_the_end_of_an_edge_is_clamped_to_its_corner()
    {
        Assert.Equal(new Point(0, 20), Box.On(Edge.Left, 500));
    }

    [Theory]
    [InlineData(20, -30, Edge.Top, 20)]
    [InlineData(60, 10, Edge.Right, 10)]
    [InlineData(-10, 5, Edge.Left, 5)]
    [InlineData(25, 50, Edge.Bottom, 25)]
    [InlineData(100, -100, Edge.Top, 40)]
    public void Nearest_finds_the_closest_place_on_the_outline(int x, int y, Edge edge, int offset)
    {
        Assert.Equal(Make.On(1, edge, offset), Box.Nearest(new Point(x, y)));
    }

    [Fact]
    public void Text_with_windows_line_breaks_is_normalised()
    {
        Assert.Equal("a\nb\nc", BoxText.Validate("a\r\nb\rc").Value);
    }

    [Fact]
    public void Text_with_control_characters_is_an_input_error()
    {
        Assert.Equal(ErrorCode.InvalidInput, BoxText.Validate("a\u001b[31m").Error.Code);
    }

    [Fact]
    public void Text_longer_than_the_limit_is_an_input_error()
    {
        Assert.True(BoxText.Validate(new string('x', BoxText.MaxLength)).IsSuccess);
        Assert.Equal(ErrorCode.InvalidInput, BoxText.Validate(new string('x', BoxText.MaxLength + 1)).Error.Code);
    }

    [Fact]
    public void Text_with_broken_characters_is_an_input_error()
    {
        // Built here rather than in InlineData, which can't carry lone surrogates through to the test.
        string[] broken = ["a" + '\uD800', '\uDC00' + "b", new string(['\uD800', '\uD800', '\uDC00']), "\uFFFF", "\uFFFE"];
        foreach (var text in broken)
            Assert.Equal(ErrorCode.InvalidInput, BoxText.Validate(text).Error.Code);
    }

    [Fact]
    public void Unicode_line_and_paragraph_separators_become_newlines()
    {
        Assert.Equal("a\nb\nc", BoxText.Validate("a\u2028b\u2029c").Value);
    }

    [Fact]
    public void A_box_can_only_be_made_valid()
    {
        Assert.Equal(ErrorCode.InvalidInput, Box.Create(0, new Rect(0, 0, 5, 5), Colour.White, "").Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, Box.Create(1, new Rect(0, 0, 0, 5), Colour.White, "").Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, Box.Create(1, new Rect(0, 0, 5, -5), Colour.White, "").Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, Box.Create(1, new Rect(0, 0, 5, 5), 0, "").Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, Box.Create(1, new Rect(0, 0, 5, 5), Colour.White, "a\r\nb").Error.Code);
    }

    [Fact]
    public void The_grid_is_only_for_commands_so_the_model_takes_any_whole_millimetre()
    {
        Assert.Equal(new Rect(3, -7, 1, 13), Box.Create(1, new Rect(3, -7, 1, 13), Colour.White, "").Value.Bounds);
        Assert.Equal(3, LineEnd.Attached.Create(1, Edge.Top, 3).Value.Offset);
        Assert.Equal(new Point(1, -2), LineEnd.Free.Create(new Point(1, -2)).Value.At);
    }

    [Fact]
    public void Line_ends_can_only_be_made_valid()
    {
        Assert.Equal(ErrorCode.InvalidInput, LineEnd.Attached.Create(1, 0, 5).Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, LineEnd.Attached.Create(1, Edge.Top, -5).Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, LineEnd.Attached.Create(0, Edge.Top, 5).Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, LineEnd.Free.Create(new Point(0, Grid.Limit + 5)).Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, Line.Create(1, Make.Free(0, 0), Make.Free(5, 5), (Colour)6).Error.Code);
    }

    [Fact]
    public void Text_with_emoji_is_fine()
    {
        Assert.Equal("ok \U0001F600", BoxText.Validate("ok \U0001F600").Value);
    }
}
