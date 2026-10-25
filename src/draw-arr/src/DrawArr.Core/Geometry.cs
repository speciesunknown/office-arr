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

/// <summary>The 5 mm grid that commands snap their input to, and the limits on where things can go.</summary>
/// <remarks>The grid is virtual: commands snap to it, while the model and files accept any whole-millimetre value within the limits.</remarks>
public static class Grid
{
    /// <summary>The grid spacing in millimetres.</summary>
    public const int Step = 5;

    /// <summary>The furthest any point, including every part of every box, may be from 0 along either axis in millimetres.</summary>
    public const int Limit = 100_000;

    /// <summary>The smallest width or height a command gives a box.</summary>
    public const int MinSize = Step;

    /// <summary>The largest width or height a box can have, which spans the whole area.</summary>
    public const int MaxSize = 2 * Limit;

    /// <summary>The grid line nearest to <paramref name="mm"/>, with halves rounded away from zero, in whole-number arithmetic that can't overflow.</summary>
    public static int Snap(int mm)
    {
        long m = mm;
        var snapped = m >= 0 ? (m + Step / 2) / Step * Step : -((-m + Step / 2) / Step * Step);
        return (int)Math.Clamp(snapped, int.MinValue / Step * Step, int.MaxValue / Step * Step);
    }

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

    /// <summary>A rectangle snapped to the grid, with sizes snapped before they are checked, or an error if it is too small, too big or reaches past the limits.</summary>
    public static Result<Rect> Rect(int x, int y, int width, int height)
    {
        var corner = Point(x, y);
        if (corner.IsFailure)
            return Result<Rect>.Fail(corner.Error);
        if (width < 0 || height < 0 || width > MaxSize || height > MaxSize)
            return Result<Rect>.Fail(ErrorCode.InvalidInput, $"A box's width and height must be between {MinSize} and {MaxSize} mm.");
        var rect = new Rect(corner.Value.X, corner.Value.Y, Snap(width), Snap(height));
        if (rect.Width < MinSize || rect.Height < MinSize)
            return Result<Rect>.Fail(ErrorCode.InvalidInput, $"A box must be at least {MinSize} mm wide and high.");
        if (OutOfRange(rect.Right) || OutOfRange(rect.Bottom))
            return Result<Rect>.Fail(ErrorCode.InvalidInput, $"A box must fit within {Limit} mm of the centre.");
        return rect;
    }

    /// <summary>Whether a point is within the limits, on the grid or not.</summary>
    public static bool IsValid(Point point) => !OutOfRange(point.X) && !OutOfRange(point.Y);

    /// <summary>Whether a rectangle has a positive size and lies wholly within the limits, on the grid or not.</summary>
    public static bool IsValid(Rect rect) =>
        !OutOfRange(rect.X) && !OutOfRange(rect.Y) && rect.Width >= 1 && rect.Height >= 1
        && rect.Width <= MaxSize && rect.Height <= MaxSize && !OutOfRange(rect.Right) && !OutOfRange(rect.Bottom);

    // Callers check the corner first, so Right and Bottom are at most Limit + MaxSize and can't overflow.
    private static bool OutOfRange(int mm) => mm < -Limit || mm > Limit;
}
