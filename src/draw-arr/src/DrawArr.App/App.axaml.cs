using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DrawArr.Files;

namespace DrawArr.App;

public sealed class App : Application
{
    /// <summary>The files to open at startup, set by <see cref="Program"/>; none opens a new drawing.</summary>
    public static IReadOnlyList<string> StartupFiles { get; set; } = [];

    /// <summary>Where unnamed drawings autosave, which tests point somewhere temporary.</summary>
    public static AutosaveFolder Autosave { get; set; } = AutosaveFolder.Default;

    /// <summary>The running instance's documents, created at startup.</summary>
    public AppHost? Host { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            var host = new AppHost(TimeProvider.System, Autosave, action => Dispatcher.UIThread.Post(action));
            Host = host;
            host.Opened += (_, session) => new DrawingWindow(host, session).Show();
            desktop.Exit += (_, _) => host.Dispose();
            _ = OpenStartupFiles(host, desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task OpenStartupFiles(AppHost host, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var opened = 0;
        var problems = new List<string>();
        foreach (var file in StartupFiles)
        {
            var session = await host.Open(file).ConfigureAwait(true);
            if (session.IsSuccess)
                opened++;
            else
                problems.Add(session.Error.Message);
        }
        // A window always opens, so problems with the files have somewhere to be shown.
        if (opened == 0)
        {
            var fresh = host.New();
            if (fresh.IsFailure)
                problems.Add(fresh.Error.Message);
        }
        if (host.Sessions.Count == 0)
        {
            // With no window to show them in, the problems go to the terminal and the app ends.
            await Console.Error.WriteLineAsync(Printable.Text("draw-arr: " + string.Join(" ", problems))).ConfigureAwait(true);
            desktop.Shutdown(1);
            return;
        }
        if (problems.Count > 0)
            Dispatcher.UIThread.Post(() => ShowProblems(host, problems), DispatcherPriority.Background);
    }

    private static void ShowProblems(AppHost host, List<string> problems)
    {
        foreach (var session in host.Sessions)
            session.ViewModel.Report(string.Join(" ", problems));
    }
}
