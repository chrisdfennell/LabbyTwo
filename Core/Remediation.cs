using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>What a remediation does when it runs.</summary>
public enum RemediationKind
{
    /// <summary>Restart one container on a Docker connection, by name.</summary>
    RestartContainer,

    /// <summary>
    /// Run one of a connection's own action buttons — the same ones the Controls card and a
    /// runbook's <c>{{button: NAS / restart}}</c> offer, through the same ActionRunner.
    /// </summary>
    ProviderAction,
}

/// <summary>What happens when a remediation has used its attempts and the alert is still firing.</summary>
public enum IfNotFixed
{
    /// <summary>Say so on the channels the alert itself went to.</summary>
    Notify,

    /// <summary>Escalate it now, to the escalation channels, rather than waiting for the escalation clock.</summary>
    Escalate,
}

/// <summary>
/// "If Plex is down for five minutes, restart the plex container once; if that does not
/// fix it, tell me." An automatic action attached to one alert rule, or to one connection
/// going down.
///
/// Most home-lab outages at three in the morning are fixed by the same thing a person would
/// do half asleep — restart the container — and the person is only woken up to do it. This
/// does it for them, and only tells them if it did not work. It is deliberately narrow:
/// one action, a small number of attempts per outage, a cooldown between them, and a
/// notification when it gives up. A remediation that retried for ever would turn a crash
/// loop into a restart loop, which is the same outage with more noise.
///
/// Stored one per trigger — a rule or a connection has at most one — so the editor is a
/// section of the rule or connection it belongs to rather than a list of its own.
/// </summary>
public sealed record Remediation
{
    /// <summary><c>rule:{rule id}</c> or <c>down:{connection id}</c> — see <see cref="RuleTrigger"/> and <see cref="DownTrigger"/>.</summary>
    public string Trigger { get; init; } = "";

    public bool Enabled { get; init; } = true;

    /// <summary>How long the alert must have been firing first. A blip that clears by itself is not worth a restart.</summary>
    public int AfterMinutes { get; init; } = 5;

    public RemediationKind Kind { get; init; } = RemediationKind.RestartContainer;

    /// <summary>The Docker connection for a restart, or the connection whose action runs.</summary>
    public string TargetConnectionId { get; init; } = "";

    /// <summary>The container's name (or Compose service) for a restart. Ignored for an action.</summary>
    public string Container { get; init; } = "";

    /// <summary>The provider action's id for <see cref="RemediationKind.ProviderAction"/>.</summary>
    public string ActionId { get; init; } = "";

    /// <summary>How many times it may run for one outage. One unless asked for more: a second restart rarely fixes what the first did not.</summary>
    public int MaxAttempts { get; init; } = 1;

    /// <summary>
    /// The least time between two runs. Also what decides that an outage is over: an alert
    /// that fires again within this long of the last run is the same outage, so a restart
    /// that itself brings the alert back cannot start another one.
    /// </summary>
    public int CooldownMinutes { get; init; } = 30;

    /// <summary>How long after running to wait before deciding it did not help.</summary>
    public int CheckAfterMinutes { get; init; } = 5;

    /// <summary>
    /// Allowed to act on a protected container, or to run an action the provider marks as
    /// dangerous. Off by default, because the protected list is exactly the things whose
    /// restart can lock you out — the tunnel, the reverse proxy.
    /// </summary>
    public bool AllowProtected { get; init; }

    public IfNotFixed IfNotFixed { get; init; } = IfNotFixed.Notify;

    public const int MaxAttemptsLimit = 5;

    public static string RuleTrigger(string ruleId) => $"rule:{ruleId}";

    public static string DownTrigger(string connectionId) => $"down:{connectionId}";

    /// <summary>The trigger a firing alert answers to: its rule's, or its connection's "down".</summary>
    public static string TriggerFor(FiringAlert entry) =>
        entry.RuleId is { } rule ? RuleTrigger(rule) : DownTrigger(entry.ConnectionId);

    /// <summary>Whether enough is filled in to run — a restart with no container, or an action with none chosen, is not.</summary>
    public bool IsComplete => TargetConnectionId.Length > 0 && Kind switch
    {
        RemediationKind.RestartContainer => Container.Trim().Length > 0,
        _ => ActionId.Length > 0,
    };

