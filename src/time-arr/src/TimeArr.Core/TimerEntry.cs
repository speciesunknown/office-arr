using System.Globalization;
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
    /// Characters that are invisible, break lines or reorder text. Format (Cf) covers zero-width
    /// and bidi controls; that also rules out zero-width joiners inside emoji sequences.
    /// </summary>
    private static readonly HashSet<UnicodeCategory> ForbiddenCategories =
    [
        UnicodeCategory.Control,
        UnicodeCategory.Format,
        UnicodeCategory.LineSeparator,
        UnicodeCategory.ParagraphSeparator,
        UnicodeCategory.PrivateUse,
        UnicodeCategory.OtherNotAssigned,
        UnicodeCategory.Surrogate,
    ];

    /// <summary>
    /// Trims and normalizes <paramref name="name"/>, and checks it: 1 to <see cref="MaxLength"/>
    /// UTF-16 code units (an emoji counts as 2), nothing invisible, control or line-breaking,
    /// not starting with '-' (so it can't be mistaken for a command-line option), and not only
    /// digits (so it can't be mistaken for a timer id).
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
            return Result<string>.Fail(ErrorCode.InvalidInput, $"Timer name is longer than {MaxLength} UTF-16 code units.");
        if (normalized.EnumerateRunes().Any(r => ForbiddenCategories.Contains(Rune.GetUnicodeCategory(r))))
            return Result<string>.Fail(ErrorCode.InvalidInput, "Timer name contains invisible, control or line-breaking characters.");
        if (normalized[0] == '-')
            return Result<string>.Fail(ErrorCode.InvalidInput, "Timer name must not start with '-'.");
        if (normalized.All(char.IsAsciiDigit))
            return Result<string>.Fail(ErrorCode.InvalidInput, "Timer name must not be only digits (those are timer ids).");

        return normalized;
    }
}
