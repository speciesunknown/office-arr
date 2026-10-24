using System.Text;

namespace TimeArr.Core;

/// <summary>One stored timer: a stable id, a unique name, and its state.</summary>
/// <remarks>Not called <c>Timer</c> to stay clear of <see cref="System.Threading.Timer"/>.</remarks>
public sealed record TimerEntry(long Id, string Name, TimerState State);

/// <summary>Rules for timer names. Names are unique ignoring ASCII case.</summary>
public static class TimerName
{
    public const int MaxLength = 64;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Trims and normalizes <paramref name="name"/>, and checks it: 1 to <see cref="MaxLength"/>
    /// characters, no control characters, and not starting with '-' (so it can't be mistaken
    /// for a command-line option).
    /// </summary>
    /// <returns>The name as it should be stored.</returns>
    public static Result<string> Validate(string? name)
    {
        if (name is null)
            return Result<string>.Fail(ErrorCode.InvalidInput, "Timer name is missing.");

        string normalized;
        try
        {
            // Throws on lone surrogates, which Normalize lets through.
            StrictUtf8.GetByteCount(name);
            normalized = name.Normalize(NormalizationForm.FormC).Trim();
        }
        catch (Exception e) when (e is ArgumentException or EncoderFallbackException)
        {
            return Result<string>.Fail(ErrorCode.InvalidInput, "Timer name is not valid text.");
        }

        if (normalized.Length == 0)
            return Result<string>.Fail(ErrorCode.InvalidInput, "Timer name is empty.");
        if (normalized.Length > MaxLength)
            return Result<string>.Fail(ErrorCode.InvalidInput, $"Timer name is longer than {MaxLength} characters.");
        if (normalized.Any(char.IsControl))
            return Result<string>.Fail(ErrorCode.InvalidInput, "Timer name contains control characters.");
        if (normalized[0] == '-')
            return Result<string>.Fail(ErrorCode.InvalidInput, "Timer name must not start with '-'.");

        return normalized;
    }
}
