using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// The weekly summary's wording, for the weeks that actually happen: an ordinary one, a
/// quiet one, a bad one, and one on an install that has only some of the things a summary
/// can talk about. What matters is that it stays short and never contradicts itself — a
/// "100%" beside an outage, or an empty heading, is how a digest earns being muted.
/// </summary>
public sealed class WeeklySummaryTests
{
    /// <summary>UTC-5 in winter, UTC-4 from the second Sunday of March to the first of November.</summary>
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Eastern", TimeSpan.FromHours(-5), "Test Eastern", "Test Eastern", "Test Eastern Daylight",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        ]);

    /// <summary>Monday 14 September to Monday 21 September 2026, 09:00 each end, in daylight time.</summary>
    private static readonly DateTimeOffset From = new(2026, 9, 14, 9, 0, 0, TimeSpan.FromHours(-4));
    private static readonly DateTimeOffset To = From.AddDays(7);

    private static DateTimeOffset Day(int offsetDays, int hour, int minute = 0) =>
        new(2026, 9, 14 + offsetDays, hour, minute, 0, TimeSpan.FromHours(-4));

    private static ServiceWeek Up(string name, double? latency = null) =>
        new(name, 100, []) { AverageLatencyMs = latency };

    private static ServiceWeek Down(string name, params Outage[] outages)
    {
        var down = outages.Aggregate(TimeSpan.Zero, (sum, o) => sum + o.Duration);
        return new ServiceWeek(name, 100 - down.TotalSeconds / (To - From).TotalSeconds * 100, outages);
    }

    private static Outage Blip(DateTimeOffset start, TimeSpan length, string message = "timed out") =>
        new(start, start + length, length, message);

    private static CapacityForecast Filling(double current, double days, double ratePerDay,
        ForecastConfidence confidence = ForecastConfidence.High) =>
        new(ForecastState.Filling, current, days, ratePerDay, confidence, 0.9, TimeSpan.FromDays(14), null);

    private static CapacityForecast Flat(double current) =>
        new(ForecastState.NotFilling, current, null, 0, ForecastConfidence.High, 0.1, TimeSpan.FromDays(14), null);

    private static WeeklySummaryData Week(Func<WeeklySummaryData, WeeklySummaryData>? shape = null)
    {
        var data = new WeeklySummaryData { From = From, To = To, Zone = Eastern };
        return shape is null ? data : shape(data);
    }

    [Fact]
    public void ATypicalWeek()
    {
        var digest = WeeklySummary.Build(Week(d => d with
        {
            Services =
            [
                Up("Pi-hole", 6),
                Down("NAS", Blip(Day(1, 3, 10), TimeSpan.FromMinutes(112), "Connection refused")),
                Down("Plex", Blip(Day(3, 21, 4), TimeSpan.FromMinutes(18)), Blip(Day(5, 8, 0), TimeSpan.FromMinutes(4))),
                Up("Nextcloud", 842),
            ],
            Capacity =
            [
                new CapacityLine("NAS", "Volume 1", 81, 1.2, "%", 0, Filling(81, 49, 0.17)),
                new CapacityLine("Proxmox", "Local disk", 40, 0, "%", 0, Flat(40)),
            ],
            Expiries =
            [
                new ExpiryLine("cloud.example.com", "certificate", 12.4),
                new ExpiryLine("Tailnet", "node key", 160),
            ],
            Speed = [new SpeedWeek("Speedtest", 412.3, 180, 38.4, 14.2)],
            Added = ["Immich"],
            UpdatedFrom = "v1.8.0",
            UpdatedTo = "v1.9.0",
        }));

        Assert.Equal("Weekly summary · 14–21 Sep", digest.Title);

        var expected = """
            99.66% uptime across 4 services. 3 outages, 2h 14m down in all.

            🔻 Outages
            • NAS — 1h 52m, Tue 03:10
            • Plex — 18m, Thu 21:04
            • Plex — 4m, Sat 08:00

            ⚠️ Least reliable
            Plex — dropped 2 times, 99.78% up

            🐢 Slowest to answer
            Nextcloud — 842 ms on average

            💾 Storage
            NAS · Volume 1 — 81%, +1% a week, full in about 7 weeks

            📅 Coming up
            cloud.example.com certificate — in 12 days

            🌐 Internet
            Averaged down 412 Mbps (lowest 180 Mbps), up 38.4 Mbps, ping 14 ms

            🛠 What changed
            • Now watching Immich
            • LabbyTwo updated from v1.8.0 to v1.9.0
            """.ReplaceLineEndings("\n");

        Assert.Equal(expected, digest.Text);
    }

    [Fact]
    public void AQuietWeekIsOneLine()
    {
        var digest = WeeklySummary.Build(Week(d => d with
        {
            Services = [Up("Pi-hole", 5), Up("NAS", 20), Up("Plex", 40)],
        }));

        Assert.Equal("Everything stayed up: 100% across 3 services. No outages this week 🎉", digest.Text);
        Assert.Equal(digest.Text, digest.Markdown);
    }

