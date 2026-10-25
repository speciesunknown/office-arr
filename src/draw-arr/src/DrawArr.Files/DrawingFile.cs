using DrawArr.Core;

namespace DrawArr.Files;

/// <summary>Drawings on disk, loaded with a size cap and saved so a crash mid-save never leaves a half-written file.</summary>
public static class DrawingFile
{
    /// <summary>The largest file draw-arr will open, in bytes.</summary>
    public const long MaxBytes = 16 * 1024 * 1024;

    /// <summary>Loads a drawing, or an error saying why the file can't be used.</summary>
    public static Result<Drawing> Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (BadPath(path) is { } bad)
            return Result<Drawing>.Fail(bad);
        try
        {
            var info = Target(new FileInfo(path));
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

    /// <summary>Saves a drawing by writing a temporary file beside it and moving that into place, writing through a link to the file it points to.</summary>
    public static Result Save(Drawing drawing, string path)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        ArgumentNullException.ThrowIfNull(path);
        if (BadPath(path) is { } bad)
            return Result.Fail(bad);
        var temp = "";
        try
        {
            // The link's target is replaced rather than the link, so a linked file stays linked.
            var target = Target(new FileInfo(path));
            if (target is null || Directory.Exists(target.FullName))
                return Result.Fail(ErrorCode.FileUnavailable, $"{path} is a folder.");
            var full = target.FullName;
            var folder = Path.GetDirectoryName(full) ?? throw new InvalidOperationException($"{full} has no folder.");
            temp = Path.Combine(folder, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = File.Exists(full) ? File.GetUnixFileMode(full) : NewFileMode;
            using (var stream = new FileStream(temp, options))
            {
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
