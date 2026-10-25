using DrawArr.Core;

namespace DrawArr.Tests.Core;

public sealed class DrawingTests
{
    private static readonly Box Box = Make.Box(1, 0, 0, 40, 20);

    [Fact]
    public void An_attached_end_follows_its_box()
    {
        var end = Make.On(1, Edge.Right, 10);
        var drawing = Drawing.Empty.With(Box);
        Assert.Equal(new Point(40, 10), drawing.Resolve(end));

        var moved = drawing.With(Make.Box(1, 100, 100, 40, 20));
        Assert.Equal(new Point(140, 110), moved.Resolve(end));
    }

    [Fact]
    public void Deleting_a_box_frees_the_line_ends_on_it_where_they_were()
    {
        var drawing = Drawing.Empty.With(Box)
            .With(Make.Line(2, Make.On(1, Edge.Top, 15), Make.Free(-50, -50), Colour.Red));

        var after = drawing.Without(1);

        Assert.Empty(after.Boxes);
        Assert.Equal(Make.Free(15, 0), after.Lines[2].Start);
        Assert.Equal(Make.Free(-50, -50), after.Lines[2].End);
    }

    [Fact]
    public void A_line_attached_to_a_missing_box_is_a_bug()
    {
        var line = Make.Line(2, Make.On(9, Edge.Top, 0), Make.Free(0, 0), Colour.Red);
        Assert.Throws<InvalidOperationException>(() => Drawing.Empty.With(line));
    }

    [Fact]
    public void Max_id_covers_boxes_and_lines()
    {
        var drawing = Drawing.Empty.With(Make.Box(4, 0, 0, 40, 20))
            .With(Make.Line(7, Make.Free(0, 0), Make.Free(5, 5), Colour.Blue));
        Assert.Equal(7, drawing.MaxId);
        Assert.Equal(0, Drawing.Empty.MaxId);
    }
}
