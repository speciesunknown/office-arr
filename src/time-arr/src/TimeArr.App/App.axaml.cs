using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TimeArr.Commands;
using TimeArr.ViewModels;

namespace TimeArr.App;

public sealed class App : Application
{
    /// <summary>Set by <see cref="Program"/> before startup (or by tests), so the window has something to talk to.</summary>
    public static ITimerCommands? Commands { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var commands = Commands ?? throw new InvalidOperationException("App.Commands must be set before startup.");
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(commands, TimeProvider.System) };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
