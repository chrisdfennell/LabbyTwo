using System.Globalization;
using System.Text.RegularExpressions;

namespace LabbyTwo.Core;

/// <summary>
/// One thing that changed somewhere in the lab: a service went down, a container was
/// recreated on a new image, an alert fired, a certificate was renewed, LabbyTwo updated
/// itself. The feed is these in time order, which is the first thing worth reading when
/// something breaks — "what changed?" is the question every outage starts with, and until
/// this existed the answer was spread across the status history, Docker's own logs, the
/// alert notifications on somebody's phone and memory.
///
/// Deliberately flat. A change is a sentence and a few words to filter by, not a typed
/// event per source, so a plugin can record one without the host learning anything about
/// it, and a new kind of change needs no new column.
/// </summary>
/// <param name="At">When it was noticed. For anything polled that is the poll that saw it,
/// which can be up to one sweep after it really happened.</param>
/// <param name="Kind">What sort of thing changed — one of <see cref="ChangeKinds"/>, or a
/// plugin's own word.</param>
/// <param name="Action">What happened to it — one of <see cref="ChangeActions"/>.</param>
/// <param name="ConnectionId">The connection it happened on or was seen through, if any.
/// Null for LabbyTwo's own update.</param>
/// <param name="Subject">The thing itself within that connection: a container's name, an
/// alert rule's id, a host name. Empty when the connection is the subject.</param>
/// <param name="Title">The sentence the feed shows: "plex restarted".</param>
/// <param name="Detail">What else is worth knowing, in words: an exit code, the old and new
/// image, what the probe said.</param>
public sealed record Change(
    DateTimeOffset At,
    string Kind,
    string Action,
    string? ConnectionId,
    string Subject,
    string Title,
    string Detail = "")
{
    /// <summary>The row id once stored; zero before.</summary>
    public long Id { get; init; }

    /// <summary>Something breaking — the red end of the feed.</summary>
    public bool IsTrouble => Action is ChangeActions.Down or ChangeActions.Firing or ChangeActions.Stopped or ChangeActions.Late
        or ChangeActions.Failed;

    /// <summary>Something coming back — the green end.</summary>
    public bool IsRecovery => Action is ChangeActions.Up or ChangeActions.Cleared or ChangeActions.Started
        or ChangeActions.Completed or ChangeActions.Tested or ChangeActions.Passed;

    /// <summary>
    /// The status dot a change is drawn with: red for trouble, green for recovery, amber for
    /// a restart — which is a recovery from something nobody saw — and grey for everything
    /// that is neither good nor bad, only different.
    /// </summary>
    public string Dot => IsTrouble ? "status-down"
        : IsRecovery ? "status-up"
        : Action is ChangeActions.Restarted or ChangeActions.Remediated or ChangeActions.RolledBack ? "status-flapping"
        // Self-healing's verdicts, which are neither a failure nor a recovery of the thing
        // itself but say which way it went: red for "did not help", green for "fixed it".
        : Action is ChangeActions.NotHelped or ChangeActions.Failed ? "status-down"
        : Action == ChangeActions.Helped ? "status-up"
        : "status-unknown";
}

/// <summary>The kinds of change LabbyTwo records itself. Plugins may use their own words.</summary>
public static class ChangeKinds
{
    public const string Status = "status";
    public const string Container = "container";
    public const string Alert = "alert";
    public const string Certificate = "certificate";
    public const string Dns = "dns";
    public const string Device = "device";
    public const string Update = "update";
    public const string Backup = "backup";

    /// <summary>Somebody pressed "Something's broken" on the family status page.</summary>
    public const string Report = "report";
    /// <summary>Something LabbyTwo did by itself to fix an alert — see <see cref="LabbyTwo.Core.Remediation"/>.</summary>
    public const string Remediation = "remediation";

    /// <summary>
    /// LabbyTwo itself losing and regaining sight of the lab (see <see cref="BlindnessRules"/>).
    /// On no connection, so it never becomes part of an incident — it explains them.
    /// </summary>
    public const string Monitor = "monitor";

    /// <summary>A scheduled action ran, failed, was skipped or was missed — see <see cref="ScheduledAction"/>.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>
    /// Somebody pressed a <c>{{ssh: …}}</c> button in a note and a command ran on a machine —
    /// who, what, and how it ended. See <see cref="RunbookCommands"/>.
    /// </summary>
    public const string Command = "command";