    [Fact]
    public void ADiskThatIsFineIsOneReassuringLine()
    {
        var digest = WeeklySummary.Build(Week(d => d with
        {
            Services = [Up("NAS")],
            Capacity = [new CapacityLine("NAS", "Volume 1", 40, 0, "%", 0, Flat(40))],
        }));

        Assert.Contains("💾 Storage\nNothing is filling up.", digest.Text);
    }

    [Fact]
    public void ABadWeekIsCappedAndStillSaysWhatIsDown()
    {
        var outages = Enumerable.Range(0, 12)
            .Select(i => Blip(Day(i % 6, 1 + i), TimeSpan.FromMinutes(10 + i)))
            .ToArray();

        var digest = WeeklySummary.Build(Week(d => d with
        {
            Services =
            [
                Down("Plex", outages[..6]),
                Down("Sonarr", outages[6..]),
                new ServiceWeek("NAS", 60, [new Outage(Day(-2, 22), null, To - Day(-2, 22), "No route to host")]),
                Up("Pi-hole"),
            ],
        }));

        var lines = digest.Text.Split('\n');

        // The one still down leads the headline and the list, with when it began — before
        // the week did, so with a date rather than a bare weekday.
        Assert.Contains("13 outages", lines[0]);
        Assert.EndsWith("NAS is still down.", lines[0]);
        Assert.Equal("• NAS — still down, since Sat 12 Sep 22:00 (8d 11h)", lines[3]);

        // Five named, the rest counted — never a wall of twelve.
        var listed = lines.SkipWhile(l => l != "🔻 Outages").Skip(1).TakeWhile(l => l.Length > 0).ToList();
        Assert.Equal(WeeklySummary.MaxOutages + 1, listed.Count);
        Assert.Equal("• …and 8 shorter, across 2 services", listed[^1]);

        // The flakiest are the ones that dropped most, not merely the ones that dropped.
        // Equal counts, so the one that lost more of the week comes first.
        Assert.Contains("⚠️ Least reliable\n• Sonarr — dropped 6 times, 98.89% up\n• Plex — dropped 6 times", digest.Text);
        Assert.DoesNotContain("NAS — dropped", digest.Text);

        Assert.Contains("**NAS is still down.**", digest.Markdown);
    }

    [Fact]
    public void MissingSectionsAreLeftOutRatherThanPrintedEmpty()
    {
        var digest = WeeklySummary.Build(Week(d => d with
        {
            Services = [Down("NAS", Blip(Day(1, 3), TimeSpan.FromMinutes(5)))],
        }));

        Assert.Contains("🔻 Outages", digest.Text);
        foreach (var heading in new[] { "Least reliable", "Slowest", "Storage", "Coming up", "Internet", "What changed" })
            Assert.DoesNotContain(heading, digest.Text);
    }

    [Fact]
    public void NothingMonitoredStillSaysSomething()
    {
        var digest = WeeklySummary.Build(Week());
        Assert.Equal("Nothing was monitored this week.", digest.Text);
    }

    [Fact]
    public void FastServicesAreNotListedAsSlow()
    {
        var digest = WeeklySummary.Build(Week(d => d with { Services = [Up("Pi-hole", 4), Up("NAS", 99)] }));
        Assert.DoesNotContain("Slowest", digest.Text);
    }

    [Fact]
    public void FarOffExpiriesAndDistantDisksAreNotNews()
    {
        var digest = WeeklySummary.Build(Week(d => d with
        {
            Services = [Up("NAS")],
            Capacity = [new CapacityLine("NAS", "Volume 1", 30, 0.1, "%", 0, Filling(30, 400, 0.02))],
            Expiries = [new ExpiryLine("cloud.example.com", "certificate", 80)],
        }));

        Assert.DoesNotContain("Coming up", digest.Text);
        Assert.Contains("Nothing is filling up.", digest.Text);
    }

    [Fact]
    public void OverdueRenewalsComeFirst()
    {
        var digest = WeeklySummary.Build(Week(d => d with
        {
            Services = [Up("NAS")],
            Expiries =
            [
                new ExpiryLine("cloud.example.com", "certificate", 3),
                new ExpiryLine("Renewals", "", 0, Overdue: 2),
                new ExpiryLine("Renewals", "next renewal", 0.5),
            ],
        }));

        Assert.Contains("📅 Coming up\n• Renewals — 2 overdue\n• Renewals next renewal — today\n• cloud.example.com certificate — in 3 days", digest.Text);
    }

