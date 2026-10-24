using System.Globalization;
using Microsoft.Data.Sqlite;
using TimeArr.Core;

namespace TimeArr.Data;

/// <summary>
/// Keeps the timers in one SQLite table, one row each. Opens a short-lived connection per
/// operation, so it is safe to use from any thread and never holds the file open between operations.
/// </summary>
public sealed class SqliteTimerRepository : ITimerRepository
{
    /// <summary>Schema version this build writes, kept in <c>PRAGMA user_version</c>.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Upper bound on the number of timers, so a runaway client cannot fill the disk.</summary>
    public const int MaxTimers = 1000;

    /// <summary>Name of the timer a new database starts with.</summary>
    public const string FirstTimerName = "default";

    /// <summary>Marks the file as time-arr's (<c>PRAGMA application_id</c>): "TmAr" in ASCII.</summary>
    public const int ApplicationId = 0x546D4172;

    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private const string SelectColumns = "SELECT id, name, is_running, started_at_utc, accumulated_ms FROM timers";

    private readonly string _connectionString;

    private SqliteTimerRepository(string path, int busyTimeoutSeconds)
    {
        DatabasePath = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            // The file is created by Open with owner-only permissions; SQLite must not create it.
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            // Seconds to wait for a lock held by another connection before giving up.
            DefaultTimeout = busyTimeoutSeconds,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Where time-arr keeps its database: <c>LocalApplicationData/time-arr/time-arr.db</c>
    /// (on Linux <c>~/.local/share/time-arr/time-arr.db</c>).
    /// </summary>
    public static Result<string> DefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                                             Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(root))
            return Result<string>.Fail(ErrorCode.StorageUnavailable, "Cannot find the local application data folder.");
        return Path.Combine(root, "time-arr", "time-arr.db");
    }

    /// <summary>Opens the database at the default location, creating it if needed.</summary>
    public static Result<SqliteTimerRepository> OpenDefault()
    {
        var path = DefaultPath();
        return path.IsSuccess ? Open(path.Value) : Result<SqliteTimerRepository>.Fail(path.Error);
    }

    /// <summary>
    /// Opens the database at <paramref name="path"/>, creating the folder (owner-only), the file
    /// (owner-only) and the schema if needed, and checks that the file is healthy. A new database
    /// starts with one paused timer named <see cref="FirstTimerName"/>.
    /// </summary>
    /// <param name="busyTimeoutSeconds">How long to wait for a lock held elsewhere before failing with <see cref="ErrorCode.StorageBusy"/>.</param>
    public static Result<SqliteTimerRepository> Open(string path, int busyTimeoutSeconds = 5)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return Result<SqliteTimerRepository>.Fail(ErrorCode.InvalidInput, $"Database path must be absolute: '{path}'.");
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return Result<SqliteTimerRepository>.Fail(ErrorCode.InvalidInput, "Database path contains characters a path can't have.");
        // 0 would mean "wait forever" to ADO.NET.
        if (busyTimeoutSeconds is < 1 or > 300)
            return Result<SqliteTimerRepository>.Fail(ErrorCode.InvalidInput, "Busy timeout must be 1 to 300 seconds.");

        var prepared = PrepareFile(path);
        if (prepared.IsFailure)
            return Result<SqliteTimerRepository>.Fail(prepared.Error);

        var repository = new SqliteTimerRepository(path, busyTimeoutSeconds);
        var initialized = repository.Initialize();
        if (initialized.IsFailure)
            return Result<SqliteTimerRepository>.Fail(initialized.Error);

        var tightened = Tighten(path, prepared.Value);
        return tightened.IsSuccess ? repository : Result<SqliteTimerRepository>.Fail(tightened.Error);
    }

    public Result<IReadOnlyList<TimerEntry>> List() => Run<IReadOnlyList<TimerEntry>>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " ORDER BY id;";

        var timers = new List<TimerEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var timer = ReadRow(reader);
            if (timer.IsFailure)
                return Result<IReadOnlyList<TimerEntry>>.Fail(timer.Error);
            timers.Add(timer.Value);
        }
        return timers;
    });

    public Result<TimerEntry> Get(long id) => Run(connection => ReadById(connection, null, id));

    public Result<TimerEntry> FindByName(string name)
    {
        var valid = TimerName.Validate(name);
        if (valid.IsFailure)
            return Result<TimerEntry>.Fail(valid.Error);

        return Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = SelectColumns + " WHERE name = $name;";
            command.Parameters.AddWithValue("$name", valid.Value);
            return ReadSingle(command, $"No timer named '{valid.Value}'.");
        });
    }

    public Result<TimerEntry> Create(string name)
    {
        var valid = TimerName.Validate(name);
        if (valid.IsFailure)
            return Result<TimerEntry>.Fail(valid.Error);

        return Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);

            var clash = CheckNameFree(connection, transaction, valid.Value, exceptId: null);
            if (clash.IsFailure)
                return Result<TimerEntry>.Fail(clash.Error);
            if (Count(connection, transaction) >= MaxTimers)
                return Result<TimerEntry>.Fail(ErrorCode.Conflict, $"There are already {MaxTimers} timers.");

            var id = Insert(connection, transaction, valid.Value);
            transaction.Commit();
            return new TimerEntry(id, valid.Value, TimerState.Zero);
        });
    }

    public Result<TimerEntry> Rename(long id, string name)
    {
        var valid = TimerName.Validate(name);
        if (valid.IsFailure)
            return Result<TimerEntry>.Fail(valid.Error);

        return Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);

            var current = ReadById(connection, transaction, id);
            if (current.IsFailure)
                return current;
            var clash = CheckNameFree(connection, transaction, valid.Value, exceptId: id);
            if (clash.IsFailure)
                return Result<TimerEntry>.Fail(clash.Error);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE timers SET name = $name WHERE id = $id;";
            command.Parameters.AddWithValue("$name", valid.Value);
            command.Parameters.AddWithValue("$id", id);
            ExpectOneRow(command);

            transaction.Commit();
            return current.Value with { Name = valid.Value };
        });
    }

    public Result Delete(long id) => Run<bool>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM timers WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1
            ? true
            : Result<bool>.Fail(ErrorCode.NotFound, $"No timer with id {id}.");
    }).ToResult();

    public Result<TimerEntry> Update(long id, Func<TimerState, Result<TimerState>> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return Run(connection =>
        {
            // Not deferred: BEGIN IMMEDIATE takes the write lock up front, so two writers
            // cannot both read the old state and then overwrite each other.
            using var transaction = connection.BeginTransaction(deferred: false);

            var current = ReadById(connection, transaction, id);
            if (current.IsFailure)
                return current;

            var next = change(current.Value.State);
            if (next.IsFailure)
                return Result<TimerEntry>.Fail(next.Error);

            WriteState(connection, transaction, id, next.Value);
            transaction.Commit();
            return current.Value with { State = next.Value };
        });
    }

    /// <summary>
    /// Creates the folder and an empty owner-only file if they are missing, refusing symlinks.
    /// Changes nothing about an existing file.
    /// </summary>
    /// <returns>Whether the folder is ours to tighten.</returns>
    private static Result<bool> PrepareFile(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(Path.GetFileName(path)))
            return Result<bool>.Fail(ErrorCode.InvalidInput, $"Database path must name a file inside a folder: '{path}'.");

        try
        {
            var created = false;
            if (!Directory.Exists(directory))
            {
                if (OperatingSystem.IsWindows())
                    Directory.CreateDirectory(directory);
                else
                    Directory.CreateDirectory(directory, DirectoryMode);
                created = true;
            }

            const string symlinkHint = " time-arr refuses symlinks here so its data can't be redirected; use a real folder.";
            if (new DirectoryInfo(directory).LinkTarget is not null)
                return Result<bool>.Fail(ErrorCode.StorageUnavailable, $"Database folder is a symbolic link: '{directory}'.{symlinkHint}");
            if (new FileInfo(path).LinkTarget is not null)
                return Result<bool>.Fail(ErrorCode.StorageUnavailable, $"Database file is a symbolic link: '{path}'.{symlinkHint}");

            if (!File.Exists(path))
                CreateOwnerOnlyFile(path);

            // Only a folder we just made, or the dedicated time-arr folder, is ours to tighten.
            // Someone else's folder (say $HOME or /tmp) keeps its mode.
            return created || IsDefaultFolder(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<bool>.Fail(ErrorCode.StorageUnavailable, $"Cannot prepare database file '{path}': {e.Message}");
        }
    }

    /// <summary>
    /// Removes group and other access left by an older run or by hand. Only ever clears bits:
    /// a file the user made read-only stays read-only. Runs once the file is known to be ours.
    /// </summary>
    private static Result Tighten(string path, bool folderIsOurs)
    {
        if (OperatingSystem.IsWindows())
            return Result.Ok();

        const UnixFileMode groupAndOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                         | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        try
        {
            if (folderIsOurs)
            {
                var directory = Path.GetDirectoryName(path)!;
                File.SetUnixFileMode(directory, File.GetUnixFileMode(directory) & ~groupAndOther);
            }
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) & ~groupAndOther);
            return Result.Ok();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(ErrorCode.StorageUnavailable, $"Cannot restrict access to '{path}': {e.Message}");
        }
    }

    private static bool IsDefaultFolder(string directory)
    {
        var defaultPath = DefaultPath();
        return defaultPath.IsSuccess
            && string.Equals(Path.GetFullPath(directory), Path.GetDirectoryName(defaultPath.Value), StringComparison.Ordinal);
    }

    private static void CreateOwnerOnlyFile(string path)
    {
        var options = new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = FileMode;

        try
        {
            using var _ = new FileStream(path, options);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Someone else created it between our check and now; that is fine.
        }
    }

    private enum FileKind { Empty, Ours, Foreign, TooNew }

    /// <summary>
    /// Checks the file read-only first and changes nothing unless it is empty (then creates the
    /// schema) or already a time-arr database (then makes sure it is in WAL mode). A file that
    /// is someone else's, or from a newer time-arr, is refused untouched.
    /// </summary>
    private Result Initialize()
    {
        var result = Run<bool>(connection =>
        {
            using (var check = connection.CreateCommand())
            {
                check.CommandText = "PRAGMA quick_check;";
                var verdict = check.ExecuteScalar() as string;
                if (verdict != "ok")
                    return Result<bool>.Fail(ErrorCode.StorageCorrupt, $"Database failed its integrity check: {verdict}");
            }

            FileKind kind;
            // One read transaction, so the three values come from the same snapshot even if
            // another process creates the schema while we look.
            using (var look = connection.BeginTransaction(deferred: true))
                kind = Classify(connection, look);

            if (kind == FileKind.Empty)
            {
                // Check again under the write lock: another process may have created it meanwhile.
                using var transaction = connection.BeginTransaction(deferred: false);
                kind = Classify(connection, transaction);
                if (kind == FileKind.Empty)
                {
                    CreateSchema(connection, transaction);
                    transaction.Commit();
                    kind = FileKind.Ours;
                }
            }

            switch (kind)
            {
                case FileKind.Foreign:
                    return Result<bool>.Fail(ErrorCode.StorageCorrupt,
                        $"'{DatabasePath}' is a database that time-arr did not create; leaving it alone.");
                case FileKind.TooNew:
                    return Result<bool>.Fail(ErrorCode.StorageTooNew,
                        $"Database was written by a newer time-arr (schema newer than {SchemaVersion}); leaving it alone.");
            }

            // Fail now, not on first use, if the table is missing or a row holds nonsense.
            // A read transaction: this doesn't block, or wait for, the running app's writes.
            using (var read = connection.BeginTransaction(deferred: true))
            {
                var rows = ReadAll(connection, read);
                if (rows.IsFailure)
                    return Result<bool>.Fail(rows.Error);
            }

            EnableWal(connection);
            return true;
        });
        return result.ToResult();
    }

    /// <summary>What kind of file this is, judged only from its headers and table list.</summary>
    private static FileKind Classify(SqliteConnection connection, SqliteTransaction transaction)
    {
        var applicationId = Scalar(connection, transaction, "PRAGMA application_id;");
        var version = Scalar(connection, transaction, "PRAGMA user_version;");
        var objects = Scalar(connection, transaction, "SELECT count(*) FROM sqlite_schema;");

        if (applicationId == ApplicationId)
            return version > SchemaVersion ? FileKind.TooNew : FileKind.Ours;
        return applicationId == 0 && version == 0 && objects == 0 ? FileKind.Empty : FileKind.Foreign;
    }

    private static void CreateSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var create = connection.CreateCommand();
        create.Transaction = transaction;
        // AUTOINCREMENT: ids of deleted timers are never handed out again, so an old id
        // (say, in a script) can't silently hit a different timer.
        create.CommandText = $"""
            CREATE TABLE timers (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT    NOT NULL UNIQUE COLLATE NOCASE
                                        CHECK (length(name) BETWEEN 1 AND {TimerName.MaxLength}),
                is_running      INTEGER NOT NULL CHECK (is_running IN (0, 1)),
                started_at_utc  TEXT    NULL,
                accumulated_ms  INTEGER NOT NULL CHECK (accumulated_ms >= 0),
                CHECK ((is_running = 1) = (started_at_utc IS NOT NULL))
            ) STRICT;
            PRAGMA application_id = {ApplicationId};
            PRAGMA user_version = {SchemaVersion};
            """;
        create.ExecuteNonQuery();
        Insert(connection, transaction, FirstTimerName);
    }

    private static Result ReadAll(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var all = connection.CreateCommand();
        all.Transaction = transaction;
        all.CommandText = SelectColumns + ";";
        using var reader = all.ExecuteReader();
        while (reader.Read())
        {
            var row = ReadRow(reader);
            if (row.IsFailure)
                return Result.Fail(row.Error);
        }
        return Result.Ok();
    }

    /// <summary>
    /// WAL lets readers and the writer work at the same time; it persists in the file, so this
    /// only does work the first time. Some network or FUSE filesystems can't do WAL; SQLite
    /// then stays in rollback mode, which is slower under contention but still correct.
    /// </summary>
    private static void EnableWal(SqliteConnection connection)
    {
        using var mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        if (mode.ExecuteScalar() as string == "wal")
            return;
        mode.CommandText = "PRAGMA journal_mode = WAL;";
        mode.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static long Count(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM timers;";
        return (long)command.ExecuteScalar()!;
    }

    private static Result CheckNameFree(SqliteConnection connection, SqliteTransaction transaction, string name, long? exceptId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // The column's NOCASE collation applies to this comparison.
        command.CommandText = "SELECT count(*) FROM timers WHERE name = $name AND id IS NOT $except;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$except", exceptId is { } id ? id : DBNull.Value);
        return (long)command.ExecuteScalar()! == 0
            ? Result.Ok()
            : Result.Fail(ErrorCode.Conflict, $"A timer named '{name}' already exists.");
    }

    private static long Insert(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO timers (name, is_running, started_at_utc, accumulated_ms) VALUES ($name, 0, NULL, 0);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", name);
        return (long)command.ExecuteScalar()!;
    }

    private static Result<TimerEntry> ReadById(SqliteConnection connection, SqliteTransaction? transaction, long id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectColumns + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return ReadSingle(command, $"No timer with id {id}.");
    }

    private static Result<TimerEntry> ReadSingle(SqliteCommand command, string notFound)
    {
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : Result<TimerEntry>.Fail(ErrorCode.NotFound, notFound);
    }

    /// <summary>Turns the current row into a timer, refusing anything time-arr would not have written.</summary>
    private static Result<TimerEntry> ReadRow(SqliteDataReader reader)
    {
        if (reader.GetFieldType(0) != typeof(long) || reader.GetFieldType(2) != typeof(long) || reader.GetFieldType(4) != typeof(long))
            return Corrupt("a row has values of the wrong type");

        var id = reader.GetInt64(0);

        if (reader.IsDBNull(1) || reader.GetFieldType(1) != typeof(string))
            return Corrupt($"timer {id} has no valid name");
        var name = reader.GetString(1);
        var validName = TimerName.Validate(name);
        if (validName.IsFailure || validName.Value != name)
            return Corrupt($"timer {id} has an invalid name");

        var isRunning = reader.GetInt64(2) switch
        {
            0 => false,
            1 => true,
            _ => (bool?)null,
        };
        if (isRunning is null)
            return Corrupt($"timer {id} has an invalid running flag");

        DateTimeOffset? startedAt = null;
        if (!reader.IsDBNull(3))
        {
            if (reader.GetFieldType(3) != typeof(string) || !TryParseTimestamp(reader.GetString(3), out var parsed))
                return Corrupt($"timer {id} has an invalid start time");
            startedAt = parsed;
        }

        var accumulatedMs = reader.GetInt64(4);
        if (accumulatedMs < 0 || accumulatedMs > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond)
            return Corrupt($"timer {id} has an invalid accumulated time");

        var state = TimerState.Create(isRunning.Value, startedAt, TimeSpan.FromMilliseconds(accumulatedMs));
        if (state.IsFailure)
            return Corrupt($"timer {id} is inconsistent: {state.Error.Message}");

        return new TimerEntry(id, name, state.Value);

        static Result<TimerEntry> Corrupt(string what) =>
            Result<TimerEntry>.Fail(ErrorCode.StorageCorrupt, $"Database is damaged: {what}.");
    }

    private static void WriteState(SqliteConnection connection, SqliteTransaction transaction, long id, TimerState state)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE timers
            SET is_running = $running, started_at_utc = $started, accumulated_ms = $accumulated
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$running", state.IsRunning ? 1L : 0L);
        command.Parameters.AddWithValue("$started",
            state.StartedAtUtc is { } started ? FormatTimestamp(started) : DBNull.Value);
        command.Parameters.AddWithValue("$accumulated", state.Accumulated.Ticks / TimeSpan.TicksPerMillisecond);
        ExpectOneRow(command);
    }

    /// <summary>For writes to a row this transaction just read: anything but one row is a bug.</summary>
    private static void ExpectOneRow(SqliteCommand command)
    {
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Timer row vanished inside a write transaction.");
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static bool TryParseTimestamp(string text, out DateTimeOffset value)
    {
        var ok = DateTime.TryParseExact(text, TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed);
        value = ok ? new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc)) : default;
        return ok;
    }

    /// <summary>Runs <paramref name="work"/> on a fresh connection, turning SQLite failures into Results.</summary>
    private Result<T> Run<T>(Func<SqliteConnection, Result<T>> work)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return work(connection);
        }
        catch (SqliteException e)
        {
            return Result<T>.Fail(MapError(e));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<T>.Fail(ErrorCode.StorageUnavailable, $"Cannot access database '{DatabasePath}': {e.Message}");
        }
    }

    private Error MapError(SqliteException e)
    {
        // Primary result codes: https://sqlite.org/rescode.html
        var code = e.SqliteErrorCode switch
        {
            5 or 6 => ErrorCode.StorageBusy,                 // BUSY, LOCKED
            11 or 26 => ErrorCode.StorageCorrupt,            // CORRUPT, NOTADB
            // Our SQL is fixed and our values are checked, so these mean the schema is not ours.
            1 or 19 or 20 => ErrorCode.StorageCorrupt,       // ERROR, CONSTRAINT, MISMATCH
            _ => ErrorCode.StorageUnavailable,               // CANTOPEN, READONLY, IOERR, FULL, PERM, ...
        };
        return new Error(code, $"Database '{DatabasePath}': {e.Message}");
    }
}
