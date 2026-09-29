using System.Globalization;
using System.Text;

namespace LabbyTwo.Core;

/// <summary>One up/down transition, as the weekly summary needs it. Core's own shape, so the builder never sees storage.</summary>
public sealed record StatusChange(DateTimeOffset At, bool IsUp, string Message = "");

/// <summary>A stretch of time a service was down. <see cref="End"/> is null while it still is.</summary>
public sealed record Outage(DateTimeOffset Start, DateTimeOffset? End, TimeSpan Duration, string Message)
{
    public bool Ongoing => End is null;
}

/// <summary>
/// One service's week: how much of it was up, the outages inside it, and how slow it was to
/// answer. <see cref="UptimePercent"/> is null when nothing was measured — a service added
/// after the week ended, or one that has never reported — which is different from 0%.
/// </summary>
public sealed record ServiceWeek(string Name, double? UptimePercent, IReadOnlyList<Outage> Outages)
{
    /// <summary>Average response time over the week, when the service reports one.</summary>
    public double? AverageLatencyMs { get; init; }

    public bool DownNow => Outages.Any(o => o.Ongoing);

    /// <summary>
    /// Walks one service's transitions across the window and says what the week looked like.
    ///
    /// The same arithmetic as the uptime strip: the state carried in from before the window
    /// counts for the time before the first event inside it, and a service with no event
    /// before the window is measured only from its first one — a service added on Thursday
    /// is not blamed, or credited, for Monday. Kept here, pure, rather than asked of the
    /// database twice, because the outages and the percentage have to agree: a week with a
    /// two-hour outage in it cannot read as 100%.
    /// </summary>
    /// <param name="prior">The newest transition before <paramref name="from"/>, if there is one.</param>
    /// <param name="events">Transitions inside the window, oldest first.</param>
    public static ServiceWeek Measure(
        string name, StatusChange? prior, IReadOnlyList<StatusChange> events, DateTimeOffset from, DateTimeOffset to)
    {
        if (prior is null && events.Count == 0)
            return new ServiceWeek(name, null, []);

        var measureFrom = prior is not null ? from : events[0].At;
        var state = prior?.IsUp ?? events[0].IsUp;

        // A service that was already down when the week began: the outage started before
        // it, and saying so ("down since last Sunday") is more honest than pretending it
        // began at the window's edge.
        DateTimeOffset? downSince = state ? null : prior?.At ?? measureFrom;
        var downMessage = state ? "" : prior?.Message ?? events[0].Message;

        var outages = new List<Outage>();
        var cursor = measureFrom;
        var up = TimeSpan.Zero;

        foreach (var change in events)
        {
            var at = change.At < measureFrom ? measureFrom : change.At > to ? to : change.At;
            if (state && at > cursor)
                up += at - cursor;
            if (at > cursor)
                cursor = at;

            if (state && !change.IsUp)
            {
                downSince = change.At;
                downMessage = change.Message;
            }
            else if (!state && change.IsUp && downSince is { } began)
            {
                outages.Add(new Outage(began, change.At, change.At - began, downMessage));
                downSince = null;
            }

            state = change.IsUp;
        }

        if (state && to > cursor)
            up += to - cursor;
        if (!state && downSince is { } stillDown)
            outages.Add(new Outage(stillDown, null, to - stillDown, downMessage));

        var measured = to - measureFrom;
        double? percent = measured > TimeSpan.Zero
            ? Math.Clamp(up.TotalSeconds / measured.TotalSeconds * 100, 0, 100)
            : null;

        return new ServiceWeek(name, percent, outages);
    }
}

/// <summary>
/// A capacity metric heading somewhere. <see cref="WeeklyChange"/> is the fitted rate over a
/// week, in the metric's own unit — "+1.2%" for a volume's percent used, "-40 GB" for free
/// space — so the builder can phrase it without knowing which kind it is.
/// </summary>
public sealed record CapacityLine(
    string Name, string Label, double Current, double? WeeklyChange, string Unit, int Decimals, CapacityForecast Forecast);

