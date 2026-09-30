using System.Globalization;
using System.Text;

namespace LabbyTwo.Core;

/// <summary>
/// One service's month: how much of it was up, the outages in it, and how long it was down
/// inside the month. <see cref="UptimePercent"/> is null when nothing was measured.
/// </summary>
/// <param name="ConnectionId">For the live chart the report draws; never shown.</param>
public sealed record ServiceMonth(string Name, string ConnectionId, double? UptimePercent, IReadOnlyList<Outage> Outages, TimeSpan Down)
{
    /// <summary>Average response time over the month, when the service reports one.</summary>
    public double? AverageLatencyMs { get; init; }

    /// <summary>
    /// The service changed state more often than one read takes, so only the first part of
    /// the month was measured. Said beside it rather than guessed at.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// The month measured from its transitions: <see cref="ServiceWeek.Measure"/>'s
    /// arithmetic, which the weekly summary and the uptime strip share, with the downtime
    /// cut to the month — an outage that began on the 30th of the month before counts only
    /// from midnight on the 1st.
    /// </summary>
    public static ServiceMonth Measure(
        string name, string connectionId, StatusChange? prior, IReadOnlyList<StatusChange> events, DateTimeOffset from, DateTimeOffset to)
    {
        var measured = ServiceWeek.Measure(name, prior, events, from, to);
        var down = measured.Outages.Aggregate(TimeSpan.Zero, (sum, o) => sum + Clip(o, from, to));
        return new ServiceMonth(name, connectionId, measured.UptimePercent, measured.Outages, down);
    }

    /// <summary>The part of an outage that fell inside [from, to).</summary>
    public static TimeSpan Clip(Outage outage, DateTimeOffset from, DateTimeOffset to)
    {
        var start = outage.Start < from ? from : outage.Start;
        var end = outage.End ?? to;
        if (end > to)
            end = to;
        return end > start ? end - start : TimeSpan.Zero;
    }
}

/// <summary>One incident as the report lists it.</summary>
/// <param name="Cause">The surest probable cause, as a sentence; null when there was no obvious one.</param>
public sealed record IncidentLine(string Title, DateTimeOffset Start, DateTimeOffset? End, int Members, bool Maintenance, string? Cause);

/// <summary>An alert rule on one connection, and how many times it fired in the month.</summary>
public sealed record AlertCount(string Name, string? Connection, int Times);

/// <summary>One item from the Backups page, as the month saw it.</summary>
/// <param name="Proven">How many times a backup of it was proven — by its source or ticked by hand.</param>
/// <param name="RestoreTests">How many restores of it somebody tried.</param>
/// <param name="Late">How many times it went late.</param>
public sealed record BackupMonth(string Name, int Proven, DateTimeOffset? LastProven, int RestoreTests, int Late);

/// <summary>The month's electricity: the whole lab, and each source's share.</summary>
public sealed record PowerMonth(double Kwh, double Cost, string Currency, IReadOnlyList<(string Name, double Kwh, double Cost)> Sources);

/// <summary>A container update started from the Containers tab, and how it ended.</summary>
public sealed record UpdateLine(string Container, string Host, SafeUpdateState State, DateTimeOffset At, string Reason);

/// <summary>Something self-healing did or concluded, from the change feed.</summary>
/// <param name="Action">One of the self-healing <see cref="ChangeActions"/>.</param>
public sealed record HealingLine(DateTimeOffset At, string Action, string Title);

/// <summary>
/// Everything a month's report could say. Every part is optional, and a part with nothing in
/// it leaves its section out — an install with no smart plug gets no power section, rather
/// than one saying "0 kWh".
/// </summary>
public sealed record MonthlyReportData
{
    /// <summary>The first day of the month reported on.</summary>
    public required DateOnly Month { get; init; }

    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }

    /// <summary>The zone to print times in: the container's TZ in the app, a fixed one in tests.</summary>
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    public IReadOnlyList<ServiceMonth> Services { get; init; } = [];
    public IReadOnlyList<IncidentLine> Incidents { get; init; } = [];
    public IReadOnlyList<AlertCount> Alerts { get; init; } = [];
    public IReadOnlyList<BackupMonth> Backups { get; init; } = [];
    public PowerMonth? Power { get; init; }
    public IReadOnlyList<UpdateLine> Updates { get; init; } = [];
    public IReadOnlyList<HealingLine> Healing { get; init; } = [];

    /// <summary>
    /// How far back the change feed goes, when it is not far enough to cover the whole month —
    /// alerts, backups and self-healing come from the feed, and a feed kept for a week says
    /// only a week's worth. Null when it covers the month.
    /// </summary>
    public DateTimeOffset? FeedFrom { get; init; }
}

