namespace LabbyTwo.Core;

/// <summary>
/// When LabbyTwo is allowed to interrupt you.
///
/// This exists because monitoring more things makes a dashboard less useful, not more,
/// unless something decides what is worth saying. Forty providers all able to raise an
/// alert is forty ways to teach somebody to ignore their phone.
/// </summary>
/// <param name="QuietFrom">Start of quiet hours, local. Equal to <paramref name="QuietTo"/> means no quiet hours.</param>
/// <param name="QuietMode">What still gets through while quiet: "down" or "nothing".</param>
public sealed record AlertPolicy(TimeOnly QuietFrom, TimeOnly QuietTo, string QuietMode)
{
    public const string FromKey = "quiet_from";
    public const string ToKey = "quiet_to";
    public const string ModeKey = "quiet_mode";

    /// <summary>Only a service actually going down gets through. The default when quiet hours are on.</summary>
    public const string DownOnly = "down";

    /// <summary>Nothing at all.</summary>
    public const string Nothing = "nothing";

    /// <summary>Off: both ends equal, so no window exists.</summary>
    public static AlertPolicy Default => new(default, default, DownOnly);

    public static AlertPolicy From(SettingsBag settings) => new(
        Time(settings.Get(FromKey)),
        Time(settings.Get(ToKey)),
        settings.Get(ModeKey, DownOnly));

    public bool QuietHoursOn => QuietFrom != QuietTo;

    /// <summary>
    /// Whether a moment is inside the window. Handles the normal case — 23:00 to 07:00 —
    /// which wraps midnight and is exactly the window anybody actually wants.
    /// </summary>
    public bool IsQuiet(DateTimeOffset at)
    {
        if (!QuietHoursOn)
            return false;

        var now = TimeOnly.FromDateTime(at.LocalDateTime);

        return QuietFrom < QuietTo
            ? now >= QuietFrom && now < QuietTo
            : now >= QuietFrom || now < QuietTo;
    }

    /// <summary>
    /// Whether this alert survives quiet hours. A recovery is deliberately held even in
    /// "down only" mode: being woken to be told something came back is the purest form of
    /// pointless alert.
    /// </summary>
    public bool Allows(Alert alert, DateTimeOffset at) =>
        !IsQuiet(at) || (QuietMode != Nothing && alert.Level == AlertLevel.Down);

    public string Describe() => !QuietHoursOn
        ? "Alerts are sent at any hour."
        : QuietMode == Nothing
            ? $"Nothing is sent between {QuietFrom:HH\\:mm} and {QuietTo:HH\\:mm}."
            : $"Between {QuietFrom:HH\\:mm} and {QuietTo:HH\\:mm}, only a service going down is sent.";

    /// <summary>
    /// Anything unparseable is treated as "not set", which turns quiet hours off rather
    /// than silently choosing midnight for somebody.
    /// </summary>
    private static TimeOnly Time(string value) =>
        TimeOnly.TryParse(value, out var parsed) ? parsed : default;
}
