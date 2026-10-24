using Avalonia;
using TimeArr.Commands;
using TimeArr.Data;

namespace TimeArr.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // A storage problem here is not fatal: the handler retries on every command, and the
        // window shows what's wrong.
        var storage = SqliteTimerStorage.Default();
        if (storage.IsFailure)
        {
            Console.Error.WriteLine($"time-arr: {storage.Error.Message}");
            return 1;
        }
        App.Commands = new TimerCommandHandler(storage.Value, TimeProvider.System);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Also used by the Avalonia designer and by headless tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
