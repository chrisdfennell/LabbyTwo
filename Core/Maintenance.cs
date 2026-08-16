using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>
/// Every alert held at once, on purpose, because you are the one breaking things.
///
/// This is not quiet hours and does not replace them. <see cref="AlertPolicy"/> answers
/// "am I asleep", which is a schedule known in advance; this answers "am I working on it",
/// which never is. Rebooting the NAS takes fifteen services down inside a minute, and
/// without this the only way to not be told fifteen times is to silence fifteen
/// connections one at a time and then remember to unsilence them all.
///
/// Stored as a single app setting rather than a column on anything: it is a property of
/// the installation for the next hour, not of any connection.
/// </summary>
/// <param name="On">Whether alerts are being held right now.</param>
/// <param name="Until">When it lifts by itself. Null while <paramref name="On"/> means it runs until somebody ends it.</param>
public sealed record Maintenance(bool On, DateTimeOffset? Until)
{
    public const string Key = "silence_all_until";

    /// <summary>The value stored for a window with no end, which only a person can lift.</summary>
    public const string Indefinite = "forever";

    public static Maintenance Off => new(false, null);

    /// <summary>
    /// Reads the window, treating one that has run out as simply off. Expiry is decided
    /// here rather than by anything that has to tick, so a window ends on time even if
    /// the process was asleep or restarted through its ending.
    /// </summary>
    public static Maintenance From(SettingsBag settings, DateTimeOffset now)
    {
        var raw = settings.Get(Key);

        if (raw.Length == 0)
            return Off;

        if (raw == Indefinite)
            return new Maintenance(true, null);

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var until))
        {
            return Off;
        }

        return until > now ? new Maintenance(true, until) : Off;
    }

    public static Maintenance From(SettingsBag settings) => From(settings, DateTimeOffset.Now);

    /// <summary>The value to store for a window ending after <paramref name="span"/>, or null for one with no end.</summary>
    public static string Value(TimeSpan? span, DateTimeOffset now) =>
        span is { } length ? (now + length).ToString("o", CultureInfo.InvariantCulture) : Indefinite;

    /// <summary>Empty, which is how the setting says "not in maintenance".</summary>
    public const string Cleared = "";

    /// <summary>
    /// Why an alert is being held, in the words the log and the suppression reason both
    /// want. Null when nothing is being held.
    /// </summary>
    public string? Reason => !On
        ? null
        : Until is { } until
            ? $"all alerts are silenced until {until.ToLocalTime():HH:mm}"
            : "all alerts are silenced";
}
