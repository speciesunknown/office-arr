using System.Diagnostics;
using DrawArr.Core;
using DrawArr.Files;

namespace DrawArr.Tests.Files;

public sealed class DrawingFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("draw-arr-tests-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string PathOf(string name) => Path.Combine(_folder, name);

    private static Drawing Sample() => Drawing.Empty.With(Make.Box(1, 0, 0, 10, 10, Colour.Green, "hello"));

    [Fact]
    public void A_saved_drawing_loads_back()
    {
        var path = PathOf("a.xml");
        Assert.True(DrawingFile.Save(Sample(), path).IsSuccess);
        Assert.Equal("hello", DrawingFile.Load(path).Value.Boxes[1].Text);
    }

    [Fact]
    public void Saving_replaces_the_old_file_and_leaves_no_temporary_files()
    {
        var path = PathOf("a.xml");
        DrawingFile.Save(Drawing.Empty, path);
        DrawingFile.Save(Sample(), path);

        Assert.Equal(1, DrawingFile.Load(path).Value.Count);
        Assert.Equal(["a.xml"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void Saving_keeps_the_permissions_of_the_file_it_replaces()
    {
        if (OperatingSystem.IsWindows())
            return;
        var path = PathOf("a.xml");
        DrawingFile.Save(Drawing.Empty, path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        DrawingFile.Save(Sample(), path);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Saving_into_a_missing_folder_is_reported()
    {
        var saved = DrawingFile.Save(Sample(), PathOf("missing/a.xml"));
        Assert.Equal(ErrorCode.FileUnavailable, saved.Error.Code);
    }

    [Fact]
    public void Saving_over_a_folder_is_reported()
    {
        Directory.CreateDirectory(PathOf("dir"));
        Assert.Equal(ErrorCode.FileUnavailable, DrawingFile.Save(Sample(), PathOf("dir")).Error.Code);
    }

    [Fact]
    public void Loading_a_missing_file_is_reported()
    {
        Assert.Equal(ErrorCode.FileUnavailable, DrawingFile.Load(PathOf("none.xml")).Error.Code);
    }

    [Fact]
    public void Loading_an_empty_file_is_reported()
    {
        File.WriteAllText(PathOf("empty.xml"), "");
        Assert.Equal(ErrorCode.FileInvalid, DrawingFile.Load(PathOf("empty.xml")).Error.Code);
    }

    [Fact]
    public void Loading_a_file_that_is_too_big_is_refused_before_reading_it()
    {
        var path = PathOf("big.xml");
        using (var stream = File.Create(path))
            stream.SetLength(DrawingFile.MaxBytes + 1);
        Assert.Contains("too big", DrawingFile.Load(path).Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loading_a_damaged_file_names_the_file()
    {
        var path = PathOf("bad.xml");
        File.WriteAllText(path, "<drawing>");
        var error = DrawingFile.Load(path).Error;
        Assert.Equal(ErrorCode.FileInvalid, error.Code);
        Assert.StartsWith(path, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loading_a_pipe_or_device_does_not_block()
    {
        if (OperatingSystem.IsWindows())
            return;
        var fifo = PathOf("fifo");
        using (var mkfifo = Process.Start("mkfifo", fifo))
            mkfifo.WaitForExit();

        Assert.Equal(ErrorCode.FileInvalid, DrawingFile.Load(fifo).Error.Code);
        Assert.Equal(ErrorCode.FileInvalid, DrawingFile.Load("/dev/zero").Error.Code);
    }

    [Fact]
    public void Loading_through_a_link_reads_the_file_it_points_to()
    {
        var path = PathOf("a.xml");
        DrawingFile.Save(Sample(), path);
        File.CreateSymbolicLink(PathOf("link.xml"), path);
        Assert.Equal(1, DrawingFile.Load(PathOf("link.xml")).Value.Count);
    }

    [Fact]
    public void Saving_through_a_link_writes_the_file_it_points_to_and_keeps_the_link()
    {
        Directory.CreateDirectory(PathOf("real"));
        var target = PathOf("real/a.xml");
        DrawingFile.Save(Drawing.Empty, target);
        var link = PathOf("link.xml");
        File.CreateSymbolicLink(link, target);

        Assert.True(DrawingFile.Save(Sample(), link).IsSuccess);

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(1, DrawingFile.Load(target).Value.Count);
        Assert.Equal(["a.xml"], Directory.GetFiles(PathOf("real")).Select(Path.GetFileName));
    }

    [Fact]
    public void Paths_that_cannot_name_a_file_are_reported_not_thrown()
    {
        foreach (var path in new[] { "", "a" + '\0' + "b.xml" })
        {
            Assert.Equal(ErrorCode.FileUnavailable, DrawingFile.Load(path).Error.Code);
            Assert.Equal(ErrorCode.FileUnavailable, DrawingFile.Save(Sample(), path).Error.Code);
        }
    }

    [Fact]
    public void An_autosave_folder_that_is_a_link_is_refused()
    {
        Directory.CreateDirectory(PathOf("elsewhere"));
        Directory.CreateSymbolicLink(PathOf("autosave"), PathOf("elsewhere"));
        Assert.Equal(ErrorCode.FileUnavailable, new AutosaveFolder(PathOf("autosave")).NewFile(new ManualClock()).Error.Code);
    }

    [Fact]
    public void An_existing_autosave_folder_is_made_private()
    {
        if (OperatingSystem.IsWindows())
            return;
        var path = PathOf("autosave");
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);

        new AutosaveFolder(path).NewFile(new ManualClock());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
    }

    [Fact]
    public void The_autosave_folder_is_private_to_the_user()
    {
        var folder = new AutosaveFolder(PathOf("autosave"));
        var file = folder.NewFile(new ManualClock()).Value;

        Assert.StartsWith(Path.Combine(_folder, "autosave", "drawing-20261025-120000-"), file, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder.Path));
    }

    [Fact]
    public void Autosave_file_names_do_not_repeat()
    {
        var folder = new AutosaveFolder(PathOf("autosave"));
        var clock = new ManualClock();
        Assert.NotEqual(folder.NewFile(clock).Value, folder.NewFile(clock).Value);
    }

    [Fact]
    public void An_autosave_folder_that_is_not_a_full_path_is_reported()
    {
        Assert.Equal(ErrorCode.FileUnavailable, new AutosaveFolder("relative").NewFile(new ManualClock()).Error.Code);
    }

    [Fact]
    public void A_save_that_refuses_links_never_writes_through_one_planted_in_its_place()
    {
        var precious = PathOf("precious.txt");
        File.WriteAllText(precious, "keep me");
        var path = PathOf("plan.xml");
        File.CreateSymbolicLink(path, precious);

        var saved = DrawingFile.Save(Sample(), path, LinkHandling.Refuse);

        Assert.Equal(ErrorCode.FileUnavailable, saved.Error.Code);
        Assert.Equal("keep me", File.ReadAllText(precious));
    }

    [Fact]
    public void The_real_path_of_a_link_is_the_file_it_points_to()
    {
        var path = PathOf("a.xml");
        DrawingFile.Save(Sample(), path);
        File.CreateSymbolicLink(PathOf("link.xml"), path);
        Assert.Equal(path, DrawingFile.RealPath(PathOf("link.xml")));
        Assert.Null(DrawingFile.RealPath("a" + '\0'));
    }
}
