using DrawArr.Core;

namespace DrawArr.Files;

/// <summary>Where drawings that haven't been saved under a name are autosaved.</summary>
/// <param name="path">The folder, which is created readable only by the user.</param>
public sealed class AutosaveFolder(string path)
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

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
            if (new DirectoryInfo(Path).LinkTarget is not null)
                return Result<string>.Fail(ErrorCode.FileUnavailable, $"The autosave folder {Path} is a link, which draw-arr won't follow.");
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(Path);
            }
            else
            {
                // draw-arr's own folder above it is private too, since it is only for draw-arr's files.
                if (System.IO.Path.GetDirectoryName(Path) is { } parent && !Directory.Exists(parent))
                    Directory.CreateDirectory(parent, Private);
                Directory.CreateDirectory(Path, Private);
                // A folder left from before, or made by hand, is tightened so other users can't read the autosaves.
                File.SetUnixFileMode(Path, Private);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Fail(ErrorCode.FileUnavailable, $"Can't create the autosave folder {Path}: {e.Message}");
        }
        var name = $"drawing-{clock.GetLocalNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.xml";
        return System.IO.Path.Combine(Path, name);
    }
}
