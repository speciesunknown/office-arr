using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TimeArr.Commands;
using TimeArr.ViewModels;

namespace TimeArr.App;

public sealed class App : Application
{
    /// <summary>The instance's command queue, which the window gets a turn in, set by <see cref="Program"/> before startup.</summary>
    public static CommandQueue? Queue { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        CrashLog.InstallForUiThread();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var queue = Queue ?? throw new InvalidOperationException("App.Queue must be set before startup.");
            // The window's turn lasts as long as the app, so it is never given back.
            var window = queue.Join();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(window.Send, TimeProvider.System),
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
