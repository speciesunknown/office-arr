using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DrawArr.App;
using DrawArr.Files;
using DrawArr.ViewModels;

namespace DrawArr.UiTests;

/// <summary>An app host with one new drawing open in a real window, autosaving to a temporary folder.</summary>
internal sealed class Ui : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    public Ui()
    {
        Folder = Directory.CreateTempSubdirectory("draw-arr-ui-").FullName;
        Host = new AppHost(TimeProvider.System, new AutosaveFolder(Path.Combine(Folder, "autosave")), action => Dispatcher.UIThread.Post(action));
        Host.Opened += (_, session) => Windows.Add(new DrawingWindow(Host, session));
        Assert.True(Host.New().IsSuccess);
        Pump();
        Window.Show();
        Pump();
    }

    public string Folder { get; }
    public AppHost Host { get; }
    public List<DrawingWindow> Windows { get; } = [];
    public DrawingWindow Window => Windows[0];
    public DrawingViewModel Vm => Window.Session.ViewModel;

    public void Dispose()
    {
        foreach (var window in Windows)
            window.Close();
        Pump();
        Host.Dispose();
        Directory.Delete(Folder, recursive: true);
    }

    public void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Window?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pumps the UI thread until every command the window sent has been answered and applied.</summary>
    public void Settle()
    {
        var idle = Vm.WhenIdle();
        var until = DateTime.UtcNow + Wait;
        while (!idle.IsCompleted)
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The window's commands weren't answered.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Pump();
    }

    public T Named<T>(string name) where T : Control =>
        Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"No control named {name}.");

    /// <summary>Where a millimetre point is on the window, in window pixels.</summary>
    public Point AtMm(double x, double y)
    {
        var canvas = Window.DrawingCanvas;
        var (px, py) = Vm.View.ToPx(x, y, canvas.Bounds.Width, canvas.Bounds.Height);
        return canvas.TranslatePoint(new Point(px, py), Window)!.Value;
    }

    public Point Centre(Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)!.Value;

    public void Drag(Point from, Point to)
    {
        Window.MouseMove(from);
        Window.MouseDown(from, MouseButton.Left);
        Window.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        Window.MouseMove(to);
        Window.MouseUp(to, MouseButton.Left);
        Settle();
    }

    public void Click(Point at) => Drag(at, at);

    /// <summary>Whether any pixel in a small square around a point of the rendered window is bright, as an outline or text is.</summary>
    public bool BrightNear(Point at, int radius = 2)
    {
        Pump();
        using var frame = Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        using var locked = frame.Lock();
        var cx = (int)Math.Round(at.X * Window.RenderScaling);
        var cy = (int)Math.Round(at.Y * Window.RenderScaling);
        for (var y = cy - radius; y <= cy + radius; y++)
        {
            for (var x = cx - radius; x <= cx + radius; x++)
            {
                if (x < 0 || y < 0 || x >= locked.Size.Width || y >= locked.Size.Height)
                    continue;
                // Frames are BGRA in memory, so the int read here is 0xAARRGGBB.
                var pixel = (uint)Marshal.ReadInt32(locked.Address, y * locked.RowBytes + x * 4);
                if (((pixel >> 16) & 0xFF) > 0x80 && ((pixel >> 8) & 0xFF) > 0x80 && (pixel & 0xFF) > 0x80)
                    return true;
            }
        }
        return false;
    }
}
