using System.Globalization;
using Avalonia.Threading;

namespace TimeArr.App;

/// <summary>Writes bugs that end the app to an owner-only <c>time-arr.log</c> next to the database, so there is something to report.</summary>
public static class CrashLog
{
    public const string FileName = "time-arr.log";

    /// <summary>The log is started afresh once it grows past this, keeping one older copy.</summary>
    private const long MaxBytes = 1024 * 1024;

    private static readonly Lock Gate = new();
    private static string? _path;

    /// <summary>Where crashes are written, once <see cref="Install"/> has run.</summary>
    public static string? FilePath => _path;

    /// <summary>Hooks the process-wide unhandled-exception events, logging into <paramref name="folder"/>.</summary>
    public static void Install(string folder)
    {
        _path = Path.Combine(folder, FileName);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("time-arr crashed (unhandled)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Write("unobserved task fault, time-arr kept running", e.Exception);
    }

    /// <summary>Hooks the UI thread's unhandled exceptions, once Avalonia is running.</summary>
    public static void InstallForUiThread() =>
        // Logged and left unhandled, so a bug on the UI thread still ends the app as CODING.md #4 asks.
        Dispatcher.UIThread.UnhandledException += (_, e) => Write("time-arr crashed (UI thread)", e.Exception);

    /// <summary>Appends one crash report, never throwing, since this runs while the app is already failing.</summary>
    /// <param name="heading">What happened, written before the exception.</param>
    public static void Write(string heading, Exception? exception)
    {
        if (_path is not { } path)
            return;
        lock (Gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);

                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var writer = new StreamWriter(path, options);
                var stamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                writer.WriteLine($"[{stamp}] {heading}:");
                writer.WriteLine(exception?.ToString() ?? "(no exception object)");
                writer.WriteLine();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // There is nowhere left to report a failure to write the crash report.
            }
        }
    }
}
