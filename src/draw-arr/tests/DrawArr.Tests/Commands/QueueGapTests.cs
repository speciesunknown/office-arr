using System.Collections.Concurrent;
using DrawArr.Commands;
using DrawArr.Core;

namespace DrawArr.Tests.Commands;

/// <summary>The command queue and workspace with several requesters on one or more documents.</summary>
public sealed class QueueGapTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Three_sources_editing_one_document_at_once_all_land_in_one_history()
    {
        var workspace = new Workspace();
        var doc = workspace.Open(new Document(Drawing.Empty));
        var changes = 0;
        workspace.Changed += (_, _) => Interlocked.Increment(ref changes);
        using var queue = new CommandQueue(workspace);
        CommandSource[] sources = [CommandSource.Window, CommandSource.Cli, CommandSource.Agent];

        var sends = sources.Select(source => Task.Run(async () =>
        {
            using var requester = queue.Join(source);
            var replies = new List<Result<DocumentReply>>();
            for (var i = 0; i < 50; i++)
                replies.Add(await requester.Send(doc, new DrawCommand.AddBox(i * 10, 0, 5, 5, Colour.White)));
            return replies;
        })).ToArray();
        var all = (await Task.WhenAll(sends).WaitAsync(Wait)).SelectMany(r => r).ToList();

        Assert.All(all, r => Assert.True(r.IsSuccess, r.ToString()));
        Assert.Equal(Enumerable.Range(1, 150).Select(i => (long)i), all.Select(r => r.Value.ItemId!.Value).Order());
        var show = await queue.Join(CommandSource.Window).Send(doc, new DrawCommand.Show());
        Assert.Equal(150, show.Value.History.Entries.Length);
        Assert.All(sources, s => Assert.Equal(50, show.Value.History.Entries.Count(e => e.Source == s)));
        Assert.Equal(150, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task Undo_from_any_requester_takes_back_the_latest_change_whoever_made_it()
    {
        var workspace = new Workspace();
        var doc = workspace.Open(new Document(Drawing.Empty));
        using var queue = new CommandQueue(workspace);
        using var window = queue.Join(CommandSource.Window);
        using var agent = queue.Join(CommandSource.Agent);

        await window.Send(doc, new DrawCommand.AddBox(0, 0, 40, 20, Colour.White));
        await agent.Send(doc, new DrawCommand.SetText(1, "agent"));
        var undone = await window.Send(doc, new DrawCommand.Undo());

        Assert.Equal("", undone.Value.Drawing.Boxes[1].Text);
        Assert.Equal(CommandSource.Agent, undone.Value.History.Entries[^1].Source);
        Assert.Equal(1, undone.Value.History.Applied);
    }

    [Fact]
    public async Task Documents_are_separate_with_their_own_ids_and_histories()
    {
        var workspace = new Workspace();
        var a = workspace.Open(new Document(Drawing.Empty));
        var b = workspace.Open(new Document(Drawing.Empty));
        using var queue = new CommandQueue(workspace);
        using var requester = queue.Join(CommandSource.Cli);

        var inA = await requester.Send(a, new DrawCommand.AddBox(0, 0, 5, 5, Colour.White));
        var inB = await requester.Send(b, new DrawCommand.AddBox(0, 0, 5, 5, Colour.White));
        await requester.Send(a, new DrawCommand.Undo());
        var showB = await requester.Send(b, new DrawCommand.Show());

        Assert.Equal((1L, 1L), (inA.Value.ItemId!.Value, inB.Value.ItemId!.Value));
        Assert.Single(showB.Value.Drawing.Boxes);
        Assert.Equal(1, showB.Value.History.Applied);
    }

    [Fact]
    public async Task Commands_queued_for_a_document_closed_meanwhile_fail_as_not_found()
    {
        var workspace = new Workspace();
        var doc = workspace.Open(new Document(Drawing.Empty));
        using var queue = new CommandQueue(workspace);
        using var requester = queue.Join(CommandSource.Agent);
        var sends = new List<Task<Result<DocumentReply>>>();

        for (var i = 0; i < 200; i++)
        {
            sends.Add(requester.Send(doc, new DrawCommand.AddBox(0, i * 5, 5, 5, Colour.White)));
            if (i == 100)
                workspace.Close(doc);
        }
        var replies = await Task.WhenAll(sends).WaitAsync(Wait);

        Assert.All(replies, r => Assert.True(r.IsSuccess || r.Error.Code == ErrorCode.NotFound, r.ToString()));
        Assert.Equal(ErrorCode.NotFound, replies[^1].Error.Code);
    }

    [Fact]
    public async Task Ids_stay_unique_when_many_requesters_add_and_undo_at_once()
    {
        var workspace = new Workspace();
        var doc = workspace.Open(new Document(Drawing.Empty));
        using var queue = new CommandQueue(workspace);
        var ids = new ConcurrentBag<long>();

        await Task.WhenAll(Enumerable.Range(0, 6).Select(n => Task.Run(async () =>
        {
            using var requester = queue.Join((CommandSource)((n % 3) + 1));
            for (var i = 0; i < 40; i++)
            {
                var added = await requester.Send(doc, new DrawCommand.AddBox(0, 0, 5, 5, Colour.White));
                ids.Add(added.Value.ItemId!.Value);
                if (i % 4 == 0)
                    await requester.Send(doc, new DrawCommand.Undo());
            }
        }))).WaitAsync(Wait);

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void A_null_command_through_the_workspace_is_an_input_error()
    {
        var workspace = new Workspace();
        var doc = workspace.Open(new Document(Drawing.Empty));

        Assert.Equal(ErrorCode.InvalidInput, workspace.Execute(new CommandRequest(doc, null!, CommandSource.Cli)).Error.Code);
    }

    [Fact]
    public void Failures_and_changes_that_do_nothing_are_not_announced()
    {
        var workspace = new Workspace();
        var doc = workspace.Open(new Document(Drawing.Empty));
        var changes = 0;
        workspace.Changed += (_, _) => changes++;

        workspace.Execute(new CommandRequest(doc, new DrawCommand.AddBox(0, 0, 5, 5, Colour.White), CommandSource.Window));
        workspace.Execute(new CommandRequest(doc, new DrawCommand.MoveBox(1, 0, 0), CommandSource.Window));
        workspace.Execute(new CommandRequest(doc, new DrawCommand.Show(), CommandSource.Window));
        workspace.Execute(new CommandRequest(doc, new DrawCommand.Delete(99), CommandSource.Window));
        workspace.Execute(new CommandRequest(doc, new DrawCommand.Undo(), CommandSource.Window));

        Assert.Equal(2, changes);
    }

    [Fact]
    public void Joining_with_an_unknown_source_is_a_bug()
    {
        using var queue = new CommandQueue(new Workspace());

        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Join((CommandSource)0));
    }

    [Fact]
    public void Closing_twice_is_harmless()
    {
        var workspace = new Workspace();
        var doc = workspace.Open(new Document(Drawing.Empty));

        Assert.True(workspace.Close(doc));
        Assert.False(workspace.Close(doc));
    }
}
