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

    /// <summary>
    /// The same shape pointing the other way: how much longer. A banner that says a
    /// window lifts at 15:40 makes you do the subtraction; one that also says "43m left"
    /// does not, and the second is the number you actually wanted.
    /// </summary>
    public static string Until(DateTimeOffset when, DateTimeOffset now)
    {
        var left = when - now;

        // Already lapsed. The caller normally stops showing this at all, but a tick can
        // land the wrong side of the boundary and "-1m left" would be nonsense.
        if (left <= TimeSpan.Zero)
            return "any moment";

        if (left.TotalMinutes < 1)
            return "less than a minute left";

        if (left.TotalHours < 1)
            return $"{left.Minutes}m left";

        if (left.TotalDays < 1)
            return left.Minutes == 0 ? $"{left.Hours}h left" : $"{left.Hours}h {left.Minutes}m left";

        var days = (int)left.TotalDays;
        return left.Hours == 0 ? $"{days}d left" : $"{days}d {left.Hours}h left";
    }

    public static string Until(DateTimeOffset when) => Until(when, DateTimeOffset.Now);
}
