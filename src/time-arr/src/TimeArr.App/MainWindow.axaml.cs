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

    // A left press on empty space in mini mode, with where it began, until it is released or becomes a drag.
    private PointerPressedEventArgs? _press;
    private Point _pressedAt;

    // Set when a double-click left mini mode, so the same double-click doesn't also start a rename.
    private bool _doubleClickUsed;

    // Registered once for every window, since a class handler is global and is never removed.
    static MainWindow() => TextBox.IsVisibleProperty.Changed.AddClassHandler<TextBox>(OnTextBoxShown);

    public MainWindow()
    {
        InitializeComponent();
        _normalMinWidth = MinWidth;
        _normalMinHeight = MinHeight;
        _ticker.Tick += (_, _) =>
        {
            _viewModel?.Tick();
            FitMiniToContent();
        };
        Opened += (_, _) => _ticker.Start();
        Closed += (_, _) => _ticker.Stop();
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
        // Buttons handle their own presses, so these only see presses on empty space and labels.
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, _) => _press = null;
        AddHandler(DoubleTappedEvent, OnDoubleTapped);
    }

    /// <summary>How far a press in mini mode has to move before it drags the window, so a double-click isn't taken as a drag.</summary>
    private const double DragThreshold = 4;

    /// <summary>In mini mode, a double-click on empty space goes back to normal mode, and a single press may become a drag.</summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _press = null;
        _doubleClickUsed = false;
        if (_viewModel?.Mode != WindowMode.Mini || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2)
        {
            _doubleClickUsed = true;
            _viewModel.ToggleMiniModeCommand.Execute(null);
            return;
        }
        _press = e;
        _pressedAt = e.GetPosition(this);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || _viewModel?.Mode != WindowMode.Mini)
            return;
        var moved = e.GetPosition(this) - _pressedAt;
        if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold)
            return;
        _press = null;
        BeginMoveDrag(press);
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
        if (_doubleClickUsed)
        {
            _doubleClickUsed = false;
            return;
        }
        var area = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Panel>().FirstOrDefault(p => p.Classes.Contains("nameArea"));
        if (area?.DataContext is TimerViewModel timer && timer.BeginRenameCommand.CanExecute(null))
            timer.BeginRenameCommand.Execute(null);
    }

    /// <summary>Puts the cursor in a rename or add box as it appears, with any old text selected so typing replaces it.</summary>
    private static void OnTextBoxShown(TextBox box, AvaloniaPropertyChangedEventArgs e)
    {
        var focusable = box.Classes.Contains("rename") || box.Classes.Contains("focusOnShow");
        if (!focusable || !box.IsVisible || TopLevel.GetTopLevel(box) is not MainWindow)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    /// <summary>Sizes the mini window to its rows, growing upward when it sits in the lower half of the screen and downward otherwise.</summary>
    private void FitMiniToContent()
    {
        if (_viewModel?.Mode != WindowMode.Mini)
            return;
        Root.Measure(new Size(MiniMaxWidth, double.PositiveInfinity));
        var wanted = Root.DesiredSize;
        if (Math.Abs(wanted.Width - ClientSize.Width) < 1 && Math.Abs(wanted.Height - ClientSize.Height) < 1)
            return;

        var scale = RenderScaling;
        var top = Position.Y;
        var bottom = top + (int)Math.Round(ClientSize.Height * scale);
        var newHeight = (int)Math.Round(wanted.Height * scale);
        if (Screens.ScreenFromWindow(this)?.WorkingArea is { } area)
        {
            var anchorBottom = bottom > area.Y + (area.Height / 2);
            top = anchorBottom ? bottom - newHeight : top;
            top = Math.Clamp(top, area.Y, Math.Max(area.Y, area.Bottom - newHeight));
        }
        Width = wanted.Width;
        Height = wanted.Height;
        if (top != Position.Y)
            Position = new PixelPoint(Position.X, top);
    }

    /// <summary>Mini mode is borderless, un-maximized, sized to fit and capped in width, and normal mode puts everything back.</summary>
    private void ApplyMode(WindowMode mode)
    {
        switch (mode)
        {
            case WindowMode.Mini:
                _normalState = WindowState;
                // A maximized or full-screen window ignores Width and Height, so it has to come down first.
                if (WindowState != WindowState.Normal)
                    WindowState = WindowState.Normal;
                if (SizeToContent == SizeToContent.Manual)
                    _normalSize = new Size(Width, Height);
                MinWidth = 0;
                MinHeight = 0;
                MaxWidth = MiniMaxWidth;
                Root.Margin = new Thickness(8, 4, 8, 8);
                TimerList.Margin = default;
                WindowDecorations = WindowDecorations.None;
                // Sized by hand, not SizeToContent, which would grow downward before the fit could keep the bottom edge.
                SizeToContent = SizeToContent.Manual;
                FitMiniToContent();
                // Again once the rows have caught up with the mode change.
                Dispatcher.UIThread.Post(FitMiniToContent);
                break;
            case WindowMode.Normal:
                WindowDecorations = WindowDecorations.Full;
                SizeToContent = SizeToContent.Manual;
                Root.Margin = new Thickness(16);
                TimerList.Margin = new Thickness(0, 0, 16, 0);
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
