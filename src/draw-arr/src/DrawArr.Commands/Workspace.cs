using System.Collections.Concurrent;
using DrawArr.Core;

namespace DrawArr.Commands;

/// <summary>A command for one open document, with who sent it.</summary>
public sealed record CommandRequest(long DocumentId, DrawCommand Command, CommandSource Source);

/// <summary>Where requests go, with <see cref="Requester"/> safe from any thread and <see cref="Workspace"/> for one thread at a time.</summary>
public interface IDrawCommands
{
    Result<DocumentReply> Execute(CommandRequest request);
}

/// <summary>The documents open in the running instance, each known by an id, and the one place their commands run.</summary>
/// <remarks>Opening and closing are safe from any thread, while <see cref="Execute"/> is for the command queue's worker only.</remarks>
public sealed class Workspace : IDrawCommands
{
    private readonly ConcurrentDictionary<long, Document> _documents = new();
    private long _lastDocumentId;

    /// <summary>Raised on the queue's worker after a command changes a document.</summary>
    public event EventHandler<DocumentChangedEventArgs>? Changed;

    /// <summary>Adds an open document and returns its id.</summary>
    public long Open(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var id = Interlocked.Increment(ref _lastDocumentId);
        _documents[id] = document;
        return id;
    }

    /// <summary>Forgets a document straight away, so later commands for it fail as not found, where <see cref="DrawCommand.CloseDocument"/> is the way to close one in turn.</summary>
    public bool Close(long documentId) => _documents.TryRemove(documentId, out _);

    /// <summary>Puts back a document closed by <see cref="DrawCommand.CloseDocument"/>, as a close whose save failed does.</summary>
    public void Reopen(long documentId, Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!_documents.TryAdd(documentId, document))
            throw new InvalidOperationException($"Document {documentId} is already open.");
    }

    public Result<DocumentReply> Execute(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Command is null)
            return Result<DocumentReply>.Fail(ErrorCode.InvalidInput, "No command given.");
        if (!Enum.IsDefined(request.Source))
            return Result<DocumentReply>.Fail(ErrorCode.InvalidInput, "Not a known command source.");
        if (!_documents.TryGetValue(request.DocumentId, out var document))
            return Result<DocumentReply>.Fail(ErrorCode.NotFound, $"There is no open document with id {request.DocumentId}.");

        var reply = document.Execute(request.Command, request.Source);
        // Removed here, on the worker, so every command queued before the close has run and none after it can.
        if (request.Command is DrawCommand.CloseDocument)
            _documents.TryRemove(request.DocumentId, out _);
        if (reply.IsSuccess && reply.Value.Change == DocumentChange.Changed)
            Changed?.Invoke(this, new DocumentChangedEventArgs(request.DocumentId, reply.Value));
        return reply;
    }
}

/// <summary>Which document changed and what it is now.</summary>
public sealed class DocumentChangedEventArgs(long documentId, DocumentReply reply) : EventArgs
{
    public long DocumentId { get; } = documentId;
    public DocumentReply Reply { get; } = reply;
}
