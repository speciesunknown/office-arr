namespace DrawArr.ViewModels;

/// <summary>How the canvas maps millimetres to pixels: a scale, and the point in millimetres shown at the centre of the view.</summary>
/// <param name="Scale">Pixels per millimetre.</param>
/// <param name="CentreX">The x in millimetres at the centre of the view.</param>
/// <param name="CentreY">The y in millimetres at the centre of the view.</param>
public sealed record ViewTransform(double Scale, double CentreX, double CentreY)
{
    public const double MinScale = 0.25;
    public const double MaxScale = 40;

    /// <summary>The furthest the centre of the view may be from 0, a little beyond the drawing's limits.</summary>
    public const double MaxCentre = 2.0 * DrawArr.Core.Grid.Limit;

    /// <summary>The starting view, with 0,0 at the centre and a 5 mm grid square 20 pixels wide.</summary>
    public static ViewTransform Default { get; } = new(4, 0, 0);

    /// <summary>The millimetre position of a pixel in a view of the given size.</summary>
    public (double X, double Y) ToMm(double px, double py, double width, double height) =>
        ((px - width / 2) / Scale + CentreX, (py - height / 2) / Scale + CentreY);

    /// <summary>The pixel position of a millimetre point in a view of the given size.</summary>
    public (double X, double Y) ToPx(double x, double y, double width, double height) =>
        ((x - CentreX) * Scale + width / 2, (y - CentreY) * Scale + height / 2);

    /// <summary>This view zoomed by a factor while keeping the millimetre point under the pointer where it is.</summary>
    public ViewTransform ZoomAround(double factor, double x, double y)
    {
        var scale = Math.Clamp(Scale * factor, MinScale, MaxScale);
        var kept = Scale / scale;
        return Clamped(new ViewTransform(scale, x - (x - CentreX) * kept, y - (y - CentreY) * kept));
    }

    /// <summary>This view with its centre moved by a distance in millimetres.</summary>
    public ViewTransform PanBy(double dx, double dy) => Clamped(this with { CentreX = CentreX + dx, CentreY = CentreY + dy });

    // Keeping the centre near the drawing keeps grid drawing away from doubles so large that adding a step changes nothing.
    private static ViewTransform Clamped(ViewTransform view) => view with
    {
        CentreX = Math.Clamp(view.CentreX, -MaxCentre, MaxCentre),
        CentreY = Math.Clamp(view.CentreY, -MaxCentre, MaxCentre),
    };
}
