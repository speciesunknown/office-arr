using DrawArr.Core;

namespace DrawArr.Tests.Core;

public sealed class GridTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 5)]
    [InlineData(-2, 0)]
    [InlineData(-3, -5)]
    [InlineData(12, 10)]
    [InlineData(-13, -15)]
    public void Snap_goes_to_the_nearest_grid_line(int mm, int expected) => Assert.Equal(expected, Grid.Snap(mm));

    [Theory]
    [InlineData(2.4, 0)]
    [InlineData(2.5, 5)]
    [InlineData(-2.5, -5)]
    [InlineData(1e12, Grid.Limit)]
    [InlineData(-1e12, -Grid.Limit)]
    public void Snapping_a_pointer_position_rounds_and_clamps(double mm, int expected) => Assert.Equal(expected, Grid.Snap(mm));

    [Fact]
    public void A_point_outside_the_limits_is_an_input_error()
    {
        var point = Grid.Point(Grid.Limit + 1, 0);
        Assert.Equal(ErrorCode.InvalidInput, point.Error.Code);
    }

    [Fact]
    public void A_rect_is_snapped()
    {
        Assert.Equal(new Rect(10, -5, 20, 15), Grid.Rect(11, -6, 22, 13).Value);
    }

    [Theory]
    [InlineData(2, 10)]
    [InlineData(10, 0)]
    [InlineData(-5, 10)]
    [InlineData(10, Grid.MaxSize + 1)]
    public void A_box_too_small_or_too_big_is_an_input_error(int width, int height)
    {
        Assert.Equal(ErrorCode.InvalidInput, Grid.Rect(0, 0, width, height).Error.Code);
    }

    [Fact]
    public void Sizes_are_snapped_before_they_are_checked()
    {
        Assert.Equal(new Rect(0, 0, 5, 5), Grid.Rect(0, 0, 3, 3).Value);
    }

    [Fact]
    public void A_box_must_fit_inside_the_limits()
    {
        Assert.True(Grid.Rect(Grid.Limit - 10, 0, 10, 10).IsSuccess);
        Assert.Equal(ErrorCode.InvalidInput, Grid.Rect(Grid.Limit - 10, 0, 15, 10).Error.Code);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Snapping_extreme_values_does_not_overflow(int mm)
    {
        Assert.Equal(Math.Sign(mm), Math.Sign(Grid.Snap(mm)));
    }
}
