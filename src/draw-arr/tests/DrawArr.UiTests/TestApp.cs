using Avalonia;
using Avalonia.Headless;
using DrawArr.UiTests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace DrawArr.UiTests;

/// <summary>The real <see cref="DrawArr.App.App"/> on the headless platform with Skia rendering offscreen, so the canvas really draws.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DrawArr.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
