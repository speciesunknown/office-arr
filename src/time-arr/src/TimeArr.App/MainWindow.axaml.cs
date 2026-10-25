using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TimeArr.ViewModels;

namespace TimeArr.App;

public sealed partial class MainWindow : Window
{
    /// <summary>The widest the mini window gets, so a long message wraps instead of pushing it off screen.</summary>
    public const double MiniMaxWidth = 360;

    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private MainViewModel? _viewModel;

    // The normal-mode size and window state, put back when leaving mini mode.
    private Size _normalSize;
    private WindowState _normalState = WindowState.Normal;
    private readonly double _normalMinWidth;
    private readonly double _normalMinHeight;

    // Registered once for every window, since a class handler is global and is never removed.
    static MainWindow() => TextBox.IsVisibleProperty.Changed.AddClassHandler<TextBox>(OnTextBoxShown);

    public MainWindow()
    {
        InitializeComponent();
        _normalMinWidth = MinWidth;
        _normalMinHeight = MinHeight;
        _ticker.Tick += (_, _) => _viewModel?.Tick();
        Opened += (_, _) => _ticker.Start();
        Closed += (_, _) => _ticker.Stop();
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
        // Buttons handle their own presses, so this only sees presses on empty space and labels.
        PointerPressed += (_, e) =>
        {
            if (_viewModel?.Mode == WindowMode.Mini && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
        AddHandler(DoubleTappedEvent, OnDoubleTapped);
    }

    private void Attach(MainViewModel? viewModel)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            ApplyMode(_viewModel.Mode);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Mode))
            ApplyMode(_viewModel!.Mode);
    }

    /// <summary>Starts a rename when a timer's name area is double-clicked.</summary>
    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        var area = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Panel>().FirstOrDefault(p => p.Classes.Contains("nameArea"));
        if (area?.DataContext is TimerViewModel timer && timer.BeginRenameCommand.CanExecute(null))
            timer.BeginRenameCommand.Execute(null);
    }

    /// <summary>Puts the cursor in a rename box as it appears, with the old name selected so typing replaces it.</summary>
    private static void OnTextBoxShown(TextBox box, AvaloniaPropertyChangedEventArgs e)
    {
        if (!box.Classes.Contains("rename") || !box.IsVisible || TopLevel.GetTopLevel(box) is not MainWindow)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    /// <summary>Mini mode is borderless, un-maximized, sized to fit and capped in width, and normal mode puts everything back.</summary>
    private void ApplyMode(WindowMode mode)
    {
        switch (mode)
        {
            case WindowMode.Mini:
                _normalState = WindowState;
                // A maximized or full-screen window ignores SizeToContent, so it has to come down first.
                if (WindowState != WindowState.Normal)
                    WindowState = WindowState.Normal;
                if (SizeToContent == SizeToContent.Manual)
                    _normalSize = new Size(Width, Height);
                MinWidth = 0;
                MinHeight = 0;
                MaxWidth = MiniMaxWidth;
                Root.Margin = new Thickness(8, 4, 8, 8);
                WindowDecorations = WindowDecorations.None;
                SizeToContent = SizeToContent.WidthAndHeight;
                break;
            case WindowMode.Normal:
                WindowDecorations = WindowDecorations.Full;
                SizeToContent = SizeToContent.Manual;
                Root.Margin = new Thickness(16);
                MaxWidth = double.PositiveInfinity;
                MinWidth = _normalMinWidth;
                MinHeight = _normalMinHeight;
                if (_normalSize.Width > 0)
                {
                    Width = _normalSize.Width;
                    Height = _normalSize.Height;
                }
                WindowState = _normalState;
                break;
            default:
                throw new InvalidOperationException($"Unhandled window mode {mode}.");
        }
    }
}
