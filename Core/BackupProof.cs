using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>How often a backup is expected. The words people use, not a cron line.</summary>
public enum BackupFrequency
{
    Hourly,
    Daily,
    Weekly,
    Monthly,
}

/// <summary>
/// How often somebody should prove a backup can be restored. Off exists because some
/// things — a config export of a switch — are not worth a drill, and a reminder nobody
/// means to act on teaches people to ignore the others.
/// </summary>
public enum DrillCadence
{
    Off,
    Monthly,
    Quarterly,
    HalfYearly,
    Yearly,
}

/// <summary>What the Backups page says about one thing.</summary>
public enum BackupState
{
    /// <summary>Backed up within its expected frequency and grace.</summary>
    Ok,

    /// <summary>The newest backup anything can prove is older than it should be.</summary>
    Late,

    /// <summary>
    /// No date at all, because the thing that should prove it is not there to ask — the
    /// connection was deleted or is down, the metric is not reported, the nightly backup is
    /// switched off. Different from <see cref="Never"/>: that is a source answering "none".
    /// </summary>
    Missing,

    /// <summary>The source answers, and there has never been a successful backup.</summary>
    Never,
}

/// <summary>
/// Where an item's proof comes from. Stored as written, so never rename one.
/// </summary>
public static class BackupSources
{
    /// <summary>Somebody ticks it. For the USB disk in a drawer and the export done by hand.</summary>
    public const string Manual = "manual";

    /// <summary>LabbyTwo's own nightly copy of its database, dated by the newest file it wrote.</summary>
    public const string LabbyTwo = "labbytwo";

    /// <summary>One off-site destination of LabbyTwo's own backup; the target is its id.</summary>
    public const string Offsite = "offsite";

    /// <summary>A reading a connection reports — hours since the last backup, or a last-success timestamp.</summary>
    public const string Metric = "metric";
}

/// <summary>
/// One thing that ought to be backed up, and what proves it was. The point of the Backups
/// page is to turn "I think that's backed up" into a date: every item here either has a
/// source that says when it last succeeded, or somebody who ticks it when they did it.
///
/// Settings and state live in one record because they live in one row, but they are saved
/// separately (see <c>BackupStore</c>): the editor writes the first half and the sweep the
/// second, so a sweep landing while somebody edits the name cannot put the old name back.
/// </summary>
public sealed record BackupItem
{
    public string Id { get; init; } = Ids.New();
    public string Name { get; init; } = "";

    /// <summary>
    /// The connection this is a backup *of*, if any — "Nextcloud", "the NAS". Optional: a
    /// laptop is worth listing and is not a connection. Also what a mute window naming
    /// connections, or a silence, is matched against.
    /// </summary>
    public string? ConnectionId { get; init; }

    /// <summary>One of <see cref="BackupSources"/>.</summary>
    public string Source { get; init; } = BackupSources.Manual;

    /// <summary>The connection id for a metric source, the destination id for an off-site one; empty otherwise.</summary>
    public string SourceTarget { get; init; } = "";

    /// <summary>For a metric source, which metric: <c>hours_since_backup</c>, <c>hours_since_ping:restic</c>, a timestamp.</summary>
    public string SourceMetric { get; init; } = "";

    public BackupFrequency Frequency { get; init; } = BackupFrequency.Daily;

    /// <summary>How long past due before it counts as late. Null for the frequency's default.</summary>
    public int? GraceHours { get; init; }

    /// <summary>Whether lateness is sent through the alert channels, or only shown.</summary>
    public bool AlertWhenLate { get; init; } = true;

    public DrillCadence Drill { get; init; } = DrillCadence.Quarterly;

    public int Position { get; init; }

    /// <summary>When it was added. The first restore drill falls due one cadence after this, not the moment it exists.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // ---- kept by the sweep, not the editor ----

    /// <summary>
    /// The newest success anything has proven, remembered. A source that goes away — the
    /// backup server is rebooting — still leaves a date to judge by, and a manual item has
    /// nothing else.
    /// </summary>
    public DateTimeOffset? LastSuccess { get; init; }

