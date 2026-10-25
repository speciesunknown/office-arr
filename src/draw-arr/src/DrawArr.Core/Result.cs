namespace DrawArr.Core;

/// <summary>What went wrong in an expected failure, with a stable code and a message for people.</summary>
public sealed record Error(ErrorCode Code, string Message)
{
    /// <summary>The kind of failure, which is never the unused value 0.</summary>
    public ErrorCode Code { get; } = Enum.IsDefined(Code) ? Code : throw new ArgumentOutOfRangeException(nameof(Code), Code, "Not an error code.");

    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>The kinds of expected failure, numbered from 1 so a default 0 is never a real code.</summary>
public enum ErrorCode
{
    /// <summary>The input was not acceptable, such as a bad argument or value.</summary>
    InvalidInput = 1,
    /// <summary>The thing asked for, such as a box or a document, does not exist.</summary>
    NotFound = 2,
    /// <summary>The request clashes with what is there, such as nothing left to undo or a drawing that is full.</summary>
    Conflict = 3,
    /// <summary>Talking to the running instance failed.</summary>
    Ipc = 4,
    /// <summary>A file could not be read or written, for example because it is missing or of the wrong permissions.</summary>
    FileUnavailable = 5,
    /// <summary>A file is not a draw-arr drawing, is damaged, or is too big.</summary>
    FileInvalid = 6,
}

/// <summary>Outcome of an operation that can fail in an expected way.</summary>
/// <remarks>It is a class rather than a struct so that no <c>default</c> value can read as success.</remarks>
public sealed class Result
{
    private static readonly Result Success = new(null);
    private readonly Error? _error;

    private Result(Error? error) => _error = error;

    public bool IsSuccess => _error is null;
    public bool IsFailure => _error is not null;

    /// <summary>The failure, which is a bug to read on a success.</summary>
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

    /// <summary>The value, which is a bug to read on a failure.</summary>
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"Result is a failure: {_error}");

    /// <summary>The failure, which is a bug to read on a success.</summary>
    public Error Error => _error ?? throw new InvalidOperationException("Result is a success; it has no error.");

    /// <summary>A success carrying <paramref name="value"/>, which must not be null because a missing value is a failure.</summary>
    public static Result<T> Ok(T value) => new(value ?? throw new ArgumentNullException(nameof(value)), null);
    public static Result<T> Fail(Error error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));
    public static Result<T> Fail(ErrorCode code, string message) => new(default, new Error(code, message));

    public static implicit operator Result<T>(T value) => Ok(value);
    public static implicit operator Result<T>(Error error) => Fail(error);

    /// <summary>Drops the value, keeping success or failure.</summary>
    public Result ToResult() => IsSuccess ? Result.Ok() : Result.Fail(Error);

    public override string ToString() => IsSuccess ? $"Ok({_value})" : $"Fail({_error})";
}