/// <summary>
/// Something with a date on it: a certificate, a node key, the next renewal. A line with
/// <see cref="Overdue"/> above zero is a count of things already past their date rather
/// than a countdown.
/// </summary>
public sealed record ExpiryLine(string Name, string What, double DaysLeft, int Overdue = 0);

/// <summary>
/// A week of electricity, as the Power page works it out: the whole lab, the dearest
/// sources, and the month's projection with its fixed charge.
/// </summary>
public sealed record PowerWeek(
    double Kwh, double Cost, string Currency, IReadOnlyList<(string Name, double Cost)> Top, double? ProjectedMonth = null);

/// <summary>
/// One item from the Backups page, as the summary needs it: whether it is on time, and
/// whether its restore test is overdue.
/// </summary>
public sealed record BackupLine(string Name, BackupState State, DateTimeOffset? LastSuccess, bool DrillOverdue, DateTimeOffset? LastRestoreTest);

/// <summary>A week of internet speed tests on one connection, as averages.</summary>
public sealed record SpeedWeek(string Name, double? Download, double? DownloadLowest, double? Upload, double? Ping);

/// <summary>
/// Everything the weekly summary could say, gathered in one place. Every part is optional:
/// a section with nothing in it is left out of the message rather than printed empty, so an
/// install without a NAS or a speed test gets a shorter note, not a list of blanks.
/// </summary>
public sealed record WeeklySummaryData
{
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }

    /// <summary>The zone to print times in. The container's TZ in the app; a fixed one in tests.</summary>
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    public IReadOnlyList<ServiceWeek> Services { get; init; } = [];

    /// <summary>Forecasts for every capacity metric. Only the ones filling up are mentioned.</summary>
    public IReadOnlyList<CapacityLine> Capacity { get; init; } = [];

    public IReadOnlyList<ExpiryLine> Expiries { get; init; } = [];
    public IReadOnlyList<SpeedWeek> Speed { get; init; } = [];

    /// <summary>The week's electricity, when anything reports power. Null leaves the section out.</summary>
    public PowerWeek? Power { get; init; }
    /// <summary>Everything on the Backups page. Empty for an install that lists nothing, which leaves the section out.</summary>
    public IReadOnlyList<BackupLine> Backups { get; init; } = [];

    /// <summary>Connections that appeared or went away since the last summary.</summary>
    public IReadOnlyList<string> Added { get; init; } = [];
    public IReadOnlyList<string> Removed { get; init; } = [];

    /// <summary>The version the last summary was sent from, when it differs from this one.</summary>
    public string? UpdatedFrom { get; init; }
    public string? UpdatedTo { get; init; }

    /// <summary>A newer LabbyTwo, when somebody has checked and there is one.</summary>
    public string? UpdateAvailable { get; init; }

    /// <summary>
    /// The units the reader chose, so a reading in the summary says what the dashboard says.
    /// Everything in the data above stays in the stored unit; only the words convert.
    /// </summary>
    public Units.Preferences Units { get; init; } = Core.Units.Preferences.Default;
}

/// <summary>The finished message: a title, plain text that is safe on any channel, and a Markdown version for the ones that render it.</summary>
public sealed record WeeklyDigest(string Title, string Text, string Markdown);

/// <summary>
/// Turns a week of data into a short note worth reading on a phone.
///
/// The hard part of a digest is what to leave out. Every list here is capped and every
/// section drops away when it has nothing to say, because a weekly message that is a data
/// dump gets skimmed once and muted the week after — and then the one week it says a disk
/// fills in ten days, nobody reads it. The rule of thumb: each line should be something
/// you might act on, or the reassurance that you need not.
///
/// A pure function of <see cref="WeeklySummaryData"/>, so the phrasing of a good week, a
/// bad one and an empty one can all be pinned by tests rather than discovered in a
/// notification.
/// </summary>
public static class WeeklySummary
{
    /// <summary>The longest outages listed by name; the rest are counted.</summary>
    public const int MaxOutages = 5;

