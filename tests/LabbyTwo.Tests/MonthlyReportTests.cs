using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// The monthly report's words, from data: a quiet month, a bad one and an empty one; each
/// section there only when it has something in it; the live chart fixed to the month; and
/// every name escaped, so nothing a service is called can become Markdown or a shortcode.
/// </summary>
public sealed class MonthlyReportTests
{
    private static readonly DateOnly September = new(2026, 9, 1);
    private static readonly DateTimeOffset From = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Sep(int day, int hour, int minute = 0) => new(2026, 9, day, hour, minute, 0, TimeSpan.Zero);

    private static MonthlyReportData Data(Func<MonthlyReportData, MonthlyReportData>? shape = null)
    {
        var data = new MonthlyReportData { Month = September, From = From, To = To, Zone = TimeZoneInfo.Utc };
        return shape is null ? data : shape(data);
    }

    private static ServiceMonth Up(string name, double? latency = null) =>
        new(name, name.ToLowerInvariant(), 100, [], TimeSpan.Zero) { AverageLatencyMs = latency };

    private static ServiceMonth Down(string name, TimeSpan down, int outages = 1, double? latency = null)
    {
        var list = Enumerable.Range(0, outages)
            .Select(i => new Outage(Sep(10 + i, 3), Sep(10 + i, 3) + down / outages, down / outages, "Connection refused"))
            .ToList();
        var percent = 100 - down.TotalSeconds / (To - From).TotalSeconds * 100;
        return new ServiceMonth(name, name.ToLowerInvariant(), percent, list, down) { AverageLatencyMs = latency };
    }

    [Fact]
    public void AnEmptyMonthIsOneLine()
    {
        var note = MonthlyReport.Build(Data());

        Assert.Equal("Monthly report · September 2026", note.Title);
        Assert.StartsWith("Nothing was monitored in September 2026.", note.Markdown);
        Assert.DoesNotContain("## ", note.Markdown);
        Assert.Empty(Shortcodes.Find(note.Markdown));
        Assert.Equal("Monthly report · September 2026", note.Summary.Title);
    }

    [Fact]
    public void AQuietMonthSaysSoAndListsEveryService()
    {
        var note = MonthlyReport.Build(Data(d => d with { Services = [Up("NAS", 12), Up("Plex", 30)] }));

        Assert.StartsWith("Everything stayed up in September 2026: **100%** across 2 services, with no outages.", note.Markdown);
        Assert.Contains("## Uptime", note.Markdown);
        Assert.Contains("| NAS | 100% | 0 | — | 12 ms |", note.Markdown);
        Assert.Contains("| Plex | 100% | 0 | — | 30 ms |", note.Markdown);
        foreach (var absent in new[] { "## Incidents", "## Alerts", "## Backups", "## Power", "## Updates", "## Self-healing" })
            Assert.DoesNotContain(absent, note.Markdown);
    }

