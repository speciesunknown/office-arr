using DrawArr.Core;

namespace DrawArr.Tests.Core;

public sealed class BoxTests
{
    private static readonly Box Box = new(1, new Rect(0, 0, 40, 20), Colour.White, "");

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
        Assert.Equal(new LineEnd.Attached(1, edge, offset), Box.Nearest(new Point(x, y)));
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
}
