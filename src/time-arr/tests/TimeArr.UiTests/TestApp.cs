using Avalonia;
using Avalonia.Headless;
using TimeArr.UiTests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace TimeArr.UiTests;

/// <summary>
/// The real <see cref="App"/> on the headless platform with Skia rendering offscreen, so text has
/// glyphs that the mouse can hit as on a desktop (headless drawing alone renders none).
/// </summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TimeArr.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}
