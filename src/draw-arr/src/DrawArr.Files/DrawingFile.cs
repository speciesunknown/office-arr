using DrawArr.Core;

namespace DrawArr.Files;

/// <summary>Drawings on disk, loaded with a size cap and saved so a crash mid-save never leaves a half-written file.</summary>
public static class DrawingFile
{
    /// <summary>The full path a path finally points to after following links, or the path itself if it isn't a link, or null if it can't name a file.</summary>
    public static string? RealPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (BadPath(path) is not null)
            return null;
        try
        {
            var info = new FileInfo(Path.GetFullPath(path));
            return info.LinkTarget is null ? info.FullName : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A link loop or an unreadable link names no file.
            return null;
        }
    }

    /// <summary>The largest file draw-arr will open in bytes, which any drawing the model can hold fits within.</summary>
    public const long MaxBytes = DrawingXml.MaxCharacters;

    /// <summary>Loads a drawing, or an error saying why the file can't be used.</summary>
    /// <remarks>Call it off the UI thread and the command queue, because a file swapped for a pipe after the checks can block the open until someone writes to the pipe.</remarks>
    public static Result<Drawing> Load(string path, LinkHandling links = LinkHandling.Follow)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Enum.IsDefined(links))
            throw new ArgumentOutOfRangeException(nameof(links), links, "Not a way of handling links.");
        if (BadPath(path) is { } bad)
            return Result<Drawing>.Fail(bad);
        try
        {
            var named = new FileInfo(path);
            if (links == LinkHandling.Refuse && named.LinkTarget is not null)
                return Result<Drawing>.Fail(ErrorCode.FileUnavailable, $"{path} turned into a link while it was being opened, so draw-arr won't read it.");
            var info = links == LinkHandling.Follow ? Target(named) : named;
            if (info is null || !info.Exists)
                return Result<Drawing>.Fail(ErrorCode.FileUnavailable, $"There is no file at {path}.");
            // Pipes and devices report no length, so this also keeps them from being opened, which could block or never end.
            if (info.Length == 0)
                return Result<Drawing>.Fail(ErrorCode.FileInvalid, $"{path} is empty or isn't an ordinary file.");
            if (info.Length > MaxBytes)
                return Result<Drawing>.Fail(ErrorCode.FileInvalid, $"{path} is bigger than {MaxBytes / (1024 * 1024)} MB, too big to be a drawing.");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var capped = new CappedStream(stream, MaxBytes);
            var drawing = DrawingXml.Read(capped);
            return drawing.IsSuccess ? drawing : Result<Drawing>.Fail(drawing.Error.Code, $"{path}: {drawing.Error.Message}");
        }
        catch (InvalidDataException e)
        {
            return Result<Drawing>.Fail(ErrorCode.FileInvalid, $"{path}: {e.Message}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Missing folders, permissions and disk errors are about the path the user gave, not a bug.
            return Result<Drawing>.Fail(ErrorCode.FileUnavailable, $"Can't read {path}: {e.Message}");
        }
    }

    /// <summary>An error for a path that can't name a file at all, such as an empty one or one holding a NUL, or null if it is usable.</summary>
    private static Error? BadPath(string path) =>
        path.Length == 0 || path.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0
            ? new Error(ErrorCode.FileUnavailable, "That isn't a usable file path.")
            : null;

    /// <summary>Saves a drawing by writing a temporary file beside it and moving that into place.</summary>
    /// <param name="drawing">The drawing to save.</param>
    /// <param name="path">Where to save it.</param>
    /// <param name="links">Whether a link at the path is followed to its file, or refused because the path should be the file itself.</param>
    /// <returns>Success, or an error saying why the file couldn't be written.</returns>
    public static Result Save(Drawing drawing, string path, LinkHandling links = LinkHandling.Follow)
    {
        if (!Enum.IsDefined(links))
            throw new ArgumentOutOfRangeException(nameof(links), links, "Not a way of handling links.");
        ArgumentNullException.ThrowIfNull(drawing);
        ArgumentNullException.ThrowIfNull(path);
        if (BadPath(path) is { } bad)
            return Result.Fail(bad);
        var temp = "";
        try
        {
            var named = new FileInfo(Path.GetFullPath(path));
            // A path pinned when the drawing was opened must still be the file itself, so a link planted there since can't redirect the save.
            if (links == LinkHandling.Refuse && named.LinkTarget is not null)
                return Result.Fail(ErrorCode.FileUnavailable, $"{path} has been replaced by a link, so draw-arr won't save through it.");
            // When following, the link's target is replaced rather than the link, so a linked file stays linked.
            var target = links == LinkHandling.Follow ? Target(named) : named;
            if (target is null || Directory.Exists(target.FullName))
                return Result.Fail(ErrorCode.FileUnavailable, $"{path} is a folder.");
            var full = target.FullName;
            var folder = Path.GetDirectoryName(full) ?? throw new InvalidOperationException($"{full} has no folder.");
            // A short name of its own, so a file name near the length limit still leaves room for it.
            temp = Path.Combine(folder, $".draw-arr-{Guid.NewGuid().ToString("N")[..16]}.tmp");
            if (IsReadOnly(target))
                return Result.Fail(ErrorCode.FileUnavailable, $"{path} is read-only, so draw-arr won't replace it.");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            var mode = OperatingSystem.IsWindows() ? default : File.Exists(full) ? File.GetUnixFileMode(full) : NewFileMode;
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                // Set after creating, because the umask would trim a mode given at creation.
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(stream.SafeFileHandle, mode);
                DrawingXml.Write(drawing, stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, full, overwrite: true);
            return Result.Ok();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (temp.Length > 0)
                TryDelete(temp);
            return Result.Fail(ErrorCode.FileUnavailable, $"Can't save {path}: {e.Message}");
        }
    }

    // Owner read and write plus read for others, as a text editor would create it.
    private const UnixFileMode NewFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>Whether an existing file has been made read-only by its owner.</summary>
    private static bool IsReadOnly(FileInfo file) =>
        file.Exists && (OperatingSystem.IsWindows()
            ? file.IsReadOnly
            : (File.GetUnixFileMode(file.FullName) & UnixFileMode.UserWrite) == 0);

    /// <summary>The file a path finally points to, following links, or null if a link leads to a folder.</summary>
    private static FileInfo? Target(FileInfo info) =>
        info.LinkTarget is null ? info : info.ResolveLinkTarget(returnFinalTarget: true) as FileInfo;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temporary file left behind is harmless, and the save's own error is the one worth reporting.
        }
    }
}

/// <summary>What a save does when the path it is given is a link.</summary>
public enum LinkHandling
{
    /// <summary>Write to the file the link points to, leaving the link in place.</summary>
    Follow = 1,
    /// <summary>Refuse, because the path was pinned to a real file and a link there now is someone else's doing.</summary>
    Refuse = 2,
}
