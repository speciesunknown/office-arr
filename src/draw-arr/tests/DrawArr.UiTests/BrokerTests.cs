using System.Net.Sockets;
using DrawArr.App;
using DrawArr.App.Ipc;
using DrawArr.Core;
using DrawArr.Files;

namespace DrawArr.UiTests;

/// <summary>The broker against a real app host, and the IPC channel end to end, without any windows.</summary>
public sealed class BrokerTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("draw-arr-broker-").FullName;
    private readonly AppHost _host;
    private readonly Broker _broker;
    private readonly List<DocumentSession> _raised = [];

    public BrokerTests()
    {
        _host = new AppHost(TimeProvider.System, new AutosaveFolder(Path.Combine(_folder, "autosave")), action => action());
        _broker = new Broker(_host, s => _raised.Add(s));
        DrawingFile.Save(Drawing.Empty, Path.Combine(_folder, "plan.xml"));
    }

    public void Dispose()
    {
        _host.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private IpcReply Run(params string[] args) => _broker.Handle(args, _folder);

    [Fact]
    public void An_edit_to_a_file_that_is_not_open_opens_it_and_goes_in_its_history_as_the_cli()
    {
        var reply = Run("add-box", "plan.xml", "1", "2", "40", "20", "--text", "hi");

        Assert.Equal(0, reply.ExitCode);
        Assert.Equal("Add box 1 at (0, 0) 40x20", reply.Output);
        var session = Assert.Single(_host.Sessions);
        Assert.Equal(Path.Combine(_folder, "plan.xml"), session.FilePath);
        Assert.Contains("1  ", Run("history", "plan.xml").Output, StringComparison.Ordinal);
        Assert.Contains("cli", Run("history", "plan.xml").Output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_edit_shows_as_an_agent_and_undo_from_anyone_takes_it_back()
    {
        Run("--agent", "add-box", "plan.xml", "0", "0", "10", "10");
        Assert.Contains("agent", Run("history", "plan.xml").Output, StringComparison.Ordinal);

        Assert.Equal(0, Run("undo", "plan.xml").ExitCode);

        Assert.Equal("The drawing is empty.", Run("show", "plan.xml").Output);
    }

    [Fact]
    public void Show_describes_the_drawing_and_list_names_the_open_files()
    {
        Run("add-box", "plan.xml", "0", "0", "40", "20");
        Run("add-box", "plan.xml", "100", "0", "40", "20");
        Run("add-line", "plan.xml", "box:1", "box:2", "--colour", "red");

        Assert.Equal("box 1 at (0, 0) 40x20 white\nbox 2 at (100, 0) 40x20 white\nline 3 from (40, 10) on box 1 to (100, 10) on box 2 red",
            Run("show", "plan.xml").Output.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal($"{Path.Combine(_folder, "plan.xml")}  2 boxes, 1 lines", Run("list").Output);
    }

    [Fact]
    public void Saving_through_the_cli_writes_the_file()
    {
        Run("add-box", "plan.xml", "0", "0", "40", "20");
        Assert.Equal(0, Run("save", "plan.xml").ExitCode);
        Assert.Equal(1, DrawingFile.Load(Path.Combine(_folder, "plan.xml")).Value.Count);
    }

    [Fact]
    public void Problems_are_reported_with_an_exit_code_not_thrown()
    {
        // Saving needs the file open, which nothing has done yet.
        Assert.Equal(1, Run("save", "plan.xml").ExitCode);
        Assert.Equal(2, Run("move-box", "plan.xml", "x", "0", "0").ExitCode);
        Assert.Equal(1, Run("move-box", "plan.xml", "9", "0", "0").ExitCode);
        Assert.Equal(1, Run("show", "missing.xml").ExitCode);
        Assert.Equal(1, Run("show", "a" + '\0' + "b").ExitCode);
    }

    [Fact]
    public void Opening_raises_the_window_of_a_file_already_open()
    {
        Run("open", "plan.xml");
        Run("open", Path.Combine(_folder, "plan.xml"));
        Assert.Single(_host.Sessions);
        Assert.Equal(2, _raised.Count);
    }

    [Fact]
    public void A_command_line_sent_over_the_socket_runs_in_the_instance()
    {
        var socketPath = Path.Combine(_folder, InstanceChannel.SocketFileName);
        var listening = InstanceChannel.Listen(socketPath);
        Assert.True(listening.IsSuccess, listening.ToString());
        using var server = new IpcServer(listening.Value, _broker);
        server.Start();

        var sent = InstanceChannel.Send(socketPath, ["add-box", "plan.xml", "0", "0", "40", "20"], _folder, TimeSpan.FromSeconds(10));

        Assert.True(sent.IsSuccess, sent.ToString());
        Assert.Equal(new IpcReply(0, "Add box 1 at (0, 0) 40x20"), sent.Value);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(socketPath));
    }

    [Fact]
    public void A_request_without_an_absolute_working_folder_is_refused()
    {
        var socketPath = Path.Combine(_folder, InstanceChannel.SocketFileName);
        using var server = new IpcServer(InstanceChannel.Listen(socketPath).Value, _broker);
        server.Start();

        var sent = InstanceChannel.Send(socketPath, ["list"], "relative", TimeSpan.FromSeconds(10));

        Assert.Equal(2, sent.Value.ExitCode);
    }

    [Fact]
    public void Nobody_listening_is_not_found()
    {
        var sent = InstanceChannel.Send(Path.Combine(_folder, "none.sock"), ["list"], _folder, TimeSpan.FromSeconds(1));
        Assert.Equal(ErrorCode.NotFound, sent.Error.Code);
    }
}