    /// <summary>Every kind, in the order the filter offers them, with the words a person uses.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        (Status, "Services up and down"),
        (Container, "Containers"),
        (Alert, "Alerts"),
        (Certificate, "Certificates"),
        (Dns, "DNS answers"),
        (Device, "Devices on the network"),
        (Update, "LabbyTwo updates"),
        (Report, "Reports from the family"),
        (Remediation, "Self-healing"),
        (Backup, "Backups and restore tests"),
        (Monitor, "LabbyTwo losing sight of the lab"),
        (Scheduled, "Scheduled actions"),
        (Command, "Commands run from notes"),
    ];

    /// <summary>
    /// The kind a word means, forgiving the plurals and near-synonyms somebody writes in a
    /// shortcode: <c>containers</c>, <c>services</c>, <c>certs</c>. Null for a word that
    /// means none of them.
    /// </summary>
    public static string? Parse(string? written)
    {
        var word = (written ?? "").Trim().ToLowerInvariant();
        return word switch
        {
            "status" or "statuses" or "service" or "services" or "up" or "down" or "outages" => Status,
            "container" or "containers" or "docker" => Container,
            "alert" or "alerts" or "rule" or "rules" => Alert,
            "certificate" or "certificates" or "cert" or "certs" or "tls" => Certificate,
            "dns" => Dns,
            "device" or "devices" or "lan" or "network" => Device,
            "update" or "updates" or "labbytwo" => Update,
            "report" or "reports" or "family" => Report,
            "remediation" or "remediations" or "self-healing" or "selfhealing" or "healing" or "fixes" => Remediation,
            "backup" or "backups" or "restore" or "restores" => Backup,
            "monitor" or "monitoring" or "blind" or "sight" => Monitor,
            "scheduled" or "schedule" or "schedules" => Scheduled,
            "command" or "commands" or "ssh" or "runbook" or "runbooks" => Command,
            _ => null,
        };
    }

    /// <summary>What the filter calls a kind; the key itself for one it does not know.</summary>
    public static string Label(string kind) =>
        All.FirstOrDefault(k => k.Key == kind) is { Key: not null } known ? known.Label : kind;
}

/// <summary>What happened, in one word per action. Stored as written, so never rename one.</summary>
public static class ChangeActions
{
    public const string Down = "down";
    public const string Up = "up";
    public const string Firing = "firing";
    public const string Cleared = "cleared";
    public const string Started = "started";
    public const string Stopped = "stopped";
    public const string Restarted = "restarted";
    public const string Paused = "paused";
    public const string Unpaused = "unpaused";
    public const string Created = "created";
    public const string Recreated = "recreated";
    public const string Removed = "removed";
    public const string Image = "image";
    public const string Renewed = "renewed";
    public const string Replaced = "replaced";
    public const string Changed = "changed";
    public const string Appeared = "appeared";
    public const string Updated = "updated";
    public const string Reported = "reported";

    // Self-healing. Kept apart from Restarted, which is Docker's word for something it
    // saw happen, rather than something LabbyTwo chose to do.
    public const string Remediated = "remediated";
    public const string Helped = "helped";
    public const string NotHelped = "not_helped";
    public const string Failed = "failed";
    public const string Skipped = "skipped";

    /// <summary>A backup was proven to have finished — by its source, or ticked by hand.</summary>
    public const string Completed = "completed";

    /// <summary>A backup went past its due time with nothing newer to prove it.</summary>
    public const string Late = "late";

    /// <summary>Somebody recorded that they tried restoring it.</summary>
    public const string Tested = "tested";

    /// <summary>A container updated from the Containers tab, now being watched (see <see cref="SafeUpdateRules"/>).</summary>
    public const string Watching = "watching";

    /// <summary>A safe update's watch ended with nothing wrong.</summary>
    public const string Passed = "passed";

    // Failed, above, is shared: self-healing's action that could not run, and a safe
    // update's watch that found something wrong or a roll-back that could not be done.

    /// <summary>A container put back on the image it ran before an update.</summary>
    public const string RolledBack = "rolled-back";
}

/// <summary>
/// What a feed read asks for: a window, optionally narrowed to some kinds and one
/// connection, newest first, at most <see cref="Limit"/> rows.
/// </summary>
/// <param name="Kinds">Null or empty for every kind.</param>
public sealed record ChangeQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyCollection<string>? Kinds = null,
    string? ConnectionId = null,
    int Limit = 200);

/// <summary>
/// A span of time written the way people write one — <c>30m</c>, <c>24h</c>, <c>7d</c>,
/// <c>2w</c> — for the <c>last=</c> option of <c>{{changes}}</c> and <c>{{incidents}}</c>.
/// Wider than a sparkline's window because the feed and incidents are kept for months.
/// </summary>
public static partial class ChangeWindow
{
    public static readonly TimeSpan Shortest = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Longest = TimeSpan.FromDays(365);

    [GeneratedRegex(@"^(\d{1,4})\s*(m|min|h|d|w)$", RegexOptions.IgnoreCase)]
    private static partial Regex Word();

    /// <summary>
    /// The span written, or <paramref name="fallback"/> when nothing was, or null with why
    /// when what was written is not a span or is outside five minutes to a year.
    /// </summary>
    public static TimeSpan? Parse(string? text, TimeSpan fallback, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        var match = Word().Match(text.Trim());
        if (!match.Success)
        {
            problem = $"“{text}” is not a length of time. Write it like 30m, 24h, 7d or 4w.";
            return null;
        }
        var count = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var span = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "m" or "min" => TimeSpan.FromMinutes(count),
            "h" => TimeSpan.FromHours(count),
            "d" => TimeSpan.FromDays(count),
            _ => TimeSpan.FromDays(count * 7),
        };
        if (span < Shortest || span > Longest)
        {
            problem = $"“{text}” is outside what is kept: between 5m and 365d.";
            return null;
        }
        return span;
    }

    /// <summary>"24 hours", "7 days", "30 minutes" — a span in words, for "nothing changed in the last …".</summary>
    public static string Describe(TimeSpan span) =>
        span.TotalDays >= 2 && span.TotalDays % 1 == 0 ? $"{span.TotalDays:0} days"
        : span.TotalHours >= 1 && span.TotalHours % 1 == 0 ? $"{span.TotalHours:0} hour{(span.TotalHours == 1 ? "" : "s")}"
        : $"{span.TotalMinutes:0} minutes";
}
