using DrawArr.Core;

namespace DrawArr.Tests.Core;

public sealed class DrawingTests
{
    private static readonly Box Box = new(1, new Rect(0, 0, 40, 20), Colour.White, "");

    [Fact]
    public void An_attached_end_follows_its_box()
    {
        var end = new LineEnd.Attached(1, Edge.Right, 10);
        var drawing = Drawing.Empty.With(Box);
        Assert.Equal(new Point(40, 10), drawing.Resolve(end));

        var moved = drawing.With(Box with { Bounds = new Rect(100, 100, 40, 20) });
        Assert.Equal(new Point(140, 110), moved.Resolve(end));
    }

    [Fact]
    public void Deleting_a_box_frees_the_line_ends_on_it_where_they_were()
    {
        var drawing = Drawing.Empty.With(Box)
            .With(new Line(2, new LineEnd.Attached(1, Edge.Top, 15), new LineEnd.Free(new Point(-50, -50)), Colour.Red));

        var after = drawing.Without(1);

        Assert.Empty(after.Boxes);
        Assert.Equal(new LineEnd.Free(new Point(15, 0)), after.Lines[2].Start);
        Assert.Equal(new LineEnd.Free(new Point(-50, -50)), after.Lines[2].End);
    }

    [Fact]
    public void A_line_attached_to_a_missing_box_is_a_bug()
    {
        var line = new Line(2, new LineEnd.Attached(9, Edge.Top, 0), new LineEnd.Free(new Point(0, 0)), Colour.Red);
        Assert.Throws<InvalidOperationException>(() => Drawing.Empty.With(line));
    }

    [Fact]
    public void Max_id_covers_boxes_and_lines()
    {
        var drawing = Drawing.Empty.With(Box with { Id = 4 })
            .With(new Line(7, new LineEnd.Free(new Point(0, 0)), new LineEnd.Free(new Point(5, 5)), Colour.Blue));
        Assert.Equal(7, drawing.MaxId);
        Assert.Equal(0, Drawing.Empty.MaxId);
    }
}