    [Fact]
    public void FreeSpaceFallsWithAMinusSign()
    {
        var digest = WeeklySummary.Build(Week(d => d with
        {
            Services = [Up("qBittorrent")],
            Capacity =
            [
                new CapacityLine("qBittorrent", "Free space", 120.5, -42.2, " GB", 1,
                    new CapacityForecast(ForecastState.Filling, 120.5, 20, -6, ForecastConfidence.Low, 0.4, TimeSpan.FromDays(10), null)),
            ],
        }));

        Assert.Contains("qBittorrent · Free space — 120.5 GB, −42.2 GB a week, full in about 3 weeks (rough guess)", digest.Text);
    }

    [Fact]
    public void MarkdownEscapesNamesButPlainTextDoesNot()
    {
        var digest = WeeklySummary.Build(Week(d => d with
        {
            Services = [Down("my_nas *main*", Blip(Day(1, 3), TimeSpan.FromMinutes(5)))],
        }));

        Assert.Contains("my_nas *main* — 5m", digest.Text);
        Assert.Contains(@"**my\_nas \*main\*** — 5m", digest.Markdown);
        Assert.Contains("🔻 **Outages**", digest.Markdown);
    }

    [Fact]
    public void ARangeAcrossAMonthEndNamesBothMonths()
    {
        var from = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.FromHours(-4));
        var digest = WeeklySummary.Build(new WeeklySummaryData { From = from, To = from.AddDays(7), Zone = Eastern });
        Assert.Equal("Weekly summary · 28 Sep – 5 Oct", digest.Title);
    }

    [Theory]
    [InlineData(99.999, "99.99%")]
    [InlineData(100, "100%")]
    [InlineData(97.5, "97.5%")]
    [InlineData(0, "0%")]
    public void APercentageIsNeverRoundedUpToPerfect(double value, string expected) =>
        Assert.Equal(expected, WeeklySummary.Percent(value));

    [Theory]
    [InlineData(30, "under a minute")]
    [InlineData(18 * 60, "18m")]
    [InlineData(112 * 60, "1h 52m")]
    [InlineData(3 * 3600, "3h")]
    [InlineData(3 * 86400 + 4 * 3600 + 59, "3d 4h")]
    public void DurationsUseThePrecisionAPersonWould(int seconds, string expected) =>
        Assert.Equal(expected, WeeklySummary.Duration(TimeSpan.FromSeconds(seconds)));

    // ---------- the uptime arithmetic ----------

    [Fact]
    public void AnOutageInsideTheWeekIsMeasuredAndCounted()
    {
        var week = ServiceWeek.Measure("NAS",
            new StatusChange(From.AddDays(-30), true),
            [new StatusChange(Day(1, 3), false, "refused"), new StatusChange(Day(1, 5), true)],
            From, To);

        var outage = Assert.Single(week.Outages);
        Assert.Equal(TimeSpan.FromHours(2), outage.Duration);
        Assert.Equal("refused", outage.Message);
        Assert.Equal(100 - 2.0 / 168 * 100, week.UptimePercent!.Value, 6);
        Assert.False(week.DownNow);
    }

    [Fact]
    public void AnOutageCarriedInFromLastWeekStartsWhenItReallyStarted()
    {
        var began = From.AddHours(-10);
        var week = ServiceWeek.Measure("NAS",
            new StatusChange(began, false, "unreachable"),
            [new StatusChange(From.AddHours(2), true)],
            From, To);

        var outage = Assert.Single(week.Outages);
        Assert.Equal(began, outage.Start);
        Assert.Equal(TimeSpan.FromHours(12), outage.Duration);

        // Only the two hours inside the week count against it.
        Assert.Equal(100 - 2.0 / 168 * 100, week.UptimePercent!.Value, 6);
    }

    [Fact]
    public void AServiceStillDownHasAnOngoingOutage()
    {
        var week = ServiceWeek.Measure("NAS",
            new StatusChange(From.AddDays(-1), true),
            [new StatusChange(To.AddHours(-3), false, "timed out")],
            From, To);

        Assert.True(week.DownNow);
        Assert.Null(week.Outages[0].End);
        Assert.Equal(TimeSpan.FromHours(3), week.Outages[0].Duration);
    }

    [Fact]
    public void AServiceAddedMidweekIsMeasuredOnlySinceItStarted()
    {
        var week = ServiceWeek.Measure("Immich", null, [new StatusChange(Day(4, 9), true)], From, To);

        Assert.Equal(100, week.UptimePercent);
        Assert.Empty(week.Outages);
    }

    [Fact]
    public void AServiceWithNoHistoryIsNotMeasuredAtAll()
    {
        var week = ServiceWeek.Measure("New", null, [], From, To);

        Assert.Null(week.UptimePercent);

        // And does not count towards "across N services".
        var digest = WeeklySummary.Build(Week(d => d with { Services = [week, Up("NAS")] }));
        Assert.Contains("across 1 service.", digest.Text);
    }

    [Fact]
    public void AStableServiceWithNoEventsThisWeekIsAHundredPercent()
    {
        var week = ServiceWeek.Measure("NAS", new StatusChange(From.AddDays(-90), true), [], From, To);
        Assert.Equal(100, week.UptimePercent);
    }
}