    /// <summary>Who ticked it, for a manual one; empty when a source proved it.</summary>
    public string LastSuccessBy { get; init; } = "";

    /// <summary>What the last sweep judged it, so the feed records the moment it went late and not every sweep after.</summary>
    public string LastState { get; init; } = "";

    /// <summary>Whether the current lateness has been sent — the "once, not every sweep" of a late alert.</summary>
    public bool LateAnnounced { get; init; }

    /// <summary>The drill due date a reminder has gone out for. A new due date, after a test, is reminded afresh.</summary>
    public DateTimeOffset? DrillRemindedFor { get; init; }

    /// <summary>When a restore was last tried. Read from the restore tests, not stored here.</summary>
    public DateTimeOffset? LastRestoreTest { get; init; }

    public string LastRestoreTestBy { get; init; } = "";

    /// <summary>What the rest of the app calls it on a mute window or a silence: the thing it protects, or the thing that proves it.</summary>
    public string? AboutConnectionId =>
        ConnectionId is { Length: > 0 } id ? id
        : Source == BackupSources.Metric && SourceTarget.Length > 0 ? SourceTarget
        : null;

    public int EffectiveGraceHours => GraceHours is >= 0 ? GraceHours.Value : BackupSchedule.DefaultGraceHours(Frequency);

    /// <summary>What is wrong with it, or null. Checked on save.</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "Give it a name — it is what the list and an alert call it.";
        if (Source == BackupSources.Metric && (SourceTarget.Length == 0 || SourceMetric.Trim().Length == 0))
            return "Choose the connection and the reading that proves it.";
        if (Source == BackupSources.Offsite && SourceTarget.Length == 0)
            return "Choose the off-site destination.";
        if (Source is not (BackupSources.Manual or BackupSources.LabbyTwo or BackupSources.Offsite or BackupSources.Metric))
            return $"“{Source}” is not somewhere a backup can be proven from.";
        if (GraceHours is < 0 or > 24 * 60)
            return "The grace has to be between 0 and 1440 hours.";
        return null;
    }
}

/// <summary>What an item's source says right now.</summary>
/// <param name="LastSuccess">When it says the last good backup finished; null for "none" or "cannot tell".</param>
/// <param name="Problem">Why it cannot be asked, in words; null when it answered.</param>
/// <param name="Source">What proved it, for the page and the feed: "Duplicati · hours_since_backup".</param>
public sealed record BackupReading(DateTimeOffset? LastSuccess, string? Problem, string Source)
{
    public static BackupReading Manual { get; } = new(null, null, "ticked by hand");
}

/// <summary>An item judged at one moment.</summary>
/// <param name="LastSuccess">The newest success proven, by the source now or remembered.</param>
/// <param name="DueBy">When it becomes late. Null for one that has never succeeded.</param>
/// <param name="Problem">Why the source could not be read, even when a remembered date still says it is on time.</param>
public sealed record BackupStatus(BackupState State, DateTimeOffset? LastSuccess, DateTimeOffset? DueBy, string? Problem)
{
    public TimeSpan? Age(DateTimeOffset now) => LastSuccess is { } last ? (now - last < TimeSpan.Zero ? TimeSpan.Zero : now - last) : null;
}

/// <summary>An item with everything the page, the shortcode and the summary say about it.</summary>
public sealed record BackupRow(BackupItem Item, BackupReading Reading, BackupStatus Status, DateTimeOffset? DrillDue, bool DrillOverdue)
{
    /// <summary>Anything worth a second look: not on time, or a restore test overdue.</summary>
    public bool NeedsAttention => Status.State != BackupState.Ok || DrillOverdue;
}

/// <summary>One "Mark restore tested": who, when, and what they found.</summary>
public sealed record RestoreTest(long Id, string ItemId, DateTimeOffset At, string Who, string Notes);

