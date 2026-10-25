using DrawArr.Core;
using DrawArr.ViewModels;
using SkiaSharp;

namespace DrawArr.App.Canvas;

/// <summary>Everything the canvas shows at one moment, taken on the UI thread and drawn on the render thread.</summary>
public sealed record Scene(Drawing Drawing, ViewTransform View, long? SelectedId, GhostLine? GhostLine, GhostBox? GhostBox);

/// <summary>Draws a scene with SkiaSharp: white on black, a 5 mm grid, boxes with centred clipped text, and lines.</summary>
public static class SceneRenderer
{
    /// <summary>The text height in millimetres, so text scales with the drawing.</summary>
    public const float TextMm = 4;

    private static readonly SKColor Background = SKColors.Black;
    private static readonly SKColor MinorGrid = new(0x22, 0x22, 0x22);
    private static readonly SKColor MajorGrid = new(0x3A, 0x3A, 0x3A);
    private static readonly SKColor Axis = new(0x60, 0x60, 0x60);
    private static readonly SKColor Selection = new(0x80, 0xC0, 0xFF);

    /// <summary>The screen colour for a palette colour.</summary>
    public static SKColor ToSkia(Colour colour) => colour switch
    {
        Colour.White => SKColors.White,
        Colour.Red => new SKColor(0xFF, 0x3B, 0x30),
        Colour.Yellow => new SKColor(0xFF, 0xD6, 0x0A),
        Colour.Blue => new SKColor(0x0A, 0x84, 0xFF),
        Colour.Green => new SKColor(0x30, 0xD1, 0x58),
        _ => throw new InvalidOperationException($"Unhandled colour {colour}."),
    };

    /// <summary>Draws a scene filling a view of the given size in pixels.</summary>
    public static void Render(SKCanvas canvas, Scene scene, float width, float height)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(scene);
        canvas.Clear(Background);
        var view = scene.View;
        DrawGrid(canvas, view, width, height);

        // Pixel positions come from the view, so stroke widths and handles stay the same size at any zoom.
        SKPoint Px(Point p)
        {
            var (x, y) = view.ToPx(p.X, p.Y, width, height);
            return new SKPoint((float)x, (float)y);
        }

        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, (float)(TextMm * view.Scale));

        foreach (var box in scene.Drawing.Boxes.Values)
            DrawBox(canvas, box.Bounds, ToSkia(box.Colour), box.Text, box.Id == scene.SelectedId, Px, stroke, fill, font);

        foreach (var line in scene.Drawing.Lines.Values)
        {
            var a = Px(scene.Drawing.Resolve(line.Start));
            var b = Px(scene.Drawing.Resolve(line.End));
            stroke.Color = ToSkia(line.Colour);
            canvas.DrawLine(a, b, stroke);
            fill.Color = ToSkia(line.Colour);
            foreach (var (end, at) in new[] { (line.Start, a), (line.End, b) })
            {
                if (end is LineEnd.Attached)
                    canvas.DrawCircle(at, 3, fill);
            }
            if (line.Id == scene.SelectedId)
            {
                stroke.Color = Selection;
                canvas.DrawCircle(a, 5, stroke);
                canvas.DrawCircle(b, 5, stroke);
            }
        }

        if (scene.GhostBox is { } ghostBox)
            DrawBox(canvas, ghostBox.Bounds, ToSkia(ghostBox.Colour).WithAlpha(0x80), "", false, Px, stroke, fill, font);
        if (scene.GhostLine is { } ghostLine)
        {
            stroke.Color = ToSkia(ghostLine.Colour).WithAlpha(0x80);
            canvas.DrawLine(Px(ghostLine.From), Px(ghostLine.To), stroke);
        }
    }

    private static void DrawBox(SKCanvas canvas, Rect bounds, SKColor colour, string text, bool selected,
        Func<Point, SKPoint> px, SKPaint stroke, SKPaint fill, SKFont font)
    {
        var topLeft = px(new Point(bounds.X, bounds.Y));
        var bottomRight = px(new Point(bounds.Right, bounds.Bottom));
        var rect = new SKRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        stroke.Color = colour;
        canvas.DrawRect(rect, stroke);
        if (text.Length > 0)
        {
            canvas.Save();
            // The text is clipped to the box rather than growing it (plan 002).
            canvas.ClipRect(rect);
            fill.Color = colour;
            var lines = text.Split('\n');
            var lineHeight = font.Spacing;
            var top = rect.MidY - lineHeight * lines.Length / 2;
            font.GetFontMetrics(out var metrics);
            for (var i = 0; i < lines.Length; i++)
            {
                var baseline = top + lineHeight * i - metrics.Ascent;
                canvas.DrawText(lines[i], rect.MidX, baseline, SKTextAlign.Center, font, fill);
            }
            canvas.Restore();
        }
        if (selected)
        {
            stroke.Color = Selection;
            var outline = rect;
            outline.Inflate(3, 3);
            canvas.DrawRect(outline, stroke);
            fill.Color = Selection;
            canvas.DrawRect(new SKRect(rect.Right - 4, rect.Bottom - 4, rect.Right + 4, rect.Bottom + 4), fill);
        }
    }

    private static void DrawGrid(SKCanvas canvas, ViewTransform view, float width, float height)
    {
        var (left, top) = view.ToMm(0, 0, width, height);
        var (right, bottom) = view.ToMm(width, height, width, height);
        using var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        // Minor lines are left out when they would be closer than 6 pixels, which would only make the screen grey.
        foreach (var (step, colour) in new[] { (Grid.Step, MinorGrid), (Grid.Step * 10, MajorGrid) })
        {
            if (step * view.Scale < 6)
                continue;
            paint.Color = colour;
            for (var x = Math.Floor(left / step) * step; x <= right; x += step)
            {
                var px = (float)view.ToPx(x, 0, width, height).X;
                canvas.DrawLine(px, 0, px, height, paint);
            }
            for (var y = Math.Floor(top / step) * step; y <= bottom; y += step)
            {
                var py = (float)view.ToPx(0, y, width, height).Y;
                canvas.DrawLine(0, py, width, py, paint);
            }
        }
        paint.Color = Axis;
        var (ox, oy) = view.ToPx(0, 0, width, height);
        canvas.DrawLine((float)ox, 0, (float)ox, height, paint);
        canvas.DrawLine(0, (float)oy, width, (float)oy, paint);
    }
}
