using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using DrawArr.ViewModels;

namespace DrawArr.App.Canvas;

/// <summary>Shows a <see cref="DrawingViewModel"/> with SkiaSharp and turns pointer input in pixels into millimetres for it.</summary>
public sealed class DrawingCanvas : Control
{
    private DrawingViewModel? _model;
    private Avalonia.Point? _panFrom;

    public DrawingCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
            _model.Redraw -= OnRedraw;
        _model = DataContext as DrawingViewModel;
        if (_model is not null)
            _model.Redraw += OnRedraw;
        InvalidateVisual();
    }

    private void OnRedraw(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        if (_model is null)
            return;
        var scene = new Scene(_model.Drawing, _model.View, _model.SelectedId, _model.GhostLine, _model.GhostBox);
        context.Custom(new SkiaOperation(new Rect(Bounds.Size), scene));
    }

    /// <summary>The millimetre position of a point in this control's pixels.</summary>
    public (double X, double Y) ToMm(Avalonia.Point p) =>
        (_model?.View ?? ViewTransform.Default).ToMm(p.X, p.Y, Bounds.Width, Bounds.Height);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_model is null)
            return;
        Focus();
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            var (x, y) = ToMm(point.Position);
            _model.PointerPressed(x, y);
            e.Pointer.Capture(this);
        }
        else if (point.Properties.IsMiddleButtonPressed || point.Properties.IsRightButtonPressed)
        {
            _panFrom = point.Position;
            e.Pointer.Capture(this);
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_model is null)
            return;
        var position = e.GetPosition(this);
        if (_panFrom is { } from)
        {
            _model.Pan((from.X - position.X) / _model.View.Scale, (from.Y - position.Y) / _model.View.Scale);
            _panFrom = position;
            return;
        }
        var (x, y) = ToMm(position);
        _model.PointerMoved(x, y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_model is null)
            return;
        if (_panFrom is not null)
        {
            _panFrom = null;
        }
        else if (e.InitialPressMouseButton == MouseButton.Left)
        {
            var (x, y) = ToMm(e.GetPosition(this));
            _ = _model.PointerReleased(x, y);
        }
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_model is null)
            return;
        var (x, y) = ToMm(e.GetPosition(this));
        _model.Zoom(Math.Pow(1.15, e.Delta.Y), x, y);
        e.Handled = true;
    }

    /// <summary>Draws a scene through Avalonia's SkiaSharp canvas on the render thread.</summary>
    private sealed class SkiaOperation(Rect bounds, Scene scene) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public bool HitTest(Avalonia.Point p) => bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => other is SkiaOperation op && op.Bounds == bounds && ReferenceEquals(op.Scene, scene);

        private Scene Scene => scene;

        public void Render(ImmediateDrawingContext context)
        {
            // Without Skia, as with headless drawing, there is nothing to draw with, so the canvas stays blank.
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
                return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            canvas.Save();
            canvas.ClipRect(new SkiaSharp.SKRect(0, 0, (float)bounds.Width, (float)bounds.Height));
            SceneRenderer.Render(canvas, scene, (float)bounds.Width, (float)bounds.Height);
            canvas.Restore();
        }

        public void Dispose()
        {
        }
    }
}