/// <summary>
/// The arithmetic of lateness and drills. Pure — the zone and the instant are passed in —
/// so a weekly backup across the night the clocks change, and a monthly one from the 31st,
/// are pinned by tests rather than found in an alert.
///
/// Frequencies are calendar steps in the lab's own zone, not fixed spans: a nightly backup
/// at 02:00 is due at 02:00 the next night whether that night is 23 or 25 hours long, and
/// a monthly one taken on 31 January is due on the last day of February, not on 3 March.
/// Hourly is the one fixed span, because an hour is an hour whatever the clocks say.
/// </summary>
public static class BackupSchedule
{
    /// <summary>
    /// How late a backup can be before it is called late, by default: enough for a slow
    /// night or a job that runs a little later each time, not enough to miss a whole run.
    /// </summary>
    public static int DefaultGraceHours(BackupFrequency frequency) => frequency switch
    {
        BackupFrequency.Hourly => 1,
        BackupFrequency.Daily => 6,
        BackupFrequency.Weekly => 24,
        _ => 72,
    };

    /// <summary>When the next backup after <paramref name="last"/> is expected.</summary>
    public static DateTimeOffset Next(DateTimeOffset last, BackupFrequency frequency, TimeZoneInfo zone)
    {
        if (frequency == BackupFrequency.Hourly)
            return last.AddHours(1);

        var local = TimeZoneInfo.ConvertTime(last, zone).DateTime;
        var date = DateOnly.FromDateTime(local);
        var time = TimeOnly.FromDateTime(local);
        var next = frequency switch
        {
            BackupFrequency.Daily => date.AddDays(1),
            BackupFrequency.Weekly => date.AddDays(7),
            _ => date.AddMonths(1),
        };
        return WeeklySchedule.At(next, time, zone);
    }

    /// <summary>When a backup last taken at <paramref name="last"/> becomes late.</summary>
    public static DateTimeOffset DueBy(DateTimeOffset last, BackupFrequency frequency, int graceHours, TimeZoneInfo zone) =>
        Next(last, frequency, zone).AddHours(Math.Max(0, graceHours));

    /// <summary>
    /// What an item is, at <paramref name="now"/>. The newer of what the source says and
    /// what was remembered counts: a source is the authority while it answers, and memory
    /// is what is left when it does not. Late is strictly past the due time — at the stroke
    /// of it, it is still on time, as a threshold rule is.
    /// </summary>
    public static BackupStatus Judge(BackupItem item, BackupReading reading, DateTimeOffset now, TimeZoneInfo zone)
    {
        var last = Newest(reading.LastSuccess, item.LastSuccess);
        if (last is null)
            return new BackupStatus(reading.Problem is null ? BackupState.Never : BackupState.Missing, null, null, reading.Problem);

        var due = DueBy(last.Value, item.Frequency, item.EffectiveGraceHours, zone);
        return new BackupStatus(now > due ? BackupState.Late : BackupState.Ok, last, due, reading.Problem);
    }

    /// <summary>How many months between restore drills; zero for none.</summary>
    public static int DrillMonths(DrillCadence cadence) => cadence switch
    {
        DrillCadence.Monthly => 1,
        DrillCadence.Quarterly => 3,
        DrillCadence.HalfYearly => 6,
        DrillCadence.Yearly => 12,
        _ => 0,
    };

    /// <summary>
    /// When the next restore test is due: one cadence after the last, or — for an item never
    /// tested — one cadence after it was added. Straight away would greet every new item
    /// with a reminder, which is how reminders get ignored. Null when drills are off.
    /// </summary>
    public static DateTimeOffset? DrillDue(BackupItem item, TimeZoneInfo zone)
    {
        var months = DrillMonths(item.Drill);
        if (months == 0)
            return null;
        var from = TimeZoneInfo.ConvertTime(item.LastRestoreTest ?? item.CreatedAt, zone).DateTime;
        return WeeklySchedule.At(DateOnly.FromDateTime(from).AddMonths(months), TimeOnly.FromDateTime(from), zone);
    }

    public static BackupRow Row(BackupItem item, BackupReading reading, DateTimeOffset now, TimeZoneInfo zone)
    {
        var due = DrillDue(item, zone);
        return new BackupRow(item, reading, Judge(item, reading, now, zone), due, due is { } d && now > d);
    }

