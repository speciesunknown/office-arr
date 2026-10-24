namespace TimeArr.Core;

/// <summary>What went wrong, for an expected failure. <see cref="Code"/> is stable; <see cref="Message"/> is for humans.</summary>
public sealed record Error(ErrorCode Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}

public enum ErrorCode
{
    /// <summary>The input was not acceptable (bad argument, bad value).</summary>
    InvalidInput,
    /// <summary>The thing asked for (e.g. a timer) does not exist.</summary>
    NotFound,
    /// <summary>The request clashes with what is there (e.g. a timer name already in use, too many timers).</summary>
    Conflict,
    /// <summary>The database file could not be opened or created (missing directory, permissions).</summary>
    StorageUnavailable,
    /// <summary>The database is held by someone else and did not free up in time.</summary>
    StorageBusy,
    /// <summary>The database or the data in it is not what time-arr wrote.</summary>
    StorageCorrupt,
    /// <summary>The database was written by a newer time-arr.</summary>
    StorageTooNew,
    /// <summary>Talking to the running instance failed.</summary>
    Ipc,
}

/// <summary>Outcome of an operation that can fail in an expected way.</summary>
/// <remarks>A class rather than a struct, so there is no <c>default</c> value that silently reads as success.</remarks>
public sealed class Result
{
    private static readonly Result Success = new(null);
    private readonly Error? _error;

    private Result(Error? error) => _error = error;

    public bool IsSuccess => _error is null;
    public bool IsFailure => _error is not null;

    /// <summary>The failure. Reading it on a success is a bug.</summary>
    public Error Error => _error ?? throw new InvalidOperationException("Result is a success; it has no error.");

    public static Result Ok() => Success;
    public static Result Fail(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));
    public static Result Fail(ErrorCode code, string message) => new(new Error(code, message));

    public static implicit operator Result(Error error) => Fail(error);

    public override string ToString() => IsSuccess ? "Ok" : $"Fail({_error})";
}

/// <summary>Outcome of an operation that returns a value or fails in an expected way.</summary>
public sealed class Result<T>
{
    private readonly T? _value;
    private readonly Error? _error;

    private Result(T? value, Error? error)
    {
        _value = value;
        _error = error;
    }

    public bool IsSuccess => _error is null;
    public bool IsFailure => _error is not null;

    /// <summary>The value. Reading it on a failure is a bug.</summary>
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"Result is a failure: {_error}");

    /// <summary>The failure. Reading it on a success is a bug.</summary>
    public Error Error => _error ?? throw new InvalidOperationException("Result is a success; it has no error.");

    /// <summary>A success. <paramref name="value"/> must not be null: a missing value is a failure, not an Ok.</summary>
    public static Result<T> Ok(T value) => new(value ?? throw new ArgumentNullException(nameof(value)), null);
    public static Result<T> Fail(Error error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));
    public static Result<T> Fail(ErrorCode code, string message) => new(default, new Error(code, message));

    public static implicit operator Result<T>(T value) => Ok(value);
    public static implicit operator Result<T>(Error error) => Fail(error);

    /// <summary>Drops the value, keeping success or failure.</summary>
    public Result ToResult() => IsSuccess ? Result.Ok() : Result.Fail(Error);

    public override string ToString() => IsSuccess ? $"Ok({_value})" : $"Fail({_error})";
}
