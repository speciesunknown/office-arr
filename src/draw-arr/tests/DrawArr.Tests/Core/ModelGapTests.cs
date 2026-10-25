using DrawArr.Core;

namespace DrawArr.Tests.Core;

/// <summary>Edge cases for the grid, boxes and box text not covered by the existing core tests.</summary>
public sealed class ModelGapTests
{
    // --- the grid ---

    [Theory]
    [InlineData(Grid.Limit, Grid.Limit)]
    [InlineData(-Grid.Limit, -Grid.Limit)]
    [InlineData(Grid.Limit - 2, Grid.Limit)]
    [InlineData(-Grid.Limit + 2, -Grid.Limit)]
    public void Points_at_the_limits_are_accepted_and_snapped(int mm, int expected)
    {
        Assert.Equal(new Point(expected, expected), Grid.Point(mm, mm).Value);
    }

    [Theory]
    [InlineData(Grid.Limit + 1)]
    [InlineData(-Grid.Limit - 1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Points_past_the_limits_are_input_errors_even_if_they_would_snap_back(int mm)
    {
        Assert.Equal(ErrorCode.InvalidInput, Grid.Point(mm, 0).Error.Code);
        Assert.Equal(ErrorCode.InvalidInput, Grid.Point(0, mm).Error.Code);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(7, 5)]
    [InlineData(8, 10)]
    [InlineData(Grid.MaxSize, Grid.MaxSize)]
    public void Box_sizes_inside_the_limits_snap(int size, int expected)
    {
        // From the far corner, so even the largest size fits inside the limits.
        var rect = Grid.Rect(-Grid.Limit, -Grid.Limit, size, size).Value;

        Assert.Equal((expected, expected), (rect.Width, rect.Height));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_pointer_position_that_is_not_a_number_is_a_bug(double mm)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Grid.Snap(mm));
    }

    [Fact]
    public void A_box_may_not_reach_past_the_limits()
    {
        // Grid.Limit is "the furthest any coordinate may be from 0", but only the corner is checked, so the far edge can be twice as far.
        var rect = Grid.Rect(Grid.Limit, Grid.Limit, Grid.MaxSize, Grid.MaxSize);

        Assert.True(rect.IsFailure, $"Accepted {rect}, whose right and bottom edges are at {2 * Grid.Limit} mm.");
    }

    // --- boxes ---

    [Fact]
    public void Nearest_on_a_corner_picks_the_top_edge_at_its_end()
    {
        var box = Make.Box(1, 0, 0, 40, 20, Colour.White);

        Assert.Equal(Make.On(1, Edge.Top, 0), box.Nearest(new Point(0, 0)));
        Assert.Equal(Make.On(1, Edge.Top, 40), box.Nearest(new Point(40, 0)));
    }

    [Fact]
    public void Nearest_from_inside_the_box_picks_the_closest_edge()
    {
        var box = Make.Box(1, 0, 0, 100, 40, Colour.White);

        Assert.Equal(Make.On(1, Edge.Bottom, 50), box.Nearest(new Point(50, 35)));
        Assert.Equal(Make.On(1, Edge.Right, 20), box.Nearest(new Point(95, 20)));
    }

    [Fact]
    public void Nearest_always_lands_on_the_grid_and_on_the_outline()
    {
        var box = Make.Box(1, -35, 10, 65, 45, Colour.White);

        for (var x = -60; x <= 60; x += 5)
        {
            for (var y = -20; y <= 80; y += 5)
            {
                var end = box.Nearest(new Point(x, y));
                var at = box.On(end.Edge, end.Offset);
                Assert.Equal(0, end.Offset % Grid.Step);
                Assert.InRange(end.Offset, 0, box.Length(end.Edge));
                Assert.True(at.X == box.Bounds.X || at.X == box.Bounds.Right || at.Y == box.Bounds.Y || at.Y == box.Bounds.Bottom, $"{at} is not on {box.Bounds}.");
            }
        }
    }

    // --- box text ---

    [Fact]
    public void Text_of_exactly_the_maximum_length_is_accepted()
    {
        Assert.True(BoxText.Validate(new string('x', BoxText.MaxLength)).IsSuccess);
    }

    [Fact]
    public void The_length_limit_counts_line_breaks_after_normalising()
    {
        // 999 letters and one \r\n are 1001 characters in, 1000 out.
        var text = new string('x', BoxText.MaxLength - 1) + "\r\n";

        Assert.Equal(BoxText.MaxLength, BoxText.Validate(text).Value.Length);
    }

    [Theory]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\r\n\r\nb", "a\n\nb")]
    [InlineData("a\n\rb", "a\n\nb")]
    [InlineData("", "")]
    public void Line_breaks_are_normalised(string input, string expected)
    {
        Assert.Equal(expected, BoxText.Validate(input).Value);
    }

    [Theory]
    [InlineData("tab\there")]
    [InlineData("nul\0")]
    [InlineData("esc\u001b[31m")]
    [InlineData("del\u007f")]
    [InlineData("next\u0085line")]
    public void Control_characters_other_than_line_breaks_are_input_errors(string text)
    {
        Assert.Equal(ErrorCode.InvalidInput, BoxText.Validate(text).Error.Code);
    }

    [Fact]
    public void Null_text_is_an_input_error()
    {
        Assert.Equal(ErrorCode.InvalidInput, BoxText.Validate(null).Error.Code);
    }

    [Fact]
    public void Text_that_xml_cannot_hold_is_an_input_error()
    {
        // Milestone 2 saves box text in XML, which can't hold lone surrogates or the non-characters U+FFFE and U+FFFF.
        string[] cases = ["ab\uD800", "\uDC00cd", "a\uFFFEb", "a\uFFFFb"];

        foreach (var text in cases)
            Assert.True(BoxText.Validate(text).IsFailure, $"Accepted text with U+{(int)text.First(c => char.IsSurrogate(c) || c >= '\uFFFE'):X4}.");
    }

    [Fact]
    public void Text_outside_the_basic_plane_is_accepted()
    {
        Assert.Equal("\U0001F600 ok", BoxText.Validate("\U0001F600 ok").Value);
    }
}