    public const int MaxFlaky = 3;
    public const int MaxSlow = 3;
    public const int MaxCapacity = 5;
    public const int MaxExpiries = 6;
    public const int MaxNames = 5;

    /// <summary>A service needs to drop at least this often in a week to be called flaky; once is an outage, not a pattern.</summary>
    public const int FlakyAfter = 2;

    /// <summary>
    /// Below this, "slowest" is not news — a list of services answering in 8 ms is a list
    /// of services that are fine. Above it, the slowest are worth a glance.
    /// </summary>
    public const double SlowMs = 100;

    /// <summary>A disk further out than this from full is not worth a line in a weekly note.</summary>
    public const double CapacityHorizonDays = 90;

    /// <summary>How far ahead a renewal or a certificate is worth mentioning.</summary>
    public const double ExpiryHorizonDays = 30;

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private sealed record Section(string Emoji, string Heading, List<string> Plain, List<string> Markdown);

    public static WeeklyDigest Build(WeeklySummaryData data)
    {
        var title = $"Weekly summary · {Range(data)}";
        var headline = Headline(data, out var headlineMarkdown);

        var sections = new List<Section>();
        Add(sections, OutagesSection(data));
        Add(sections, FlakySection(data));
        Add(sections, SlowSection(data));
        Add(sections, CapacitySection(data));
        Add(sections, ExpirySection(data));
        Add(sections, SpeedSection(data));
        Add(sections, PowerSection(data));
        Add(sections, BackupsSection(data));
        Add(sections, ChangesSection(data));

        var text = new StringBuilder(headline);
        var markdown = new StringBuilder(headlineMarkdown);
        foreach (var section in sections)
        {
            text.Append("\n\n").Append(section.Emoji).Append(' ').Append(section.Heading);
            markdown.Append("\n\n").Append(section.Emoji).Append(" **").Append(section.Heading).Append("**");

            // A section that is one sentence reads better as a sentence than as a list of one.
            foreach (var line in section.Plain)
                text.Append('\n').Append(section.Plain.Count == 1 ? line : "• " + line);
            foreach (var line in section.Markdown)
                markdown.Append('\n').Append(section.Markdown.Count == 1 ? line : "- " + line);
        }

        return new WeeklyDigest(title, text.ToString(), markdown.ToString());
    }

    private static void Add(List<Section> sections, Section? section)
    {
        if (section is { Plain.Count: > 0 })
            sections.Add(section);
    }

    // ---------- the sections ----------

    private static string Headline(WeeklySummaryData data, out string markdown)
    {
        var measured = data.Services.Where(s => s.UptimePercent is not null).ToList();
        if (measured.Count == 0)
        {
            markdown = "Nothing was monitored this week.";
            return markdown;
        }

        var services = measured.Count == 1 ? "1 service" : $"{measured.Count} services";
        var outages = measured.SelectMany(s => s.Outages).ToList();

        if (outages.Count == 0)
        {
            markdown = $"Everything stayed up: 100% across {services}. No outages this week 🎉";
            return markdown;
        }

        // The mean of the percentages rather than pooled time: every service counts once,
        // so one flapping container does not vanish into a dozen healthy ones, and one
        // healthy service does not vanish into a week-long outage somewhere else.
        var overall = measured.Average(s => s.UptimePercent!.Value);
        var down = outages.Aggregate(TimeSpan.Zero, (sum, o) => sum + Clip(o, data));
        var count = outages.Count == 1 ? "1 outage" : $"{outages.Count} outages";
        var line = $"{Percent(overall)} uptime across {services}. {count}, {Duration(down)} down in all.";

        var stillDown = measured.Where(s => s.DownNow).Select(s => s.Name).ToList();
        if (stillDown.Count > 0)
        {
            var names = Names(stillDown);
            var verb = stillDown.Count == 1 ? "is" : "are";
            markdown = $"{line} **{Escape(names)} {verb} still down.**";
            return $"{line} {names} {verb} still down.";
        }

        markdown = line;
        return line;
    }

