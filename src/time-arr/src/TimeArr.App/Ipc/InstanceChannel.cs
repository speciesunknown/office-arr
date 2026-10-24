using System.Net.Sockets;
using TimeArr.Core;

namespace TimeArr.App.Ipc;

/// <summary>
/// Where the one running instance listens: <c>time-arr.sock</c> next to the database, in the
/// user's own time-arr folder (owner-only, 0700). Ownership is decided by an exclusive lock on
/// <c>time-arr.lock</c>, held for the life of the process, so two instances racing to start
/// can't both become the owner; the OS drops the lock if the process dies.
/// </summary>
public static class InstanceChannel
{
    public const string SocketFileName = "time-arr.sock";
    public const string LockFileName = "time-arr.lock";

    /// <summary>Unix limit on a socket path (sun_path is 108 bytes, including the terminator).</summary>
    private const int MaxSocketPathBytes = 107;

    private const UnixFileMode OwnerOnlyFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Creates <paramref name="folder"/> owner-only if it's missing; refuses a symlink.</summary>
    public static Result PrepareFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                if (OperatingSystem.IsWindows())
                    Directory.CreateDirectory(folder);
                else
                    Directory.CreateDirectory(folder, OwnerOnlyFolder);
            }
            if (new DirectoryInfo(folder).LinkTarget is not null)
                return Result.Fail(ErrorCode.Ipc, $"'{folder}' is a symbolic link; time-arr won't put its socket there.");
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(folder);
                if ((mode & ~OwnerOnlyFolder) != 0)
                    File.SetUnixFileMode(folder, mode & OwnerOnlyFolder);
            }
            return Result.Ok();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(ErrorCode.Ipc, $"Cannot prepare '{folder}': {e.Message}");
        }
    }

    public static Result<string> SocketPath(string folder)
    {
        var path = Path.Combine(folder, SocketFileName);
        return System.Text.Encoding.UTF8.GetByteCount(path) > MaxSocketPathBytes
            ? Result<string>.Fail(ErrorCode.Ipc, $"Socket path is too long for this system: '{path}'.")
            : path;
    }

    /// <summary>Tries to become the owner. Fails with <see cref="ErrorCode.Conflict"/> if another process holds the lock.</summary>
    public static Result<FileStream> TryLock(string folder)
    {
        var path = Path.Combine(folder, LockFileName);
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                // FileShare.None is an exclusive advisory lock (flock) on Unix, a share lock on Windows.
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = OwnerOnlyFile;
            return new FileStream(path, options);
        }
        catch (IOException) when (File.Exists(path))
        {
            return Result<FileStream>.Fail(ErrorCode.Conflict, "Another time-arr is starting or running.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<FileStream>.Fail(ErrorCode.Ipc, $"Cannot open '{path}': {e.Message}");
        }
    }

    /// <summary>
    /// Sends <paramref name="args"/> to the running instance and returns its reply.
    /// <see cref="ErrorCode.NotFound"/> means nobody is listening.
    /// </summary>
    public static Result<IpcReply> Send(string socketPath, string[] args, TimeSpan timeout)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.SendTimeout = socket.ReceiveTimeout = (int)timeout.TotalMilliseconds;
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable
                                            or SocketError.HostNotFound)
        {
            return Result<IpcReply>.Fail(ErrorCode.NotFound, "No time-arr is running.");
        }
        catch (SocketException e)
        {
            // ENOENT shows up as various codes depending on the platform; no socket file means no server.
            return File.Exists(socketPath)
                ? Result<IpcReply>.Fail(ErrorCode.Ipc, $"Cannot reach the running time-arr: {e.Message}")
                : Result<IpcReply>.Fail(ErrorCode.NotFound, "No time-arr is running.");
        }

        var sent = IpcProtocol.Write(socket, new IpcRequest(args));
        return sent.IsFailure ? Result<IpcReply>.Fail(sent.Error) : IpcProtocol.ReadReply(socket);
    }

    /// <summary>
    /// Binds the listening socket. Only call while holding the lock: then any socket file already
    /// there is a leftover from a process that died, and is replaced.
    /// </summary>
    public static Result<Socket> Listen(string socketPath)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            if (File.Exists(socketPath))
                File.Delete(socketPath);
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(socketPath, OwnerOnlyFile);
            socket.Listen(16);
            return socket;
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException)
        {
            socket.Dispose();
            return Result<Socket>.Fail(ErrorCode.Ipc, $"Cannot listen on '{socketPath}': {e.Message}");
        }
    }
}