    /// <summary>
    /// When a reading says the last backup was. Two shapes are understood, which between
    /// them cover every backup provider and anything a JSON or Prometheus connection can be
    /// pointed at:
    /// <list type="bullet">
    /// <item>A timestamp — anything past 1 000 000 000, which is 2001 in Unix seconds and no
    /// plausible age. Milliseconds are recognised by size, as a JavaScript clock writes them.</item>
    /// <item>An age, measured back from when the reading was taken: hours by default, as
    /// <c>hours_since_backup</c> is, or minutes, days or seconds when the metric's unit or
    /// its name says so.</item>
    /// </list>
    /// Null for a negative or non-finite number, which proves nothing.
    /// </summary>
    public static DateTimeOffset? FromMetric(string metric, string unit, double value, DateTimeOffset readAt)
    {
        if (!double.IsFinite(value) || value < 0)
            return null;
        if (value >= 1e12)
            return value < 1e14 ? DateTimeOffset.FromUnixTimeMilliseconds((long)value) : null;
        if (value >= 1e9)
            return DateTimeOffset.FromUnixTimeSeconds((long)value);

        var name = metric.ToLowerInvariant();
        var suffix = unit.Trim().ToLowerInvariant();
        var age = suffix is "min" or "mins" or "minutes" or "m" || name.Contains("minutes", StringComparison.Ordinal) ? TimeSpan.FromMinutes(value)
            : suffix is "d" or "days" or "day" || name.Contains("days", StringComparison.Ordinal) ? TimeSpan.FromDays(value)
            : suffix is "s" or "sec" or "seconds" || name.Contains("seconds", StringComparison.Ordinal) ? TimeSpan.FromSeconds(value)
            : TimeSpan.FromHours(value);
        return readAt - age;
    }

    /// <summary>
    /// Whether a metric reads like proof of a backup — an age or a last-success time — so
    /// the editor can offer those first among everything a connection reports.
    /// </summary>
    public static bool LooksLikeProof(string metric, string unit)
    {
        var name = metric.ToLowerInvariant();
        return name.Contains("since", StringComparison.Ordinal)
               || name.Contains("age", StringComparison.Ordinal)
               || name.Contains("last_success", StringComparison.Ordinal)
               || name.Contains("timestamp", StringComparison.Ordinal)
               || name.Contains("backup", StringComparison.Ordinal)
               || unit.Trim() == "h";
    }

    private static DateTimeOffset? Newest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    // ---------- words ----------

    public static string Describe(BackupFrequency frequency) => frequency switch
    {
        BackupFrequency.Hourly => "hourly",
        BackupFrequency.Daily => "daily",
        BackupFrequency.Weekly => "weekly",
        _ => "monthly",
    };

    public static string Describe(DrillCadence cadence) => cadence switch
    {
        DrillCadence.Monthly => "monthly",
        DrillCadence.Quarterly => "every three months",
        DrillCadence.HalfYearly => "every six months",
        DrillCadence.Yearly => "yearly",
        _ => "off",
    };

    public static string Describe(BackupState state) => state switch
    {
        BackupState.Ok => "on time",
        BackupState.Late => "late",
        BackupState.Missing => "missing",
        _ => "never backed up",
    };

    /// <summary>The status dot: green on time, red late, amber for "cannot tell", grey for never.</summary>
    public static string Dot(BackupState state) => state switch
    {
        BackupState.Ok => "status-up",
        BackupState.Late => "status-down",
        BackupState.Missing => "status-flapping",
        _ => "status-unknown",
    };