    private static Section? OutagesSection(WeeklySummaryData data)
    {
        var all = data.Services
            .SelectMany(s => s.Outages.Select(o => (s.Name, Outage: o)))
            // Still-down first — it is the one that may need doing something about — then
            // the longest, because a fifteen-second blip matters less than a lost evening.
            .OrderByDescending(x => x.Outage.Ongoing)
            .ThenByDescending(x => x.Outage.Duration)
            .ThenBy(x => x.Outage.Start)
            .ToList();

        if (all.Count == 0)
            return null;

        var section = new Section("🔻", "Outages", [], []);
        foreach (var (name, outage) in all.Take(MaxOutages))
        {
            var detail = outage.Ongoing
                ? $"still down, since {When(outage.Start, data)} ({Duration(outage.Duration)})"
                : $"{Duration(outage.Duration)}, {When(outage.Start, data)}";
            section.Plain.Add($"{name} — {detail}");
            section.Markdown.Add($"**{Escape(name)}** — {detail}");
        }

        if (all.Count > MaxOutages)
        {
            var rest = all.Skip(MaxOutages).ToList();
            var services = rest.Select(x => x.Name).Distinct().Count();
            var line = $"…and {rest.Count} shorter, across {(services == 1 ? "1 service" : $"{services} services")}";
            section.Plain.Add(line);
            section.Markdown.Add(line);
        }

        return section;
    }

    private static Section? FlakySection(WeeklySummaryData data)
    {
        var flaky = data.Services
            .Where(s => s.Outages.Count >= FlakyAfter)
            .OrderByDescending(s => s.Outages.Count)
            .ThenBy(s => s.UptimePercent ?? 100)
            .Take(MaxFlaky)
            .ToList();

        if (flaky.Count == 0)
            return null;

        var section = new Section("⚠️", "Least reliable", [], []);
        foreach (var service in flaky)
        {
            var detail = $"dropped {service.Outages.Count} times"
                         + (service.UptimePercent is { } up ? $", {Percent(up)} up" : "");
            section.Plain.Add($"{service.Name} — {detail}");
            section.Markdown.Add($"**{Escape(service.Name)}** — {detail}");
        }
        return section;
    }

    private static Section? SlowSection(WeeklySummaryData data)
    {
        var slow = data.Services
            .Where(s => s.AverageLatencyMs is >= SlowMs)
            .OrderByDescending(s => s.AverageLatencyMs)
            .Take(MaxSlow)
            .ToList();

        if (slow.Count == 0)
            return null;

        var section = new Section("🐢", "Slowest to answer", [], []);
        foreach (var service in slow)
        {
            var detail = $"{Milliseconds(service.AverageLatencyMs!.Value)} on average";
            section.Plain.Add($"{service.Name} — {detail}");
            section.Markdown.Add($"**{Escape(service.Name)}** — {detail}");
        }
        return section;
    }

    private static Section? CapacitySection(WeeklySummaryData data)
    {
        if (data.Capacity.Count == 0)
            return null;

        var filling = data.Capacity
            .Where(c => c.Forecast.State == ForecastState.Full
                        || c.Forecast is { State: ForecastState.Filling, DaysLeft: { } days } && days <= CapacityHorizonDays)
            .OrderBy(c => c.Forecast.SortKey)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var section = new Section("💾", "Storage", [], []);

        // Somebody with a NAS wants to hear that it is fine, once, in a line. Somebody
        // without one never sees the section at all — that is the empty Capacity above.
        if (filling.Count == 0)
        {
            const string fine = "Nothing is filling up.";
            section.Plain.Add(fine);
            section.Markdown.Add(fine);
            return section;
        }

        foreach (var line in filling.Take(MaxCapacity))
        {
            // The level converts as a reading and the week's movement as a change, so a
            // temperature that rose 5 °C says +9.0°F rather than +41.0°F.
            var (current, unit) = Units.Display(line.Current, line.Unit, data.Units);
            var parts = new List<string> { Format(current, unit, line.Decimals) };
            if (line.WeeklyChange is { } stored && Units.DisplayChange(stored, line.Unit, data.Units) is var (change, changeUnit)
                && Math.Abs(change) >= Math.Pow(10, -line.Decimals) / 2)
                parts.Add($"{Signed(change, changeUnit, line.Decimals)} a week");
            parts.Add(line.Forecast.Describe()
                      + (line.Forecast is { State: ForecastState.Filling, Confidence: ForecastConfidence.Low } ? " (rough guess)" : ""));

            var detail = string.Join(", ", parts);
            section.Plain.Add($"{line.Name} · {line.Label} — {detail}");
            section.Markdown.Add($"**{Escape(line.Name)}** · {Escape(line.Label)} — {detail}");
        }

        if (filling.Count > MaxCapacity)
        {
            var more = $"…and {filling.Count - MaxCapacity} more filling within {CapacityHorizonDays:0} days";
            section.Plain.Add(more);
            section.Markdown.Add(more);
        }

        return section;
    }

