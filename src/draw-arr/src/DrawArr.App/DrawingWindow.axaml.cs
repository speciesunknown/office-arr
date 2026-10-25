using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DrawArr.App.Canvas;
using DrawArr.ViewModels;

namespace DrawArr.App;

/// <summary>The window for one open drawing.</summary>
public sealed partial class DrawingWindow : Window
{
    private static readonly FilePickerFileType DrawingFiles = new("draw-arr drawings") { Patterns = ["*.xml"] };

    private readonly AppHost _host;
    private readonly DocumentSession _session;
    private readonly DrawingViewModel _model;
    // Set once closing has saved, so the second Close that follows it goes through.
    private CloseState _closing = CloseState.Open;

    private enum CloseState { Open = 1, Saving = 2, Saved = 3 }

    /// <summary>Only for the XAML designer and loader, which need a parameterless constructor.</summary>
    public DrawingWindow() => throw new InvalidOperationException("A drawing window needs a document.");

    public DrawingWindow(AppHost host, DocumentSession session)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _model = session.ViewModel;
        InitializeComponent();
        DataContext = _model;
        session.AskForPath = AskForSavePath;

        NewItem.Click += (_, _) => New();
        OpenItem.Click += (_, _) => _ = Open();
        SaveAsItem.Click += (_, _) => _ = SaveAs();
        CloseItem.Click += (_, _) => Close();
        LineTool.Click += (_, _) =>
            _model.Tool = _model.Tool == Tool.Line ? Tool.Select : Tool.Line;

        var source = BoxSource;
        source.PointerPressed += OnBoxSourcePressed;
        source.PointerReleased += OnBoxSourceReleased;

        var text = BoxText;
        text.LostFocus += (_, _) => _ = _model.CommitText();
        text.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _ = _model.CommitText();
                e.Handled = true;
            }
        };

        FillPalette(PaletteButtons);
    }

    /// <summary>The canvas, for tests that drive it.</summary>
    public DrawingCanvas DrawingCanvas => Canvas;

    public DocumentSession Session => _session;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Keys typed into the text box belong to the text, so only keys nothing else took are commands.
        if (e.Handled || e.Source is TextBox)
            return;
        switch (e.Key, e.KeyModifiers)
        {
            case (Key.Delete, KeyModifiers.None):
            case (Key.Back, KeyModifiers.None):
                _model.DeleteCommand.Execute(null);
                break;
            case (Key.Escape, KeyModifiers.None):
                _model.CancelGesture();
                _model.Tool = Tool.Select;
                break;
            case (Key.N, KeyModifiers.Control):
                New();
                break;
            case (Key.O, KeyModifiers.Control):
                _ = Open();
                break;
            case (Key.W, KeyModifiers.Control):
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closing == CloseState.Saved)
            return;
        // Closing waits for the last changes to be saved, and stays open with a message if they can't be.
        e.Cancel = true;
        if (_closing == CloseState.Saving)
            return;
        _closing = CloseState.Saving;
        var closed = await _host.Close(_session).ConfigureAwait(true);
        if (closed.IsFailure)
        {
            _closing = CloseState.Open;
            _model.Report($"Not closed, because the last changes couldn't be saved: {closed.Error.Message}");
            return;
        }
        _closing = CloseState.Saved;
        Close();
    }

    private void FillPalette(WrapPanel panel)
    {
        foreach (var colour in DrawingViewModel.Palette)
        {
            var skia = SceneRenderer.ToSkia(colour);
            var button = new Button
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(Color.FromRgb(skia.Red, skia.Green, skia.Blue)),
                Name = $"Colour{colour}",
            };
            ToolTip.SetTip(button, colour.ToString());
            button.Click += (_, _) => _model.Colour = colour;
            panel.Children.Add(button);
        }
    }

    private void OnBoxSourcePressed(object? sender, PointerPressedEventArgs e)
    {
        _model.Tool = Tool.Box;
        e.Handled = true;
    }

    /// <summary>Places the box where the pointer is let go if that is over the canvas, so the box can be dragged out of the sidebar.</summary>
    private void OnBoxSourceReleased(object? sender, PointerReleasedEventArgs e)
    {
        var canvas = Canvas;
        var at = e.GetPosition(canvas);
        if (_model.Tool == Tool.Box && new Rect(canvas.Bounds.Size).Contains(at))
        {
            var (x, y) = canvas.ToMm(at);
            _model.PointerPressed(x, y);
        }
        e.Handled = true;
    }

    private void New()
    {
        var created = _host.New();
        if (created.IsFailure)
            _model.Report(created.Error.Message);
    }

    private async Task Open()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open drawing",
            AllowMultiple = true,
            FileTypeFilter = [DrawingFiles, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is not { } path)
                continue;
            var opened = await _host.Open(path).ConfigureAwait(true);
            if (opened.IsFailure)
                _model.Report(opened.Error.Message);
            else
                ActivateWindowFor(opened.Value);
        }
    }

    private async Task SaveAs()
    {
        if (await AskForSavePath().ConfigureAwait(true) is not { } path)
            return;
        var saved = await _session.SaveAs(path).ConfigureAwait(true);
        _model.Report(saved.IsSuccess ? "Saved." : saved.Error.Message);
    }

    private async Task<string?> AskForSavePath()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save drawing",
            DefaultExtension = "xml",
            FileTypeChoices = [DrawingFiles],
            SuggestedFileName = "drawing.xml",
        }).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    private static void ActivateWindowFor(DocumentSession session)
    {
        if (Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            return;
        foreach (var window in desktop.Windows)
        {
            if (window is DrawingWindow drawing && drawing.Session == session)
                window.Activate();
        }
    }
}