/// <summary>The finished report: a note for the notes tab, and a short summary for the alert channels.</summary>
public sealed record MonthlyReportNote(string Title, string Markdown, WeeklyDigest Summary);

/// <summary>
/// Turns a month of data into a note worth keeping: a line saying how the month went, then
/// one section per thing that happened, each left out when nothing did.
///
/// Where the weekly summary is a nudge to be read on a phone, this is the record — so it
/// lists rather than picks, within reason: every service's uptime in one table, the
/// incidents with what probably caused them, the alerts that fired most. And where a live
/// value says something no table can, it is one: the response time of the least available
/// services is a <c>{{chart}}</c> of the month itself, fixed to its dates, so it draws the
/// same month whenever the note is opened.
///
/// A pure function of <see cref="MonthlyReportData"/>, so a good month, a bad one and an
/// empty one are pinned by tests. Every name in it is the user's own text and is escaped —
/// for Markdown, and for the shortcode braces, so a service called <c>{{button: …}}</c> is
/// a name in a table rather than a button in the report.
/// </summary>
public static class MonthlyReport
{
    /// <summary>The most services listed by name; past it the rest at 100% are counted, and anything less than 100% is always listed.</summary>
    public const int MaxServices = 25;
    public const int MaxIncidents = 15;
    public const int MaxAlerts = 5;
    public const int MaxBackups = 15;
    public const int MaxPowerSources = 5;
    public const int MaxUpdates = 15;
    public const int MaxHealing = 10;

    /// <summary>Lines on the report's response-time chart.</summary>
    public const int ChartLines = 3;

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static MonthlyReportNote Build(MonthlyReportData data)
    {
        var name = MonthlySchedule.Name(data.Month);
        var title = $"Monthly report · {name}";
        var markdown = new StringBuilder();
        var summary = new List<Said>();

        // The headline holds numbers and the month's name, never anybody's text, so its bold
        // marks are the only Markdown in it.
        var headline = Headline(data);
        markdown.Append(headline).Append("\n\n");

        if (data.FeedFrom is { } feedFrom)
        {
            markdown.Append("> [!NOTE]\n> The change feed only goes back to ")
                .Append(When(feedFrom, data))
                .Append(", so alerts, backups and self-healing before then are not counted. `Labby__ChangeRetentionDays` sets how long it is kept.\n\n");
        }

        Section(markdown, UptimeSection(data));
        Section(markdown, IncidentsSection(data, summary));
        Section(markdown, AlertsSection(data, summary));
        Section(markdown, BackupsSection(data, summary));
        Section(markdown, PowerSection(data, summary));
        Section(markdown, UpdatesSection(data, summary));
        Section(markdown, HealingSection(data, summary));

        markdown.Append("---\n\n").Append("*Made by LabbyTwo on ")
            .Append(TimeZoneInfo.ConvertTime(data.To, data.Zone).ToString("d MMMM yyyy", Culture))
            .Append(" from what it recorded in ").Append(name).Append(".*\n");

        var text = new StringBuilder(headline.Replace("**", "", StringComparison.Ordinal));
        var md = new StringBuilder(headline);
        foreach (var line in summary)
        {
            text.Append("\n• ").Append(line.Plain);
            md.Append("\n- ").Append(line.Markdown);
        }
        const string where = "The whole report is on the Monthly reports tab.";
        text.Append("\n\n").Append(where);
        md.Append("\n\n").Append(where);

        return new MonthlyReportNote(title, markdown.ToString().TrimEnd() + "\n", new WeeklyDigest(title, text.ToString(), md.ToString()));
    }