    private static Section? ExpirySection(WeeklySummaryData data)
    {
        var due = data.Expiries
            .Where(e => e.Overdue > 0 || e.DaysLeft <= ExpiryHorizonDays)
            .OrderByDescending(e => e.Overdue > 0)
            .ThenBy(e => e.DaysLeft)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (due.Count == 0)
            return null;

        var section = new Section("📅", "Coming up", [], []);
        foreach (var expiry in due.Take(MaxExpiries))
        {
            var detail = expiry.Overdue > 0
                ? $"{expiry.Overdue} overdue"
                : expiry.DaysLeft switch
                {
                    <= 0 => "expired",
                    < 1 => "today",
                    < 2 => "tomorrow",
                    var days => $"in {(int)Math.Floor(days)} days",
                };
            var what = expiry.What.Length > 0 ? $" {expiry.What}" : "";
            section.Plain.Add($"{expiry.Name}{what} — {detail}");
            section.Markdown.Add($"**{Escape(expiry.Name)}**{Escape(what)} — {detail}");
        }

        if (due.Count > MaxExpiries)
        {
            var more = $"…and {due.Count - MaxExpiries} more within {ExpiryHorizonDays:0} days";
            section.Plain.Add(more);
            section.Markdown.Add(more);
        }

        return section;
    }

    private static Section? SpeedSection(WeeklySummaryData data)
    {
        var tests = data.Speed.Where(s => s.Download is not null || s.Upload is not null).ToList();
        if (tests.Count == 0)
            return null;

        var section = new Section("🌐", "Internet", [], []);
        foreach (var speed in tests)
        {
            var parts = new List<string>();
            if (speed.Download is { } down)
            {
                var lowest = speed.DownloadLowest is { } low && low < down * 0.8 ? $" (lowest {Mbps(low)})" : "";
                parts.Add($"down {Mbps(down)}{lowest}");
            }
            if (speed.Upload is { } up)
                parts.Add($"up {Mbps(up)}");
            if (speed.Ping is { } ping)
                parts.Add($"ping {ping.ToString("0", Culture)} ms");

            var detail = "Averaged " + string.Join(", ", parts);
            // The name only earns its place when there is more than one to tell apart.
            section.Plain.Add(tests.Count > 1 ? $"{speed.Name} — {detail}" : detail);
            section.Markdown.Add(tests.Count > 1 ? $"**{Escape(speed.Name)}** — {detail}" : detail);
        }
        return section;
    }

    /// <summary>The most power sources named in the summary; the rest are in the total.</summary>
    public const int MaxPower = 3;

