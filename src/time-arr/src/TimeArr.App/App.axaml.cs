using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TimeArr.Commands;
using TimeArr.ViewModels;

namespace TimeArr.App;

public sealed class App : Application
{
    /// <summary>What the window talks to, set by <see cref="Program"/> (or a test) before startup.</summary>
    public static ITimerCommands? Commands { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        CrashLog.InstallForUiThread();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var commands = Commands ?? throw new InvalidOperationException("App.Commands must be set before startup.");
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(commands, TimeProvider.System, MainViewModel.RunInBackground),
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
