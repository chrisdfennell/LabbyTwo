namespace LabbyTwo.Core;

/// <summary>
/// How long ago something happened, in the shape a dashboard wants: two units at most,
/// biggest first. "25d 21h ago" is readable at a glance in a table row; a full timestamp
/// is not, and "25 days ago" throws away detail that matters when you are looking for what
/// you touched this morning.
/// </summary>
public static class Ago
{
    /// <summary>Shown when there is no timestamp at all, rather than a date in 0001.</summary>
    public const string Unknown = "—";

    public static string Since(DateTimeOffset when, DateTimeOffset now)
    {
        if (when == DateTimeOffset.MinValue)
            return Unknown;

        var elapsed = now - when;

        // Clock skew between here and the server, or a timestamp written a moment into the
        // future. "in -2s" helps nobody.
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        if (elapsed.TotalSeconds < 45)
            return "just now";

        if (elapsed.TotalHours < 1)
            return $"{elapsed.Minutes}m ago";

        if (elapsed.TotalDays < 1)
            return elapsed.Minutes == 0 ? $"{elapsed.Hours}h ago" : $"{elapsed.Hours}h {elapsed.Minutes}m ago";

        var days = (int)elapsed.TotalDays;
        return elapsed.Hours == 0 ? $"{days}d ago" : $"{days}d {elapsed.Hours}h ago";
    }

    public static string Since(DateTimeOffset when) => Since(when, DateTimeOffset.Now);
}