    /// <summary>
    /// One sentence for the week's electricity and, when there is more than one source, the
    /// few that cost the most. Nothing when nothing reports power, or nothing was used —
    /// "0.00 kWh" every Monday would be noise for an install without a smart plug.
    /// </summary>
    private static Section? PowerSection(WeeklySummaryData data)
    {
        if (data.Power is not { Kwh: > 0 } power)
            return null;

        var section = new Section("⚡", "Power", [], []);
        var line = $"{PowerShortcode.Kwh(power.Kwh)}, about {PowerTariff.FormatMoney(power.Cost, power.Currency)} this week";
        if (power.ProjectedMonth is { } projected)
            line += $" — on course for {PowerTariff.FormatMoney(projected, power.Currency)} this month";
        section.Plain.Add(line);
        section.Markdown.Add(line);

        var top = power.Top.Where(t => t.Cost > 0).Take(MaxPower).ToList();
        if (top.Count > 1)
        {
            var names = string.Join(", ", top.Select(t => $"{t.Name} {PowerTariff.FormatMoney(t.Cost, power.Currency)}"));
            section.Plain.Add("Most: " + names);
            section.Markdown.Add("Most: " + string.Join(", ", top.Select(t => $"{Escape(t.Name)} {PowerTariff.FormatMoney(t.Cost, power.Currency)}")));
        }
        return section;
    }

    /// <summary>The most backup lines listed by name.</summary>
    public const int MaxBackups = 6;

    /// <summary>
    /// A line of reassurance when every backup is on time and every restore test done — the
    /// thing somebody who set the list up wants to hear once a week — and otherwise only
    /// what needs doing: late first, then without a date, then restore tests overdue.
    /// </summary>
    private static Section? BackupsSection(WeeklySummaryData data)
    {
        if (data.Backups.Count == 0)
            return null;

        var section = new Section("🛟", "Backups", [], []);
        var wanting = data.Backups
            .Where(b => b.State != BackupState.Ok || b.DrillOverdue)
            .OrderBy(b => b.State switch { BackupState.Late => 0, BackupState.Missing => 1, BackupState.Never => 2, _ => 3 })
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (wanting.Count == 0)
        {
            var fine = data.Backups.Count == 1 ? "The one backup listed is on time." : $"All {data.Backups.Count} backups are on time.";
            section.Plain.Add(fine);
            section.Markdown.Add(fine);
            return section;
        }

        foreach (var backup in wanting.Take(MaxBackups))
        {
            var parts = new List<string>();
            switch (backup.State)
            {
                case BackupState.Late:
                    parts.Add(backup.LastSuccess is { } last ? $"late, last one {When(last, data)}" : "late");
                    break;
                case BackupState.Missing:
                    parts.Add("nothing can say when it was last backed up");
                    break;
                case BackupState.Never:
                    parts.Add("never backed up");
                    break;
            }
            if (backup.DrillOverdue)
            {
                parts.Add(backup.LastRestoreTest is { } tested
                    ? $"restore test due, last tried {TimeZoneInfo.ConvertTime(tested, data.Zone).ToString("d MMM", Culture)}"
                    : "never test-restored");
            }
            var detail = string.Join("; ", parts);
            section.Plain.Add($"{backup.Name} — {detail}");
            section.Markdown.Add($"**{Escape(backup.Name)}** — {detail}");
        }

        if (wanting.Count > MaxBackups)
        {
            var more = $"…and {wanting.Count - MaxBackups} more on the Backups page";
            section.Plain.Add(more);
            section.Markdown.Add(more);
        }
        return section;
    }

    private static Section? ChangesSection(WeeklySummaryData data)
    {
        var section = new Section("🛠", "What changed", [], []);

        void Line(string plain, string markdown)
        {
            section.Plain.Add(plain);
            section.Markdown.Add(markdown);
        }

        if (data.Added.Count > 0)
            Line($"Now watching {Names(data.Added)}", $"Now watching {Escape(Names(data.Added))}");
        if (data.Removed.Count > 0)
            Line($"No longer watching {Names(data.Removed)}", $"No longer watching {Escape(Names(data.Removed))}");
        if (data.UpdatedFrom is { Length: > 0 } from && data.UpdatedTo is { Length: > 0 } to)
            Line($"LabbyTwo updated from {from} to {to}", $"LabbyTwo updated from `{from}` to `{to}`");
        if (data.UpdateAvailable is { Length: > 0 } available)
            Line($"LabbyTwo {available} is available", $"LabbyTwo `{available}` is available");

        return section.Plain.Count > 0 ? section : null;
    }

