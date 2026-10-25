using DrawArr.Core;

namespace DrawArr.Files;

/// <summary>Where drawings that haven't been saved under a name are autosaved.</summary>
/// <param name="path">The folder, which is created readable only by the user.</param>
public sealed class AutosaveFolder(string path)
{
    /// <summary>The usual folder, <c>~/.local/share/draw-arr/autosave</c> on Linux.</summary>
    public static AutosaveFolder Default { get; } = new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "draw-arr", "autosave"));

    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>A new, unused file name in the folder, creating the folder if need be.</summary>
    public Result<string> NewFile(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (!System.IO.Path.IsPathFullyQualified(Path))
            return Result<string>.Fail(ErrorCode.FileUnavailable, "There is no local data folder to autosave in.");
        try
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(Path);
            else
                Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Fail(ErrorCode.FileUnavailable, $"Can't create the autosave folder {Path}: {e.Message}");
        }
        var name = $"drawing-{clock.GetLocalNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.xml";
        return System.IO.Path.Combine(Path, name);
    }
}