    /// <summary>One line of the channel summary, as plain text and as Markdown with the names escaped.</summary>
    private sealed record Said(string Plain, string Markdown);

    private static void Section(StringBuilder markdown, string? section)
    {
        if (section is { Length: > 0 })
            markdown.Append(section.TrimEnd()).Append("\n\n");
    }

    // ---------- the sections ----------

    /// <summary>
    /// One sentence. Written with **bold** marks as plain text; the Markdown versions escape
    /// everything and then put the bold back, so a name in it can never add its own.
    /// </summary>
    private static string Headline(MonthlyReportData data)
    {
        var name = MonthlySchedule.Name(data.Month);
        var measured = data.Services.Where(s => s.UptimePercent is not null).ToList();
        if (measured.Count == 0)
            return $"Nothing was monitored in {name}.";

        var services = measured.Count == 1 ? "1 service" : $"{measured.Count} services";
        var down = measured.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Down);
        if (down == TimeSpan.Zero && measured.All(s => s.Outages.Count == 0))
            return $"Everything stayed up in {name}: **100%** across {services}, with no outages.";

        // The mean of the percentages, as the weekly summary has it: every service counts
        // once, so one flapping container does not vanish into a dozen healthy ones.
        var overall = measured.Average(s => s.UptimePercent!.Value);
        var incidents = data.Incidents.Count switch
        {
            0 => "",
            1 => "1 incident, ",
            var n => $"{n} incidents, ",
        };
        return $"**{WeeklySummary.Percent(overall)}** uptime across {services} in {name} — {incidents}**{WeeklySummary.Duration(down)}** down in all.";
    }

    private static string? UptimeSection(MonthlyReportData data)
    {
        var measured = data.Services.Where(s => s.UptimePercent is not null)
            .OrderBy(s => s.UptimePercent)
            .ThenByDescending(s => s.Down)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (measured.Count == 0)
            return null;

        var withLatency = measured.Any(s => s.AverageLatencyMs is not null);
        var section = new StringBuilder("## Uptime\n\n");
        section.Append(withLatency
            ? "| Service | Uptime | Outages | Down for | Response |\n|---|---:|---:|---:|---:|\n"
            : "| Service | Uptime | Outages | Down for |\n|---|---:|---:|---:|\n");

        // Everything with an outage is listed; perfect services only while there is room.
        var shown = measured.Where((s, i) => i < MaxServices || s.Outages.Count > 0 || s.UptimePercent < 100).ToList();
        foreach (var service in shown)
        {
            section.Append("| ").Append(Escape(service.Name)).Append(service.Truncated ? " (only part of the month could be read)" : "")
                .Append(" | ").Append(WeeklySummary.Percent(service.UptimePercent!.Value))
                .Append(" | ").Append(service.Outages.Count.ToString(Culture))
                .Append(" | ").Append(service.Down > TimeSpan.Zero ? WeeklySummary.Duration(service.Down) : "—");
            if (withLatency)
                section.Append(" | ").Append(service.AverageLatencyMs is { } ms ? Milliseconds(ms) : "—");
            section.Append(" |\n");
        }
        if (measured.Count > shown.Count)
        {
            var rest = measured.Count - shown.Count;
            section.Append('\n').Append(rest == 1 ? "…and 1 more, up all month." : $"…and {rest} more, all up all month.").Append('\n');
        }

        if (ChartSection(data) is { } chart)
            section.Append('\n').Append(chart).Append('\n');
        return section.ToString();
    }

    /// <summary>
    /// The response time of the least available services — or, in a month where everything
    /// stayed up, the slowest — as a live chart fixed to the month's dates. By name, so the
    /// note reads like the rest of the notes; a service renamed later shows a "?" there,
    /// which says so, rather than quietly drawing something else.
    /// </summary>
    public static string? ChartSection(MonthlyReportData data)
    {
        var candidates = data.Services.Where(s => s.AverageLatencyMs is not null && s.UptimePercent is not null).ToList();
        if (candidates.Count == 0)
            return null;

        var worst = candidates.Any(s => s.UptimePercent < 100)
            ? candidates.OrderBy(s => s.UptimePercent).ThenByDescending(s => s.AverageLatencyMs).Take(ChartLines).ToList()
            : candidates.OrderByDescending(s => s.AverageLatencyMs).Take(ChartLines).ToList();
        var title = candidates.Count <= ChartLines
            ? "Response time"
            : candidates.Any(s => s.UptimePercent < 100) ? "Response time of the least available" : "Response time of the slowest";

        return ChartShortcode.Write(
            worst.Select(s => new ChartLine(s.Name, "latency_ms")),
            [
                new("from", data.Month.ToString("yyyy-MM-dd", Culture)),
                new("to", data.Month.AddMonths(1).ToString("yyyy-MM-dd", Culture)),
                new("title", title),
            ]);
    }

    private static string? IncidentsSection(MonthlyReportData data, List<Said> summary)
    {
        if (data.Incidents.Count == 0)
            return null;

        var ordered = data.Incidents.OrderByDescending(i => Length(i, data)).ThenBy(i => i.Start).ToList();
        var longest = ordered[0];
        var incidents = $"{Count(data.Incidents.Count, "incident")}, the longest {WeeklySummary.Duration(Length(longest, data))}";
        summary.Add(new Said($"{incidents} ({longest.Title})", $"{incidents} ({Escape(longest.Title)})"));

        var section = new StringBuilder("## Incidents\n\n");
        foreach (var incident in data.Incidents.OrderBy(i => i.Start).Take(MaxIncidents))
        {
            var length = WeeklySummary.Duration(Length(incident, data));
            section.Append("- **").Append(Escape(incident.Title)).Append("** — ")
                .Append(incident.End is null ? $"still open, {length} so far" : length)
                .Append(", ").Append(When(incident.Start, data));
            if (incident.Maintenance)
                section.Append(" *(during maintenance)*");
            section.Append(incident.Cause is { Length: > 0 } cause ? $". Probably: {Escape(cause)}" : ". No obvious cause.");
            section.Append('\n');
        }
        if (data.Incidents.Count > MaxIncidents)
            section.Append("- …and ").Append(data.Incidents.Count - MaxIncidents).Append(" more on the Incidents page\n");
        return section.ToString();
    }

    private static string? AlertsSection(MonthlyReportData data, List<Said> summary)
    {
        var fired = data.Alerts.Where(a => a.Times > 0)
            .OrderByDescending(a => a.Times).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (fired.Count == 0)
            return null;

        var total = fired.Sum(a => a.Times);
        var often = $"Alerts fired {Count(total, "time")}; most often";
        var times = $"({Count(fired[0].Times, "time")})";
        summary.Add(new Said($"{often} {fired[0].Name} {times}", $"{often} {Escape(fired[0].Name)} {times}"));

        var section = new StringBuilder("## Alerts that fired most\n\n");
        foreach (var alert in fired.Take(MaxAlerts))
        {
            section.Append("- **").Append(Escape(alert.Name)).Append("**")
                .Append(alert.Connection is { Length: > 0 } on ? $" on {Escape(on)}" : "")
                .Append(" — ").Append(Count(alert.Times, "time")).Append('\n');
        }
        if (fired.Count > MaxAlerts)
            section.Append("- …and ").Append(fired.Count - MaxAlerts).Append(" more, ").Append(Count(fired.Skip(MaxAlerts).Sum(a => a.Times), "time")).Append(" between them\n");
        return section.ToString();
    }

    private static string? BackupsSection(MonthlyReportData data, List<Said> summary)
    {
        var active = data.Backups.Where(b => b.Proven > 0 || b.RestoreTests > 0 || b.Late > 0).ToList();
        if (active.Count == 0)
            return null;

        var proven = active.Sum(b => b.Proven);
        var tests = active.Sum(b => b.RestoreTests);
        summary.Add(Plain($"Backups proven {Count(proven, "time")}; {(tests == 0 ? "no restore tested" : $"{Count(tests, "restore")} tested")}"));

        var section = new StringBuilder("## Backups\n\n");
        foreach (var backup in active.OrderByDescending(b => b.Late).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).Take(MaxBackups))
        {
            var parts = new List<string>();
            parts.Add(backup.Proven == 0 ? "never proven" : $"proven {Count(backup.Proven, "time")}"
                + (backup.LastProven is { } last ? $", last on {Day(last, data)}" : ""));
            if (backup.Late > 0)
                parts.Add($"late {Count(backup.Late, "time")}");
            if (backup.RestoreTests > 0)
                parts.Add($"restore tested {(backup.RestoreTests == 1 ? "once" : Count(backup.RestoreTests, "time"))}");
            section.Append("- **").Append(Escape(backup.Name)).Append("** — ").Append(string.Join("; ", parts)).Append('\n');
        }
        if (active.Count > MaxBackups)
            section.Append("- …and ").Append(active.Count - MaxBackups).Append(" more on the Backups page\n");
        if (tests == 0)
            section.Append("\nNo restore was tested this month. A backup is only proven good by restoring it — the Backups page keeps track of when each was last tried.\n");
        return section.ToString();
    }

    private static string? PowerSection(MonthlyReportData data, List<Said> summary)
    {
        if (data.Power is not { Kwh: > 0 } power)
            return null;

        var total = $"{PowerShortcode.Kwh(power.Kwh)}, about {PowerTariff.FormatMoney(power.Cost, power.Currency)}";
        summary.Add(Plain($"Power: {total}"));

        var section = new StringBuilder("## Power\n\n").Append(total).Append(".\n");
        var sources = power.Sources.Where(s => s.Kwh > 0).OrderByDescending(s => s.Cost).ThenByDescending(s => s.Kwh).ToList();
        if (sources.Count > 1)
        {
            section.Append('\n');
            foreach (var (name, kwh, cost) in sources.Take(MaxPowerSources))
            {
                section.Append("- **").Append(Escape(name)).Append("** — ").Append(PowerShortcode.Kwh(kwh))
                    .Append(", ").Append(PowerTariff.FormatMoney(cost, power.Currency)).Append('\n');
            }
            if (sources.Count > MaxPowerSources)
                section.Append("- …and ").Append(sources.Count - MaxPowerSources).Append(" more on the Power page\n");
        }
        return section.ToString();
    }

    private static string? UpdatesSection(MonthlyReportData data, List<Said> summary)
    {
        if (data.Updates.Count == 0)
            return null;

        var rolledBack = data.Updates.Count(u => u.State == SafeUpdateState.RolledBack);
        var failed = data.Updates.Count(u => u.State == SafeUpdateState.RollbackFailed);
        var passed = data.Updates.Count(u => u.State is SafeUpdateState.Passed or SafeUpdateState.Updated);

        var counts = new List<string>();
        if (passed > 0)
            counts.Add($"{passed} went through");
        if (rolledBack > 0)
            counts.Add($"{rolledBack} rolled back");
        if (failed > 0)
            counts.Add($"{failed} could not be rolled back");
        var others = data.Updates.Count - passed - rolledBack - failed;
        if (others > 0)
            counts.Add($"{others} other{(others == 1 ? "" : "s")}");
        var line = $"{Count(data.Updates.Count, "container update")}: {string.Join(", ", counts)}";
        summary.Add(Plain(line));

        var section = new StringBuilder("## Updates\n\n").Append(line).Append(".\n\n");
        // Trouble first — that is what somebody reading this wants — then the rest by date.
        foreach (var update in data.Updates
                     .OrderBy(u => u.State is SafeUpdateState.Passed or SafeUpdateState.Updated ? 1 : 0)
                     .ThenBy(u => u.At)
                     .Take(MaxUpdates))
        {
            section.Append("- **").Append(Escape(update.Container)).Append("** on ").Append(Escape(update.Host))
                .Append(" — ").Append(Word(update.State)).Append(", ").Append(When(update.At, data));
            if (update.Reason is { Length: > 0 } reason && update.State is not (SafeUpdateState.Passed or SafeUpdateState.Updated))
                section.Append(". ").Append(Escape(Shorten(reason, 200)));
            section.Append('\n');
        }
        if (data.Updates.Count > MaxUpdates)
            section.Append("- …and ").Append(data.Updates.Count - MaxUpdates).Append(" more\n");
        return section.ToString();
    }

    private static string? HealingSection(MonthlyReportData data, List<Said> summary)
    {
        if (data.Healing.Count == 0)
            return null;

        var ran = data.Healing.Count(h => h.Action == ChangeActions.Remediated);
        var failed = data.Healing.Count(h => h.Action == ChangeActions.Failed);
        var helped = data.Healing.Count(h => h.Action == ChangeActions.Helped);
        var notHelped = data.Healing.Count(h => h.Action == ChangeActions.NotHelped);

        var parts = new List<string> { $"{Count(ran, "automatic action")} run" };
        if (helped > 0)
            parts.Add($"{helped} fixed it");
        if (notHelped > 0)
            parts.Add($"{notHelped} did not help");
        if (failed > 0)
            parts.Add($"{failed} could not run");
        var line = string.Join(", ", parts);
        summary.Add(Plain($"Self-healing: {line}"));

        var section = new StringBuilder("## Self-healing\n\n").Append(line).Append(".\n\n");
        var listed = data.Healing
            .Where(h => h.Action is ChangeActions.Remediated or ChangeActions.Failed or ChangeActions.NotHelped or ChangeActions.Helped)
            .OrderBy(h => h.At).ToList();
        foreach (var heal in listed.Take(MaxHealing))
            section.Append("- ").Append(When(heal.At, data)).Append(" — ").Append(Escape(heal.Title)).Append('\n');
        if (listed.Count > MaxHealing)
            section.Append("- …and ").Append(listed.Count - MaxHealing).Append(" more in the change feed\n");
        return section.ToString();
    }

    // ---------- phrasing ----------

    /// <summary>A summary line with nobody's text in it, only numbers and fixed words — the same either way.</summary>
    private static Said Plain(string line) => new(line, line);

    /// <summary>How long an incident lasted inside the month; one still open counts to the month's end.</summary>
    private static TimeSpan Length(IncidentLine incident, MonthlyReportData data)
    {
        var end = incident.End ?? data.To;
        return end > incident.Start ? end - incident.Start : TimeSpan.Zero;
    }

    private static string Word(SafeUpdateState state) => state switch
    {
        SafeUpdateState.Passed => "passed its watch",
        SafeUpdateState.Updated => "updated, not watched",
        SafeUpdateState.RolledBack => "rolled back",
        SafeUpdateState.RollbackFailed => "failed, and could not be rolled back",
        SafeUpdateState.NotUpdated => "was not updated",
        SafeUpdateState.NotChecked => "could not be checked",
        SafeUpdateState.Abandoned => "taken over by something else",
        _ => "still being watched",
    };

    /// <summary>"Tue 8 Sep 14:03", in the report's zone.</summary>
    private static string When(DateTimeOffset at, MonthlyReportData data) =>
        TimeZoneInfo.ConvertTime(at, data.Zone).ToString("ddd d MMM HH:mm", Culture);

    private static string Day(DateTimeOffset at, MonthlyReportData data) =>
        TimeZoneInfo.ConvertTime(at, data.Zone).ToString("d MMM", Culture);

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n.ToString("N0", Culture)} {noun}s";

    private static string Milliseconds(double ms) =>
        ms >= 1000 ? $"{(ms / 1000).ToString("0.0", Culture)} s" : $"{ms.ToString("0", Culture)} ms";

    private static string Shorten(string text, int max)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// The user's own text made safe for the note: Markdown's marks escaped as the weekly
    /// summary escapes them, and the braces too, so nothing in a name can start a shortcode,
    /// a heading or a table cell. Line breaks become spaces: a name is one line.
    /// </summary>
    public static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in WeeklySummary.Escape(text.ReplaceLineEndings(" ")))
        {
            if (c is '{' or '}' or '<')
                builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }
}
