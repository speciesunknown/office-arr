using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using TimeArr.ViewModels;

namespace TimeArr.App;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private MainViewModel? _viewModel;

    // The normal-mode size, put back when leaving mini mode.
    private Size _normalSize;
    private double _normalMinWidth;
    private double _normalMinHeight;

    public MainWindow()
    {
        InitializeComponent();
        _normalMinWidth = MinWidth;
        _normalMinHeight = MinHeight;
        _ticker.Tick += (_, _) => _viewModel?.Tick();
        Opened += (_, _) => _ticker.Start();
        Closed += (_, _) => _ticker.Stop();
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
    }

    private void Attach(MainViewModel? viewModel)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            ApplyMode(_viewModel.IsMiniMode);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsMiniMode))
            ApplyMode(_viewModel!.IsMiniMode);
    }

    /// <summary>Mini mode: the window shrinks to fit the running timers. Normal mode: back to its old size.</summary>
    private void ApplyMode(bool mini)
    {
        if (mini)
        {
            if (SizeToContent == SizeToContent.Manual)
                _normalSize = new Size(Width, Height);
            MinWidth = 0;
            MinHeight = 0;
            Root.Margin = new Thickness(8, 4, 8, 8);
            SizeToContent = SizeToContent.WidthAndHeight;
        }
        else
        {
            SizeToContent = SizeToContent.Manual;
            Root.Margin = new Thickness(16);
            MinWidth = _normalMinWidth;
            MinHeight = _normalMinHeight;
            if (_normalSize.Width > 0)
            {
                Width = _normalSize.Width;
                Height = _normalSize.Height;
            }
        }
    }
}
