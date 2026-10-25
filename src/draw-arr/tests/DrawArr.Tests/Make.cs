using DrawArr.Core;

namespace DrawArr.Tests;

/// <summary>Builds valid model values through their factories, failing the test if a value is not valid.</summary>
internal static class Make
{
    public static Box Box(long id, int x, int y, int width, int height, Colour colour = Colour.White, string text = "") =>
        Box(id, new Rect(x, y, width, height), colour, text);

    public static Box Box(long id, Rect bounds, Colour colour = Colour.White, string text = "") =>
        Value(DrawArr.Core.Box.Create(id, bounds, colour, text));

    public static LineEnd.Free Free(int x, int y) => Value(LineEnd.Free.Create(new Point(x, y)));

    public static LineEnd.Attached On(long boxId, Edge edge, int offset) => Value(LineEnd.Attached.Create(boxId, edge, offset));

    public static Line Line(long id, LineEnd start, LineEnd end, Colour colour = Colour.White) =>
        Value(DrawArr.Core.Line.Create(id, start, end, colour));

    private static T Value<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, result.ToString());
        return result.Value;
    }
}