    /// <summary>The values kept within reason, whatever the form sent.</summary>
    public Remediation Normalised() => this with
    {
        AfterMinutes = Math.Clamp(AfterMinutes, 0, 24 * 60),
        MaxAttempts = Math.Clamp(MaxAttempts, 1, MaxAttemptsLimit),
        CooldownMinutes = Math.Clamp(CooldownMinutes, 1, 24 * 60),
        CheckAfterMinutes = Math.Clamp(CheckAfterMinutes, 1, 24 * 60),
        Container = Container.Trim(),
    };

    public TimeSpan After => TimeSpan.FromMinutes(Math.Max(0, AfterMinutes));

    public TimeSpan Cooldown => TimeSpan.FromMinutes(Math.Max(1, CooldownMinutes));

    public TimeSpan CheckAfter => TimeSpan.FromMinutes(Math.Max(1, CheckAfterMinutes));
}

/// <summary>
/// What a remediation has done about one alert — one row per firing key, kept in the
/// database so a restart of LabbyTwo neither forgets it has already restarted plex (and
/// does it again) nor loses the check it was waiting on.
/// </summary>
/// <param name="AlertKey">The firing alert's key: <c>rule:{rule}:{connection}</c> or <c>status:{connection}</c>.</param>
/// <param name="EpisodeStart">When this outage began, as far as remediation is concerned.</param>
/// <param name="Attempts">Runs in this outage.</param>
/// <param name="LastRunAt">When it last ran; null if it has only been held back so far.</param>
/// <param name="CheckAt">When to decide whether the last run helped; null when nothing is waiting.</param>
/// <param name="Did">What the last run did, in the past tense — "Restarted plex" — for the notices that follow it.</param>
/// <param name="Outcome">The last thing that happened, as one of <see cref="RemediationOutcomes"/>.</param>
/// <param name="Note">Why it was last held back, so the feed says so once rather than every sweep.</param>
/// <param name="GaveUp">Out of attempts and said so; nothing more happens until the outage ends.</param>
public sealed record RemediationState(
    string AlertKey,
    DateTimeOffset EpisodeStart,
    int Attempts,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? CheckAt,
    string Did,
    string Outcome,
    string Note,
    bool GaveUp);

/// <summary>What <see cref="RemediationState.Outcome"/> holds.</summary>
public static class RemediationOutcomes
{
    public const string None = "";
    public const string Waiting = "waiting";
    public const string Helped = "helped";
    public const string NotHelped = "not_helped";
    public const string Failed = "failed";
}

/// <summary>
/// The decisions, apart from anything that runs or stores them, so each is a test rather
/// than an outage staged by hand.
///
/// <list type="number">
/// <item><b>An outage is one episode however often it flaps.</b> An alert that fires again
/// within <see cref="Remediation.CooldownMinutes"/> of the last run belongs to the same
/// episode and shares its attempts. This is the loop guard: a restart that knocks the
/// service over again a minute later cannot buy itself another restart.</item>
/// <item><b>It waits <see cref="Remediation.AfterMinutes"/></b> from when the alert fired,
/// then runs — unless it is out of attempts, still inside the cooldown, or waiting to see
/// whether the last run helped.</item>
/// <item><b>A run is judged <see cref="Remediation.CheckAfterMinutes"/> later.</b> If the
/// alert cleared in the meantime it helped; if it is still firing it did not, and when that
/// was the last attempt it gives up and says so, once.</item>
/// </list>
/// </summary>
public static class RemediationRules
{
    /// <summary>What to do about one firing alert right now.</summary>
    public enum Step
    {
        /// <summary>Nothing yet — waiting for the delay, the cooldown or the check.</summary>
        Wait,

        /// <summary>Run the action.</summary>
        Run,

        /// <summary>The last run is due its verdict, and the alert is still firing: it did not help.</summary>
        NotHelped,

        /// <summary>Out of attempts, and already said so.</summary>
        Done,
    }

