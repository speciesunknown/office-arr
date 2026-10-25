using System.Diagnostics;
using DrawArr.App;
using DrawArr.App.Canvas;
using DrawArr.Core;
using DrawArr.ViewModels;
using SkiaSharp;

namespace DrawArr.UiTests;

/// <summary>The scene renderer on its own, drawing into an offscreen bitmap.</summary>
public sealed class RendererTests
{
    private static TimeSpan TimeToRender(Drawing drawing, ViewTransform view)
    {
        using var bitmap = new SKBitmap(1920, 1080);
        using var canvas = new SKCanvas(bitmap);
        var scene = new Scene(drawing, view, null, null, null);
        SceneRenderer.Render(canvas, scene, 1920, 1080);
        var timer = Stopwatch.StartNew();
        SceneRenderer.Render(canvas, scene, 1920, 1080);
        return timer.Elapsed;
    }

    /// <summary>A drawing as big as files allow, every box full of short lines, which once took seconds per frame.</summary>
    private static Drawing Heavy()
    {
        var text = string.Join("\n", Enumerable.Repeat("a", 500));
        var drawing = Drawing.Empty;
        for (var i = 1; i <= Drawing.MaxItems; i++)
            drawing = drawing.With(Box.Create(i, new Rect((i % 100) * 20 - 1000, (i / 100) * 20 - 1000, 15, 15), Colour.White, text).Value);
        return drawing;
    }

    [Theory]
    [InlineData(4.0)]
    [InlineData(ViewTransform.MaxScale)]
    [InlineData(ViewTransform.MinScale)]
    public void A_full_drawing_of_many_line_texts_draws_in_well_under_a_second(double scale)
    {
        var elapsed = TimeToRender(Heavy(), new ViewTransform(scale, 0, 0));
        Assert.True(elapsed < TimeSpan.FromMilliseconds(500), $"A frame took {elapsed.TotalMilliseconds} ms.");
    }

    [Fact]
    public void Printable_hides_format_characters_beyond_the_first_plane()
    {
        Assert.Equal("a??b?c", Printable.Text("a\U000E0041\U000E0042b\u202Ec"));
        Assert.Equal("line\nnext\ttab \U0001F600", Printable.Text("line\nnext\ttab \U0001F600"));
    }
}