    /// <summary>"Tue 3 Sep 02:14", or with the year when it is not this one.</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var year = TimeZoneInfo.ConvertTime(now, zone).Year;
        return local.ToString(local.Year == year ? "ddd d MMM HH:mm" : "d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>A date without the time, for restore tests, where the hour is noise.</summary>
    public static string Day(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var year = TimeZoneInfo.ConvertTime(now, zone).Year;
        return local.ToString(local.Year == year ? "ddd d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>One line on the state of a row — "last backup 14h ago", "late — last one Tue 3 Sep 02:14".</summary>
    public static string Summary(BackupRow row, DateTimeOffset now, TimeZoneInfo zone) => row.Status switch
    {
        { State: BackupState.Never } => "never backed up",
        { State: BackupState.Missing, Problem: { } why } => $"missing — {why}",
        { State: BackupState.Missing } => "missing",
        { State: BackupState.Late, LastSuccess: { } last } => $"late — last one {When(last, now, zone)}, {Ago.Since(last, now)}",
        { LastSuccess: { } ok } => $"last backup {Ago.Since(ok, now)}",
        _ => Describe(row.Status.State),
    };
}

/// <summary>
/// What one sweep should send and remember about one item. Pure, so "a late backup is
/// announced once", "a held reminder goes out when the hold lifts" and "a new due date is
/// reminded afresh" are pinned without a clock or a channel.
/// </summary>
/// <param name="SendLate">Announce that it is late.</param>
/// <param name="SendRecovered">Announce that an announced lateness is over.</param>
/// <param name="SendDrill">Remind that a restore test is due.</param>
public sealed record BackupNotices(bool SendLate, bool SendRecovered, bool SendDrill)
{
    public static BackupNotices For(BackupRow row) => new(
        SendLate: row.Status.State == BackupState.Late && row.Item.AlertWhenLate && !row.Item.LateAnnounced,
        SendRecovered: row.Status.State != BackupState.Late && row.Item.LateAnnounced,
        SendDrill: row.DrillOverdue && row.DrillDue is { } due && row.Item.DrillRemindedFor != due);

    public bool Any => SendLate || SendRecovered || SendDrill;

    /// <summary>
    /// The state to keep after sending. A late alert or a drill reminder counts as sent only
    /// if it reached a channel — one held by quiet hours, maintenance or a mute window goes
    /// on the first sweep after the hold lifts, rather than never. A recovery is forgotten
    /// either way: nobody needs to hear at 07:00 that a backup came back at 03:00.
    /// </summary>
    public BackupItem Apply(BackupItem item, DateTimeOffset? drillDue, bool lateSent, bool drillSent) => item with
    {
        LateAnnounced = SendRecovered ? false : SendLate ? lateSent : item.LateAnnounced,
        DrillRemindedFor = SendDrill && drillSent ? drillDue : item.DrillRemindedFor,
    };
}

/// <summary>What <c>{{backups: …}}</c> asks for.</summary>
/// <param name="OnlyAttention">Only what is late, missing, never done, or has a restore test overdue.</param>
public sealed record BackupListOptions(bool OnlyAttention, int Limit)
{
    public const int DefaultLimit = 10;

    /// <summary>
    /// <c>{{backups}}</c>, <c>{{backups: late}}</c>, <c>{{backups: limit=5}}</c>. Null with
    /// why when something written means nothing.
    /// </summary>
    public static BackupListOptions? Parse(Shortcode code, out string? problem)
    {
        var problems = new List<string>();
        var which = (code.Part(0) is { Length: > 0 } part ? part : code.Option("show", "all")).Trim().ToLowerInvariant();
        if (which is not ("all" or "late" or "attention" or "problems"))
            problems.Add($"“{which}” is not something backups can show. Use late or all.");

        var limit = DefaultLimit;
        var written = code.Option("limit");
        if (written.Length > 0)
        {
            if (int.TryParse(written, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1)
                limit = Math.Min(n, 100);
            else
                problems.Add($"“{written}” is not a number of lines. Write limit=10.");
        }

        problem = problems.Count > 0 ? string.Join(" ", problems) : null;
        return problem is null ? new BackupListOptions(which != "all", limit) : null;
    }

    /// <summary>
    /// The rows a list shows: the ones needing attention first — late, then missing, then
    /// never, then a drill overdue — then the rest in the page's own order.
    /// </summary>
    public IReadOnlyList<BackupRow> Pick(IEnumerable<BackupRow> rows) =>
    [
        .. rows.Where(r => !OnlyAttention || r.NeedsAttention)
            .OrderBy(r => r.Status.State switch
            {
                BackupState.Late => 0,
                BackupState.Missing => 1,
                BackupState.Never => 2,
                _ => r.DrillOverdue ? 3 : 4,
            })
            .ThenBy(r => r.Item.Position)
            .ThenBy(r => r.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Limit),
    ];
}