    // ---------- phrasing ----------

    /// <summary>"14–20 Sep", or "28 Sep – 4 Oct" across a month end.</summary>
    private static string Range(WeeklySummaryData data)
    {
        var from = TimeZoneInfo.ConvertTime(data.From, data.Zone);
        // The window ends at the moment of sending; the last whole day in it is the one before
        // only when sending at midnight, which nobody schedules, so the end's own date is right.
        var to = TimeZoneInfo.ConvertTime(data.To, data.Zone);
        return from.Month == to.Month && from.Year == to.Year
            ? $"{from.Day}–{to.ToString("d MMM", Culture)}"
            : $"{from.ToString("d MMM", Culture)} – {to.ToString("d MMM", Culture)}";
    }

    /// <summary>
    /// "Tue 03:10" inside the week, with the date as well for anything that began before it —
    /// a weekday alone would be ambiguous about which Tuesday.
    /// </summary>
    private static string When(DateTimeOffset at, WeeklySummaryData data)
    {
        var local = TimeZoneInfo.ConvertTime(at, data.Zone);
        return at < data.From
            ? local.ToString("ddd d MMM HH:mm", Culture)
            : local.ToString("ddd HH:mm", Culture);
    }

    /// <summary>The part of an outage that fell inside the week, for the total. The line itself shows the whole outage.</summary>
    private static TimeSpan Clip(Outage outage, WeeklySummaryData data)
    {
        var start = outage.Start < data.From ? data.From : outage.Start;
        var end = outage.End ?? data.To;
        if (end > data.To)
            end = data.To;
        return end > start ? end - start : TimeSpan.Zero;
    }

    /// <summary>"under a minute", "18m", "1h 52m", "3d 4h" — the precision a person would use.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
            return "under a minute";
        if (span < TimeSpan.FromHours(1))
            return $"{(int)span.TotalMinutes}m";
        if (span < TimeSpan.FromHours(48))
            return span.Minutes == 0 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalHours}h {span.Minutes}m";
        return span.Hours == 0 ? $"{span.Days}d" : $"{span.Days}d {span.Hours}h";
    }

    /// <summary>
    /// Two decimals at most, and never rounded up to 100: 99.996% is a week with an outage in
    /// it, and printing "100%" beside that outage would read as a contradiction.
    /// </summary>
    public static string Percent(double value)
    {
        var floored = Math.Floor(value * 100) / 100;
        return floored.ToString("0.##", Culture) + "%";
    }

    private static string Milliseconds(double ms) =>
        ms >= 1000 ? $"{(ms / 1000).ToString("0.0", Culture)} s" : $"{ms.ToString("0", Culture)} ms";

    private static string Mbps(double value) =>
        value.ToString(value >= 100 ? "0" : "0.#", Culture) + " Mbps";

    private static string Format(double value, string unit, int decimals) =>
        value.ToString("F" + decimals, Culture) + unit;

    private static string Signed(double value, string unit, int decimals) =>
        (value >= 0 ? "+" : "−") + Format(Math.Abs(value), unit, decimals);

    /// <summary>"A, B and C", or "A, B, C, D, E and 3 more".</summary>
    private static string Names(IReadOnlyList<string> names)
    {
        var shown = names.Take(MaxNames).ToList();
        var extra = names.Count - shown.Count;
        if (extra > 0)
            return $"{string.Join(", ", shown)} and {extra} more";
        return shown.Count == 1 ? shown[0] : $"{string.Join(", ", shown[..^1])} and {shown[^1]}";
    }

    /// <summary>
    /// Service names are the user's own text, and "my_nas" or "*arr stack" would otherwise
    /// turn half a Discord message italic. Only the Markdown version needs this.
    /// </summary>
    public static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '[' or ']' or '>' or '#')
                builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }
}
