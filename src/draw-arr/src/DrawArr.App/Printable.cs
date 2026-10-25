using System.Globalization;
using System.Text;

namespace DrawArr.App;

/// <summary>Makes text safe to print to a terminal, as time-arr's <c>Cli.Printable</c> does.</summary>
public static class Printable
{
    /// <summary>The text with control and format characters other than newline and tab replaced by '?', so printed text can't send escape sequences or reorder the line.</summary>
    public static string Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        // Whole characters are checked, so format characters beyond the first plane, such as the tag characters, are caught too.
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsHidden(rune) && rune.Value is not '\n' and not '\t')
                builder.Append('?');
            else
                builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    // Format characters include the bidi overrides such as U+202E and the tag characters such as U+E0041.
    private static bool IsHidden(Rune rune) =>
        Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format || rune == Rune.ReplacementChar;
}
