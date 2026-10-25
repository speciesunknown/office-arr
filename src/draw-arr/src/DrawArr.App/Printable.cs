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
        foreach (var c in text)
            builder.Append(IsHidden(c) && c is not '\n' and not '\t' ? '?' : c);
        return builder.ToString();
    }

    // Format characters include the bidi overrides such as U+202E.
    private static bool IsHidden(char c) =>
        char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format;
}