    /// <summary>
    /// Whether a state belongs to the outage this firing alert is part of. A state with no
    /// run in it is only ever a note about being held back, so it belongs to whatever is
    /// firing now as long as that started before the note was written.
    /// </summary>
    public static bool SameEpisode(Remediation remediation, FiringAlert entry, RemediationState state) =>
        state.LastRunAt is { } last
            ? entry.Since < last + remediation.Cooldown
            : entry.Since <= state.EpisodeStart;

    /// <summary>The state to carry on with for this firing alert: the stored one, or null when a new outage has begun.</summary>
    public static RemediationState? Current(Remediation remediation, FiringAlert entry, RemediationState? stored) =>
        stored is not null && SameEpisode(remediation, entry, stored) ? stored : null;

    /// <summary>When it may run for this alert, ignoring the guardrails, which are checked separately.</summary>
    public static Step Decide(Remediation remediation, FiringAlert entry, RemediationState? state, DateTimeOffset now)
    {
        if (state is { CheckAt: { } check })
            return now >= check ? Step.NotHelped : Step.Wait;

        if (state is { GaveUp: true } || state?.Attempts >= remediation.MaxAttempts)
            return Step.Done;

        if (now < entry.ClockFrom + remediation.After)
            return Step.Wait;

        if (state?.LastRunAt is { } last && now < last + remediation.Cooldown)
            return Step.Wait;

        return Step.Run;
    }

    /// <summary>
    /// Whether an outage's state can be let go of: nothing is firing for it, nothing is
    /// waiting on a verdict, and the cooldown that ties a re-fire to it has run out.
    /// </summary>
    public static bool Expired(Remediation? remediation, RemediationState state, DateTimeOffset now)
    {
        if (state.CheckAt is not null)
            return false;
        var cooldown = remediation?.Cooldown ?? TimeSpan.FromMinutes(30);
        return (state.LastRunAt ?? state.EpisodeStart) + cooldown <= now;
    }

    /// <summary>"in 5 min", "now" — when it will run, for the first notification.</summary>
    public static string When(Remediation remediation, FiringAlert entry, DateTimeOffset now)
    {
        var due = entry.ClockFrom + remediation.After - now;
        return due <= TimeSpan.FromSeconds(30)
            ? "now"
            : $"in {Math.Ceiling(due.TotalMinutes).ToString(CultureInfo.InvariantCulture)} min";
    }

    /// <summary>"after 5 min, restart plex once; wait 30 min between tries" — the whole plan in one line, for the page.</summary>
    public static string Describe(Remediation remediation, string action)
    {
        if (!remediation.Enabled)
            return "Off.";
        var after = remediation.AfterMinutes > 0 ? $"After {remediation.AfterMinutes} min, {action}" : $"Straight away, {action}";
        var times = remediation.MaxAttempts == 1 ? "once" : $"up to {remediation.MaxAttempts} times, {remediation.CooldownMinutes} min apart";
        var then = remediation.IfNotFixed == IfNotFixed.Escalate ? "escalate" : "tell you";
        return $"{after} {times}; if it is still firing {remediation.CheckAfterMinutes} min later, {then}.";
    }
}

/// <summary>
/// The global switches: whether anything runs at all, and how many runs an hour the whole
/// installation may make. The cap is the last guard against something nobody foresaw —
/// two remediations that knock each other's service over, say — turning into a night of
/// restarts. Stored as app settings, since they are about the installation, not a rule.
/// </summary>
public sealed record RemediationSettings(bool Enabled, int MaxPerHour)
{
    public const string EnabledKey = "remediation_enabled";
    public const string MaxPerHourKey = "remediation_max_per_hour";
    public const int DefaultMaxPerHour = 5;

    public static RemediationSettings From(SettingsBag settings) => new(
        settings.GetBool(EnabledKey, true),
        Math.Max(0, settings.GetInt(MaxPerHourKey, DefaultMaxPerHour)));

    public Dictionary<string, string> ToSettings() => new()
    {
        [EnabledKey] = Enabled ? "true" : "false",
        [MaxPerHourKey] = Math.Max(0, MaxPerHour).ToString(CultureInfo.InvariantCulture),
    };
}
