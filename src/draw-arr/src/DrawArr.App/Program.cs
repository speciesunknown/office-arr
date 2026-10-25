using Avalonia;

namespace DrawArr.App;

/// <summary>Startup, opening a window for each drawing file named on the command line, or a new drawing if none is.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        App.StartupFiles = args;
        // draw-arr's own arguments are not for Avalonia.
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
    }

    /// <summary>The Avalonia setup, also used by the designer and by headless tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
