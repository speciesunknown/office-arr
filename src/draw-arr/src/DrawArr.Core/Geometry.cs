using System.Globalization;

namespace DrawArr.Core;

/// <summary>A point in millimetres, with 0,0 at the centre of the view and y growing downwards.</summary>
public readonly record struct Point(int X, int Y)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X}, {Y})");
}

/// <summary>A rectangle in millimetres given by its top-left corner and its size.</summary>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X}, {Y}) {Width}x{Height}");
}

/// <summary>The 5 mm grid everything snaps to, and the limits on where things can go.</summary>
public static class Grid
{
    /// <summary>The grid spacing in millimetres.</summary>
    public const int Step = 5;

    /// <summary>The furthest any coordinate may be from 0 in millimetres, which keeps every sum well inside an int.</summary>
    public const int Limit = 100_000;

    /// <summary>The smallest width or height a box can have.</summary>
    public const int MinSize = Step;

    /// <summary>The largest width or height a box can have.</summary>
    public const int MaxSize = Limit;

    /// <summary>The grid line nearest to <paramref name="mm"/>, with halves rounded away from zero.</summary>
    public static int Snap(int mm) => (int)Math.Round(mm / (double)Step, MidpointRounding.AwayFromZero) * Step;

    /// <summary>The grid line nearest to <paramref name="mm"/>, clamped to the limits, for pointer positions which are always finite.</summary>
    public static int Snap(double mm)
    {
        if (!double.IsFinite(mm))
            throw new ArgumentOutOfRangeException(nameof(mm), mm, "A pointer position must be finite.");
        var clamped = Math.Clamp(mm, -Limit, Limit);
        return (int)Math.Round(clamped / Step, MidpointRounding.AwayFromZero) * Step;
    }

    /// <summary>A point snapped to the grid, or an error if it is outside the limits.</summary>
    public static Result<Point> Point(int x, int y)
    {
        if (OutOfRange(x) || OutOfRange(y))
            return Result<Point>.Fail(ErrorCode.InvalidInput, $"A position must be within {Limit} mm of the centre.");
        return new Point(Snap(x), Snap(y));
    }

    /// <summary>A rectangle snapped to the grid, or an error if it is outside the limits or too small.</summary>
    public static Result<Rect> Rect(int x, int y, int width, int height)
    {
        var corner = Point(x, y);
        if (corner.IsFailure)
            return Result<Rect>.Fail(corner.Error);
        if (width < MinSize || height < MinSize || width > MaxSize || height > MaxSize)
            return Result<Rect>.Fail(ErrorCode.InvalidInput, $"A box's width and height must be between {MinSize} and {MaxSize} mm.");
        return new Rect(corner.Value.X, corner.Value.Y, Snap(width), Snap(height));
    }

    private static bool OutOfRange(int mm) => mm < -Limit || mm > Limit;
}
