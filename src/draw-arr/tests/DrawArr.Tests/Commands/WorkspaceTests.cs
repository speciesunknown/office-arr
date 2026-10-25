using DrawArr.Commands;
using DrawArr.Core;

namespace DrawArr.Tests.Commands;

public sealed class WorkspaceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public void Commands_for_a_document_that_is_not_open_are_not_found()
    {
        var workspace = new Workspace();
        var id = workspace.Open(new Document(Drawing.Empty));
        workspace.Close(id);

        var reply = workspace.Execute(new CommandRequest(id, new DrawCommand.Show(), CommandSource.Cli));
        Assert.Equal(ErrorCode.NotFound, reply.Error.Code);
    }

    [Fact]
    public void Changes_are_announced_with_the_document_they_were_for()
    {
        var workspace = new Workspace();
        workspace.Open(new Document(Drawing.Empty));
        var second = workspace.Open(new Document(Drawing.Empty));
        var seen = new List<long>();
        workspace.Changed += (_, e) => seen.Add(e.DocumentId);

        workspace.Execute(new CommandRequest(second, new DrawCommand.Show(), CommandSource.Cli));
        workspace.Execute(new CommandRequest(second, new DrawCommand.AddBox(0, 0, 5, 5, Colour.White), CommandSource.Cli));

        Assert.Equal([second], seen);
    }

    [Fact]
    public void An_unknown_source_is_an_input_error()
    {
        var workspace = new Workspace();
        var id = workspace.Open(new Document(Drawing.Empty));
        Assert.Equal(ErrorCode.InvalidInput, workspace.Execute(new CommandRequest(id, new DrawCommand.Show(), 0)).Error.Code);
    }

    [Fact]
    public async Task Requesters_record_their_source_in_the_history()
    {
        var workspace = new Workspace();
        var id = workspace.Open(new Document(Drawing.Empty));
        using var queue = new CommandQueue(workspace);
        using var agent = queue.Join(CommandSource.Agent);
        using var window = queue.Join(CommandSource.Window);

        await agent.Send(id, new DrawCommand.AddBox(0, 0, 5, 5, Colour.White)).WaitAsync(Wait);
        var reply = await window.Send(id, new DrawCommand.SetText(1, "x")).WaitAsync(Wait);

        Assert.Equal([CommandSource.Agent, CommandSource.Window], reply.Value.History.Entries.Select(e => e.Source));
    }

    [Fact]
    public async Task A_stopped_queue_fails_commands_instead_of_running_them()
    {
        var workspace = new Workspace();
        var id = workspace.Open(new Document(Drawing.Empty));
        var queue = new CommandQueue(workspace);
        var cli = queue.Join(CommandSource.Cli);
        queue.Dispose();

        var reply = await cli.Send(id, new DrawCommand.Show()).WaitAsync(Wait);
        Assert.Equal(ErrorCode.Ipc, reply.Error.Code);
    }
}
