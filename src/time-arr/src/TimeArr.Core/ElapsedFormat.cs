namespace TimeArr.Core;

/// <summary>How elapsed time is shown to people, in the window and on the command line.</summary>
public static class ElapsedFormat
{
    /// <summary><c>h:mm:ss</c> under a day, <c>Nd hh:mm:ss</c> from a day on.</summary>
    public static string Format(TimeSpan elapsed)
    {
        var days = (long)elapsed.TotalDays;
        return days >= 1
            ? $"{days}d {elapsed.Hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Hours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
}
