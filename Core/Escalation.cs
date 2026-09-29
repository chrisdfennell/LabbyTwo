using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>
/// "If nobody has dealt with it in fifteen minutes, tell me somewhere louder." An alert
/// still firing after <see cref="AfterMinutes"/> is sent again, to a second set of channels
/// — email first, then the phone — and optionally again every <see cref="RepeatMinutes"/>
/// until it clears.
///
/// Off unless asked for, globally and per rule. An installation that escalated by default
/// would double every notification anybody already found too many of, which is the fastest
/// way to have alerting turned off altogether.
/// </summary>
/// <param name="AfterMinutes">How long an alert must have been firing. Zero or less is off.</param>
/// <param name="Channels">Alert channel connection ids. Empty means every channel.</param>
/// <param name="RepeatMinutes">Send again this often while still firing. Zero sends once.</param>
public sealed record EscalationPolicy(int AfterMinutes, IReadOnlyList<string> Channels, int RepeatMinutes)
{
    public const string AfterKey = "escalate_after_minutes";
    public const string ChannelsKey = "escalate_channels";
    public const string RepeatKey = "escalate_repeat_minutes";

    public static EscalationPolicy Off { get; } = new(0, [], 0);

    public bool On => AfterMinutes > 0;

    /// <summary>The installation-wide default, which every rule uses unless it says otherwise, and every down alert uses.</summary>
    public static EscalationPolicy From(SettingsBag settings) => new(
        Math.Max(0, settings.GetInt(AfterKey)),
        ParseChannels(settings.Get(ChannelsKey)),
        Math.Max(0, settings.GetInt(RepeatKey)));

    public Dictionary<string, string> ToSettings() => new()
    {
        [AfterKey] = Math.Max(0, AfterMinutes).ToString(CultureInfo.InvariantCulture),
        [ChannelsKey] = string.Join(',', Channels),
        [RepeatKey] = Math.Max(0, RepeatMinutes).ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// What a rule actually escalates by. A rule that never chose uses the default, so
    /// turning escalation on in one place covers every rule; a rule can opt out (zero) or
    /// set its own.
    /// </summary>
    public static EscalationPolicy For(AlertRule? rule, EscalationPolicy fallback) => rule?.EscalateAfterMinutes switch
    {
        null => fallback,
        <= 0 => Off,
        { } minutes => new EscalationPolicy(minutes, ParseChannels(rule.EscalateTo), Math.Max(0, rule.EscalateRepeatMinutes)),
    };

    /// <summary>
    /// When the next escalation is due, or null for never. The first is
    /// <see cref="AfterMinutes"/> after <paramref name="clockFrom"/>; each after that is
    /// <see cref="RepeatMinutes"/> after the last one actually sent.
    ///
    /// Measured from the last one *sent*, not from a fixed grid, which is the whole of the
    /// catch-up rule: an escalation that was due while LabbyTwo was stopped, or while
    /// maintenance held it, goes once when it can, and the next is a full interval after
    /// that — never a burst of the ones that were missed.
    /// </summary>
    /// <param name="clockFrom">When the alert started counting — see <see cref="FiringAlert.ClockFrom"/>.</param>
    /// <param name="lastEscalated">When it was last escalated, or null if it never has been.</param>
    public DateTimeOffset? DueAt(DateTimeOffset clockFrom, DateTimeOffset? lastEscalated)
    {
        if (!On)
            return null;
        if (lastEscalated is not { } last)
            return clockFrom.AddMinutes(AfterMinutes);
        return RepeatMinutes > 0 ? last.AddMinutes(RepeatMinutes) : null;
    }

    /// <summary>"after 15 min to Pushover, every 30 min", for the page.</summary>
    public string Describe(Func<string, string> channelName)
    {
        if (!On)
            return "Never escalates.";
        var to = Channels.Count == 0 ? "every channel" : string.Join(", ", Channels.Select(channelName));
        return $"Still firing after {AfterMinutes} min: sent again to {to}" +
               (RepeatMinutes > 0 ? $", and every {RepeatMinutes} min after that." : ", once.");
    }

    public static IReadOnlyList<string> ParseChannels(string? stored) =>
        [.. (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()];
}

/// <summary>
/// An alert that has fired and not yet cleared, as far as delivery is concerned: when it
/// started, whether a mute window is still holding it, and whether and where it has been
/// escalated. Kept in the database so that none of that is forgotten by a restart — which
/// would either escalate everything again on startup or lose an escalation that was due.
/// </summary>
/// <param name="Key">The alert's tag: <c>rule:{rule}:{connection}</c> or <c>status:{connection}</c>.</param>
/// <param name="RuleId">Null for a connection going down.</param>
/// <param name="Since">When the condition started — for a rule, the start of its sustain window.</param>
/// <param name="ClockFrom">
/// When the escalation clock started: the moment it fired, or — for one a mute window held —
/// the moment the window ended and it was delivered. A window is somebody saying "this is
/// expected until five", so time inside it is not time anybody failed to react.
/// </param>
/// <param name="HeldBy">The mute window holding the first notification, which is still owed. Null once delivered or when never held.</param>
/// <param name="EscalatedAt">When it was last escalated. Null if never.</param>
/// <param name="Escalations">How many times it has been escalated.</param>
/// <param name="EscalatedTo">Every channel an escalation went to, so the recovery can go there too. "*" means all of them.</param>
/// <param name="Value">The reading when it fired, so a restored rule has something to show before the next probe.</param>
public sealed record FiringAlert(
    string Key,
    string? RuleId,
    string ConnectionId,
    DateTimeOffset Since,
    DateTimeOffset ClockFrom,
    string? HeldBy,
    DateTimeOffset? EscalatedAt,
    int Escalations,
    IReadOnlyList<string> EscalatedTo,
    string Title,
    string Body,
    string? Link,
    double Value)
{
    /// <summary>What <see cref="EscalatedTo"/> holds for "every channel".</summary>
    public const string AllChannels = "*";

    public bool IsStatus => RuleId is null;

    public static string RuleKey(string ruleId, string connectionId) => $"rule:{ruleId}:{connectionId}";

    public static string StatusKey(string connectionId) => $"status:{connectionId}";

    /// <summary>
    /// Where the recovery goes: wherever the alert first went, plus everywhere it was
    /// escalated to. Null is every channel. Being told "it's fine now" only on the channel
    /// you stopped reading an hour ago would leave the loud one saying it is still broken.
    /// </summary>
    public static IReadOnlyList<string>? RecoveryChannels(string? primary, FiringAlert? entry)
    {
        if (primary is not { Length: > 0 })
            return null;
        if (entry is null || entry.EscalatedTo.Count == 0)
            return [primary];
        if (entry.EscalatedTo.Contains(AllChannels))
            return null;
        return [.. entry.EscalatedTo.Prepend(primary).Distinct()];
    }
}
