using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using TimeArr.Commands;
using TimeArr.Core;

namespace TimeArr.App.Ipc;

/// <summary>A command line forwarded to the running instance.</summary>
public sealed record IpcRequest(string[] Args);

/// <summary>What the running instance answers: an exit code and the text to print.</summary>
public sealed record IpcReply(int ExitCode, string Output);

/// <summary>
/// Wire format: a 4-byte big-endian length, then that many bytes of UTF-8 JSON. One request and
/// one reply per connection. Everything is size-capped and checked; a malformed message is an
/// <see cref="ErrorCode.Ipc"/> Result, never an exception.
/// </summary>
public static class IpcProtocol
{
    /// <summary>Largest request: <see cref="Cli.MaxArgs"/> arguments of <see cref="Cli.MaxArgLength"/>, as escaped JSON, with room to spare.</summary>
    public const int MaxRequestBytes = 16 * 1024;

    /// <summary>Largest reply: a list of 1000 timers fits easily.</summary>
    public const int MaxReplyBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4,
    };

    public static Result Write<T>(Socket socket, T message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        try
        {
            var sent = 0;
            while (sent < frame.Length)
                sent += socket.Send(frame, sent, frame.Length - sent, SocketFlags.None);
            return Result.Ok();
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return Result.Fail(ErrorCode.Ipc, $"Sending to time-arr failed: {e.Message}");
        }
    }

    public static Result<IpcRequest> ReadRequest(Socket socket)
    {
        var body = ReadFrame(socket, MaxRequestBytes);
        if (body.IsFailure)
            return Result<IpcRequest>.Fail(body.Error);
        var request = Deserialize<IpcRequest>(body.Value);
        if (request.IsFailure)
            return request;
        if (request.Value.Args is null || request.Value.Args.Any(a => a is null))
            return Result<IpcRequest>.Fail(ErrorCode.Ipc, "Request has no argument list.");
        return request;
    }

    public static Result<IpcReply> ReadReply(Socket socket)
    {
        var body = ReadFrame(socket, MaxReplyBytes);
        if (body.IsFailure)
            return Result<IpcReply>.Fail(body.Error);
        var reply = Deserialize<IpcReply>(body.Value);
        if (reply.IsFailure)
            return reply;
        if (reply.Value.Output is null || reply.Value.ExitCode is < 0 or > 255)
            return Result<IpcReply>.Fail(ErrorCode.Ipc, "Reply from time-arr is malformed.");
        return reply;
    }

    private static Result<T> Deserialize<T>(byte[] body)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(body, Json);
            return value is null ? Result<T>.Fail(ErrorCode.Ipc, "Empty message.") : value;
        }
        catch (JsonException e)
        {
            return Result<T>.Fail(ErrorCode.Ipc, $"Malformed message: {e.Message}");
        }
    }

    private static Result<byte[]> ReadFrame(Socket socket, int maxBytes)
    {
        try
        {
            var header = new byte[4];
            if (!ReadExactly(socket, header))
                return Result<byte[]>.Fail(ErrorCode.Ipc, "Connection closed before a message arrived.");
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length <= 0 || length > maxBytes)
                return Result<byte[]>.Fail(ErrorCode.Ipc, $"Message size {length} is out of range (1..{maxBytes}).");
            var body = new byte[length];
            return ReadExactly(socket, body)
                ? body
                : Result<byte[]>.Fail(ErrorCode.Ipc, "Connection closed in the middle of a message.");
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut || e.SocketErrorCode == SocketError.WouldBlock)
        {
            return Result<byte[]>.Fail(ErrorCode.Ipc, "Timed out waiting for time-arr.");
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return Result<byte[]>.Fail(ErrorCode.Ipc, $"Reading from time-arr failed: {e.Message}");
        }
    }

    private static bool ReadExactly(Socket socket, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = socket.Receive(buffer, read, buffer.Length - read, SocketFlags.None);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }
}
