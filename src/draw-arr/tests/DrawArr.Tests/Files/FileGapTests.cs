using DrawArr.Core;
using DrawArr.Files;

namespace DrawArr.Tests.Files;

/// <summary>Load, save and the autosave folder on a real disk, beyond <see cref="DrawingFileTests"/>.</summary>
public sealed class FileGapTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("draw-arr-tests-").FullName;

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var dir in Directory.GetDirectories(_folder, "*", SearchOption.AllDirectories).Prepend(_folder))
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Directory.Delete(_folder, recursive: true);
    }

    private string PathOf(string name) => Path.Combine(_folder, name);

    private static Drawing Sample(string text = "hello") => Drawing.Empty.With(Make.Box(1, 0, 0, 10, 10, Colour.Green, text));

    private static bool IsRoot => Environment.UserName == "root";

    private string[] Leftovers() => Directory.GetFiles(_folder, "*.tmp", SearchOption.AllDirectories);

    // --- saving ---

    [Fact]
    public void Saving_through_a_link_updates_the_file_it_points_to_and_keeps_the_link()
    {
        if (OperatingSystem.IsWindows())
            return;
        var real = PathOf("real.xml");
        var link = PathOf("link.xml");
        Assert.True(DrawingFile.Save(Sample("old"), real).IsSuccess);
        File.CreateSymbolicLink(link, real);

        Assert.True(DrawingFile.Save(Sample("new"), link).IsSuccess);

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("new", DrawingFile.Load(real).Value.Boxes[1].Text);
    }

    [Fact]
    public void Saving_keeps_a_group_and_world_writable_mode_whatever_the_umask()
    {
        if (OperatingSystem.IsWindows())
            return;
        var path = PathOf("shared.xml");
        Assert.True(DrawingFile.Save(Sample(), path).IsSuccess);
        var shared = (UnixFileMode)0b110_110_110; // 0666
        File.SetUnixFileMode(path, shared);

        Assert.True(DrawingFile.Save(Sample("again"), path).IsSuccess);

        Assert.Equal(shared, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Saving_over_a_read_only_file_is_refused()
    {
        // The rename only needs write permission on the folder, so a file the user made read-only is replaced without a word.
        if (OperatingSystem.IsWindows() || IsRoot)
            return;
        var path = PathOf("protected.xml");
        Assert.True(DrawingFile.Save(Sample("keep me"), path).IsSuccess);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var saved = DrawingFile.Save(Sample("overwritten"), path);

        Assert.True(saved.IsFailure, "A read-only file was replaced.");
        Assert.Equal("keep me", DrawingFile.Load(path).Value.Boxes[1].Text);
    }

    [Fact]
    public void Saving_into_a_read_only_folder_fails_cleanly_and_keeps_the_old_file()
    {
        if (OperatingSystem.IsWindows() || IsRoot)
            return;
        var sub = Directory.CreateDirectory(PathOf("ro")).FullName;
        var path = Path.Combine(sub, "a.xml");
        Assert.True(DrawingFile.Save(Sample("old"), path).IsSuccess);
        File.SetUnixFileMode(sub, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var saved = DrawingFile.Save(Sample("new"), path);

        Assert.Equal(ErrorCode.FileUnavailable, saved.Error.Code);
        Assert.Equal("old", DrawingFile.Load(path).Value.Boxes[1].Text);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void A_file_name_near_the_length_limit_can_be_saved()
    {
        // The temporary name adds a dot, 32 hex digits and ".tmp" (38 characters), so a 220-character name pushes it past 255.
        var path = PathOf(new string('n', 216) + ".xml");

        var saved = DrawingFile.Save(Sample(), path);

        Assert.True(saved.IsSuccess, saved.ToString());
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void A_new_file_is_readable_by_everyone_and_writable_by_its_owner()
    {
        if (OperatingSystem.IsWindows())
            return;
        var path = PathOf("new.xml");

        Assert.True(DrawingFile.Save(Sample(), path).IsSuccess);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(path));
    }

    [Fact]
    public void A_private_file_stays_private_when_saved_again()
    {
        if (OperatingSystem.IsWindows())
            return;
        var path = PathOf("private.xml");
        Assert.True(DrawingFile.Save(Sample(), path).IsSuccess);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.True(DrawingFile.Save(Sample("again"), path).IsSuccess);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Many_saves_in_a_row_leave_one_file_and_no_temporaries()
    {
        var path = PathOf("busy.xml");

        for (var i = 0; i < 50; i++)
            Assert.True(DrawingFile.Save(Sample($"v{i}"), path).IsSuccess);

        Assert.Equal("v49", DrawingFile.Load(path).Value.Boxes[1].Text);
        Assert.Equal([path], Directory.GetFiles(_folder));
    }

    [Fact]
    public void Concurrent_saves_to_one_file_leave_one_whole_drawing()
    {
        var path = PathOf("race.xml");

        Parallel.For(0, 20, i => DrawingFile.Save(Sample($"writer {i}"), path));

        Assert.StartsWith("writer ", DrawingFile.Load(path).Value.Boxes[1].Text, StringComparison.Ordinal);
        Assert.Empty(Leftovers());
    }

    // --- loading ---

    [Fact]
    public void Loading_a_folder_is_reported_not_thrown()
    {
        var loaded = DrawingFile.Load(_folder);

        Assert.True(loaded.IsFailure);
    }

    [Fact]
    public void Loading_a_dangling_link_or_a_link_to_a_folder_is_reported()
    {
        if (OperatingSystem.IsWindows())
            return;
        var dangling = PathOf("dangling.xml");
        var toFolder = PathOf("to-folder.xml");
        File.CreateSymbolicLink(dangling, PathOf("missing.xml"));
        File.CreateSymbolicLink(toFolder, _folder);

        Assert.Equal(ErrorCode.FileUnavailable, DrawingFile.Load(dangling).Error.Code);
        Assert.Equal(ErrorCode.FileUnavailable, DrawingFile.Load(toFolder).Error.Code);
    }

    [Fact]
    public void Loading_an_unreadable_file_is_reported()
    {
        if (OperatingSystem.IsWindows() || IsRoot)
            return;
        var path = PathOf("secret.xml");
        Assert.True(DrawingFile.Save(Sample(), path).IsSuccess);
        File.SetUnixFileMode(path, UnixFileMode.None);

        Assert.Equal(ErrorCode.FileUnavailable, DrawingFile.Load(path).Error.Code);
    }

    [Fact]
    public void A_file_of_exactly_the_size_limit_is_read_rather_than_refused_for_size()
    {
        var path = PathOf("big.xml");
        var head = $"""<drawing xmlns="{DrawingXml.Namespace}" version="1"><!--""";
        const string tail = "--></drawing>";
        File.WriteAllText(path, head + new string('x', (int)DrawingFile.MaxBytes - head.Length - tail.Length) + tail);
        Assert.Equal(DrawingFile.MaxBytes, new FileInfo(path).Length);

        var loaded = DrawingFile.Load(path);

        Assert.True(loaded.IsSuccess, loaded.ToString());
    }

    // --- the autosave folder ---

    [Fact]
    public void An_existing_autosave_folder_open_to_others_is_tightened()
    {
        if (OperatingSystem.IsWindows())
            return;
        var path = Directory.CreateDirectory(PathOf("autosave")).FullName;
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101); // 0755

        Assert.True(new AutosaveFolder(path).NewFile(new ManualClock()).IsSuccess);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Folders_created_on_the_way_to_the_autosave_folder_are_private_too()
    {
        if (OperatingSystem.IsWindows())
            return;
        var parent = PathOf("draw-arr");

        Assert.True(new AutosaveFolder(Path.Combine(parent, "autosave")).NewFile(new ManualClock()).IsSuccess);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(parent));
    }

    [Fact]
    public void An_autosave_folder_that_cannot_be_created_is_reported()
    {
        if (OperatingSystem.IsWindows() || IsRoot)
            return;
        var locked = Directory.CreateDirectory(PathOf("locked")).FullName;
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var file = new AutosaveFolder(Path.Combine(locked, "autosave")).NewFile(new ManualClock());

        Assert.Equal(ErrorCode.FileUnavailable, file.Error.Code);
    }

    [Fact]
    public void An_autosave_name_carries_the_local_time_and_saves()
    {
        var file = new AutosaveFolder(PathOf("autosave")).NewFile(new ManualClock()).Value;

        Assert.StartsWith("drawing-20261025-120000-", Path.GetFileName(file), StringComparison.Ordinal);
        Assert.True(DrawingFile.Save(Sample(), file).IsSuccess);
    }
}
