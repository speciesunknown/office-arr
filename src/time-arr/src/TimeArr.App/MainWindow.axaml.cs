using Avalonia.Controls;
using Avalonia.Threading;
using TimeArr.ViewModels;

namespace TimeArr.App;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public MainWindow()
    {
        InitializeComponent();
        _ticker.Tick += (_, _) => (DataContext as MainViewModel)?.Tick();
        Opened += (_, _) => _ticker.Start();
        Closed += (_, _) => _ticker.Stop();
    }
}
