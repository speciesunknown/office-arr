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

    /// <summary>The timer a new database starts with, and the one commands use when none is named.</summary>
    public const string Default = "default";

    /// <summary>
    /// The looser, stable check for a name read back from storage: non-empty, trimmed, at most
    /// <see cref="MaxLength"/>, valid UTF-16, no control or line-breaking characters. Unlike
    /// <see cref="Validate"/>, this must never get stricter, or old databases stop opening.
    /// </summary>
    public static bool IsStorable(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength || name.Trim().Length != name.Length)
            return false;
        try
        {
            StrictUtf8.GetByteCount(name);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        return !name.EnumerateRunes().Any(r => Rune.GetUnicodeCategory(r)
            is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
    }

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

    /// <summary>Most combining marks allowed in a row (enough for real scripts, not for "Zalgo" text).</summary>
    private const int MaxMarksInARow = 4;

    /// <summary>
    /// Unicode Default_Ignorable_Code_Point ranges that are not already in a forbidden category
    /// (from DerivedCoreProperties.txt), plus U+2800 BRAILLE PATTERN BLANK. All render as nothing.
    /// Variation selectors are handled separately: they're fine after a symbol (emoji).
    /// </summary>
    private static readonly (int First, int Last)[] Ignorable =
    [
        (0x034F, 0x034F), (0x115F, 0x1160), (0x17B4, 0x17B5), (0x180B, 0x180F),
        (0x2800, 0x2800), (0x3164, 0x3164), (0xFFA0, 0xFFA0), (0x1D173, 0x1D17A), (0xE0000, 0xE0FFF),
    ];

    private const int CombiningKeycap = 0x20E3;

    private static bool IsVariationSelector(Rune r) =>
        r.Value is (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF);

    private static Result CheckVisible(string name)
    {
        var invisible = Result.Fail(ErrorCode.InvalidInput,
            "Timer name contains invisible, control or line-breaking characters.");
        Rune? previous = null;
        var marksInARow = 0;
        var runes = name.EnumerateRunes().ToArray();

        for (var i = 0; i < runes.Length; i++)
        {
            var rune = runes[i];
            var category = Rune.GetUnicodeCategory(rune);
            if (ForbiddenCategories.Contains(category))
                return invisible;

            if (IsVariationSelector(rune))
            {
                // Only where it changes how something looks: after a symbol (U+2764 U+FE0F is a
                // red heart emoji), or in a keycap (digit, '#' or '*', then U+FE0F U+20E3).
                // After a letter it is just invisible.
                var afterSymbol = previous is { } p && Rune.GetUnicodeCategory(p) == UnicodeCategory.OtherSymbol;
                var inKeycap = previous is { Value: (>= '0' and <= '9') or '#' or '*' }
                               && i + 1 < runes.Length && runes[i + 1].Value == CombiningKeycap;
                if (!afterSymbol && !inKeycap)
                    return invisible;
            }
            else if (Array.Exists(Ignorable, range => rune.Value >= range.First && rune.Value <= range.Last))
            {
                return invisible;
            }

            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark)
            {
                if (previous is null)
                    return Result.Fail(ErrorCode.InvalidInput, "Timer name must not start with a combining mark.");
                if (++marksInARow > MaxMarksInARow)
                    return Result.Fail(ErrorCode.InvalidInput, $"Timer name has more than {MaxMarksInARow} combining marks in a row.");
            }
            else
            {
                marksInARow = 0;
            }
            previous = rune;
        }
        return Result.Ok();
    }

    /// <summary>
    /// Trims and normalizes <paramref name="name"/>, and checks it: 1 to <see cref="MaxLength"/>
    /// UTF-16 code units (an emoji counts as 2), nothing invisible, control or line-breaking,
    /// and not starting with '-' (so it can't be mistaken for a command-line option). A name is
    /// always a name, even "42": ids are only ever passed in their own field (e.g. <c>--id</c>).
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
        var visible = CheckVisible(normalized);
        if (visible.IsFailure)
            return Result<string>.Fail(visible.Error);
        if (normalized[0] == '-')
            return Result<string>.Fail(ErrorCode.InvalidInput, "Timer name must not start with '-'.");

        return normalized;
    }
}