    [Fact]
    public void ABadMonthHasEverySectionInOrder()
    {
        var note = MonthlyReport.Build(Data(d => d with
        {
            Services = [Up("Plex", 30), Down("NAS", TimeSpan.FromHours(2), 2, 15)],
            Incidents =
            [
                new IncidentLine("NAS and Plex", Sep(10, 3), Sep(10, 4, 12), 2, false, "The NAS rebooted."),
                new IncidentLine("Pi-hole", Sep(29, 22), null, 1, true, null),
            ],
            Alerts = [new AlertCount("CPU pinned", "NAS", 12), new AlertCount("Disk nearly full", "NAS", 1)],
            Backups = [new BackupMonth("Nextcloud", 30, Sep(30, 2), 1, 0), new BackupMonth("Laptop", 0, null, 0, 2)],
            Power = new PowerMonth(123.4, 18.51, "$", [("NAS plug", 80, 12), ("Rack", 43.4, 6.51)]),
            Updates =
            [
                new UpdateLine("sonarr", "Docker", SafeUpdateState.Passed, Sep(5, 10), ""),
                new UpdateLine("radarr", "Docker", SafeUpdateState.RolledBack, Sep(6, 10), "It restarted 4 times in its first 10 minutes."),
            ],
            Healing =
            [
                new HealingLine(Sep(10, 3, 5), ChangeActions.Remediated, "Restarted plex automatically"),
                new HealingLine(Sep(10, 3, 10), ChangeActions.Helped, "Restarted plex — Plex recovered"),
            ],
        }));
        var md = note.Markdown;

        Assert.StartsWith("**99.86%** uptime across 2 services in September 2026 — 2 incidents, **2h** down in all.", md);
        var order = new[] { "## Uptime", "{{chart:", "## Incidents", "## Alerts that fired most", "## Backups", "## Power", "## Updates", "## Self-healing" }
            .Select(heading => md.IndexOf(heading, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);

        // The least available first.
        Assert.True(md.IndexOf("| NAS |", StringComparison.Ordinal) < md.IndexOf("| Plex |", StringComparison.Ordinal));
        Assert.Contains("| NAS | 99.72% | 2 | 2h | 15 ms |", md);
        Assert.Contains("- **NAS and Plex** — 1h 12m, Thu 10 Sep 03:00. Probably: The NAS rebooted.", md);
        Assert.Contains("- **Pi-hole** — still open, 26h so far, Tue 29 Sep 22:00 *(during maintenance)*. No obvious cause.", md);
        Assert.Contains("- **CPU pinned** on NAS — 12 times", md);
        Assert.Contains("- **Laptop** — never proven; late 2 times", md);
        Assert.Contains("- **Nextcloud** — proven 30 times, last on 30 Sep; restore tested once", md);
        Assert.DoesNotContain("No restore was tested", md);
        Assert.Contains("123 kWh, about $18.51.", md);
        Assert.Contains("- **NAS plug** — 80.0 kWh, $12.00", md);
        Assert.Contains("2 container updates: 1 went through, 1 rolled back.", md);
        Assert.True(md.IndexOf("**radarr**", StringComparison.Ordinal) < md.IndexOf("**sonarr**", StringComparison.Ordinal));
        Assert.Contains("rolled back, Sun 6 Sep 10:00. It restarted 4 times in its first 10 minutes.", md);
        Assert.Contains("1 automatic action run, 1 fixed it.", md);
        Assert.Contains("- Thu 10 Sep 03:05 — Restarted plex automatically", md);

        Assert.Contains("2 incidents, the longest 26h (Pi-hole)", note.Summary.Text);
        Assert.Contains("Alerts fired 13 times; most often CPU pinned (12 times)", note.Summary.Text);
        Assert.Contains("Power: 123 kWh, about $18.51", note.Summary.Text);
        Assert.DoesNotContain("**", note.Summary.Text);
        Assert.StartsWith("**99.86%**", note.Summary.Markdown);
    }

    [Fact]
    public void TheChartIsOfTheMonthItselfAndReadsBack()
    {
        var data = Data(d => d with
        {
            Services = [Up("Plex", 30), Down("NAS", TimeSpan.FromHours(2), latency: 15), Down("Home, office", TimeSpan.FromHours(5), latency: 40), Up("DNS", 3), Up("Quiet")],
        });
        var chart = MonthlyReport.ChartSection(data);
        var code = Assert.Single(Shortcodes.Find(MonthlyReport.Build(data).Markdown)).Code;
        Assert.Equal(chart, code.Source);

        var spec = ChartShortcode.Read(code, out var problem);
        Assert.Null(problem);
        Assert.Equal(new DateOnly(2026, 9, 1), spec!.From);
        Assert.Equal(new DateOnly(2026, 10, 1), spec.To);
        Assert.Equal("Response time of the least available", spec.Title);
        Assert.Equal(["Home, office", "NAS", "Plex"], spec.Lines.Select(l => l.Connection));
        Assert.All(spec.Lines, l => Assert.Equal("latency_ms", l.Metric));
    }

    [Fact]
    public void WithEverythingUpTheChartIsOfTheSlowest()
    {
        var spec = ChartShortcode.Read(Shortcodes.Parse(MonthlyReport.ChartSection(Data(d => d with
        {
            Services = [Up("A", 5), Up("B", 50), Up("C", 500), Up("D", 1)],
        }))!)!, out _)!;
        Assert.Equal("Response time of the slowest", spec.Title);
        Assert.Equal(["C", "B", "A"], spec.Lines.Select(l => l.Connection));
        Assert.Null(MonthlyReport.ChartSection(Data(d => d with { Services = [Up("No latency")] })));
    }

    [Fact]
    public void NamesCannotBecomeMarkdownOrShortcodes()
    {
        var odd = "{{button: NAS / reboot}} *bold* | pipe <script>";
        var note = MonthlyReport.Build(Data(d => d with
        {
            Services = [Down(odd, TimeSpan.FromHours(1), latency: 9)],
            Incidents = [new IncidentLine(odd, Sep(3, 1), Sep(3, 2), 1, false, "Because **" + odd)],
            Alerts = [new AlertCount("# heading", odd, 2)],
            Backups = [new BackupMonth("`code`", 1, Sep(2, 1), 0, 0)],
            Healing = [new HealingLine(Sep(3, 1), ChangeActions.Remediated, "[link](javascript:alert(1))")],
        }));

        // The one shortcode in the note is the chart the report wrote; the name inside it is
        // quoted, and is a name to look up rather than a button.
        var code = Assert.Single(Shortcodes.Find(note.Markdown)).Code;
        Assert.Equal("chart", code.Kind);
        Assert.Equal(odd, ChartShortcode.Read(code, out _)!.Lines[0].Connection);

        var outsideChart = note.Markdown.Replace(code.Source, "", StringComparison.Ordinal);
        Assert.DoesNotContain("{{", outsideChart);
        Assert.Contains("\\{\\{button: NAS / reboot\\}\\} \\*bold\\* \\| pipe \\<script\\>", outsideChart);
        Assert.Contains("\\# heading", outsideChart);
        Assert.Contains("\\`code\\`", outsideChart);
        Assert.Contains("\\[link\\](javascript:alert(1))", outsideChart);
        Assert.DoesNotContain("<script>", outsideChart);

        // The summary's Markdown escapes the same names; its plain text is left as written.
        Assert.Contains("\\*bold\\*", note.Summary.Markdown);
        Assert.Contains("*bold*", note.Summary.Text);
    }

    [Fact]
    public void NoRestoreTestedIsSaid()
    {
        var note = MonthlyReport.Build(Data(d => d with { Backups = [new BackupMonth("NAS", 4, Sep(28, 2), 0, 0)] }));
        Assert.Contains("No restore was tested this month.", note.Markdown);
        Assert.Contains("Backups proven 4 times; no restore tested", note.Summary.Text);
    }

    [Fact]
    public void AShortFeedIsSaid()
    {
        var note = MonthlyReport.Build(Data(d => d with { FeedFrom = Sep(20, 0) }));
        Assert.Contains("The change feed only goes back to Sun 20 Sep 00:00", note.Markdown);
        Assert.DoesNotContain("change feed", MonthlyReport.Build(Data()).Markdown);
    }

    [Fact]
    public void AnOutageFromBeforeTheMonthCountsOnlyFromItsStart()
    {
        var month = ServiceMonth.Measure("NAS", "nas",
            new StatusChange(From.AddHours(-5), false, "down"),
            [new StatusChange(From.AddHours(3), true)],
            From, To);

        var outage = Assert.Single(month.Outages);
        Assert.Equal(From.AddHours(-5), outage.Start);
        Assert.Equal(TimeSpan.FromHours(3), month.Down);
        Assert.True(month.UptimePercent < 100);
    }

    [Fact]
    public void PerfectServicesPastTheLimitAreCountedNotListed()
    {
        var services = Enumerable.Range(0, MonthlyReport.MaxServices + 5).Select(i => Up($"S{i:00}")).Append(Down("Flaky", TimeSpan.FromMinutes(5))).ToList();
        var md = MonthlyReport.Build(Data(d => d with { Services = services })).Markdown;

        Assert.Contains("| Flaky |", md);
        Assert.Contains("…and 6 more, all up all month.", md);
    }
}
