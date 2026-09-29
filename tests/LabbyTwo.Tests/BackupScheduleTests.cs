using System.Net;
using System.Text;
using LabbyTwo.Core;
using LabbyTwo.Providers;

namespace LabbyTwo.Tests;

/// <summary>
/// The arithmetic of the Backups page, with no database: when a backup is due in a zone
/// whose clocks change, what "late", "missing" and "never" mean, how a reading becomes a
/// date, when restore drills fall due, that each notice goes once, what {{backups}} accepts,
/// and how the weekly summary words it.
/// </summary>
public sealed class BackupScheduleTests
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

    /// <summary>A wall-clock time in the test zone, as an instant. In 2026 the clocks go forward on 8 March and back on 1 November.</summary>
    private static DateTimeOffset Local(int month, int day, int hour, int minute = 0, int year = 2026) =>
        WeeklySchedule.At(new DateOnly(year, month, day), new TimeOnly(hour, minute), Eastern);

    private static BackupItem Daily(DateTimeOffset? remembered = null) => new()
    {
        Name = "Photos",
        Frequency = BackupFrequency.Daily,
        LastSuccess = remembered,
        CreatedAt = Local(1, 1, 12),
        Drill = DrillCadence.Off,
    };

    // ---------- when it is due ----------

    [Fact]
    public void ANightlyBackupIsDueAtTheSameWallClockTimeWhicheverNightTheClocksChange()
    {
        // Saturday 22:00 EST to Sunday 22:00 EDT is 23 hours; the backup is still due at 22:00.
        var spring = BackupSchedule.Next(Local(3, 7, 22), BackupFrequency.Daily, Eastern);
        Assert.Equal(Local(3, 8, 22), spring);
        Assert.Equal(TimeSpan.FromHours(23), spring - Local(3, 7, 22));

        // And 25 hours across the night they go back.
        var autumn = BackupSchedule.Next(Local(10, 31, 22), BackupFrequency.Daily, Eastern);
        Assert.Equal(Local(11, 1, 22), autumn);
        Assert.Equal(TimeSpan.FromHours(25), autumn - Local(10, 31, 22));
    }

    [Fact]
    public void ABackupAtATimeTheClocksSkipIsDueJustAfterTheJump()
    {
        // 02:30 on 8 March does not exist; it lands at 03:30 EDT, the same distance past the change.
        var due = BackupSchedule.Next(Local(3, 7, 2, 30), BackupFrequency.Daily, Eastern);
        Assert.Equal(DateTimeOffset.Parse("2026-03-08T03:30:00-04:00"), due);
    }

    [Fact]
    public void HourlyIsAFixedHourEvenAcrossTheChange()
    {
        var before = DateTimeOffset.Parse("2026-11-01T01:30:00-04:00");
        Assert.Equal(before.AddHours(1), BackupSchedule.Next(before, BackupFrequency.Hourly, Eastern));
    }

    [Fact]
    public void WeeklyAndMonthlyAreCalendarSteps()
    {
        Assert.Equal(Local(3, 10, 3), BackupSchedule.Next(Local(3, 3, 3), BackupFrequency.Weekly, Eastern));
        // From the 31st of January to the last day of February, not the 3rd of March.
        Assert.Equal(Local(2, 28, 3), BackupSchedule.Next(Local(1, 31, 3), BackupFrequency.Monthly, Eastern));
    }

    [Fact]
    public void GraceIsAddedAndTheDefaultDependsOnTheFrequency()
    {
        Assert.Equal(6, Daily().EffectiveGraceHours);
        Assert.Equal(1, (Daily() with { Frequency = BackupFrequency.Hourly }).EffectiveGraceHours);
        Assert.Equal(24, (Daily() with { Frequency = BackupFrequency.Weekly }).EffectiveGraceHours);
        Assert.Equal(0, (Daily() with { GraceHours = 0 }).EffectiveGraceHours);

        Assert.Equal(Local(9, 29, 8), BackupSchedule.DueBy(Local(9, 28, 2), BackupFrequency.Daily, 6, Eastern));
    }

    // ---------- late, missing, never ----------

    [Fact]
    public void LateIsStrictlyPastTheDueTime()
    {
        var last = Local(9, 28, 2);
        var reading = new BackupReading(last, null, "PBS");
        var due = Local(9, 29, 8);

        var onTheStroke = BackupSchedule.Judge(Daily(), reading, due, Eastern);
        Assert.Equal(BackupState.Ok, onTheStroke.State);
        Assert.Equal(due, onTheStroke.DueBy);

        var aSecondAfter = BackupSchedule.Judge(Daily(), reading, due.AddSeconds(1), Eastern);
        Assert.Equal(BackupState.Late, aSecondAfter.State);
        Assert.Equal(last, aSecondAfter.LastSuccess);
    }

    [Fact]
    public void NoDateIsNeverWhenTheSourceAnswersAndMissingWhenItCannot()
    {
        var now = Local(9, 29, 12);
        Assert.Equal(BackupState.Never, BackupSchedule.Judge(Daily(), BackupReading.Manual, now, Eastern).State);

        var missing = BackupSchedule.Judge(Daily(), new BackupReading(null, "PBS is down", "PBS"), now, Eastern);
        Assert.Equal(BackupState.Missing, missing.State);
        Assert.Equal("PBS is down", missing.Problem);
        Assert.Null(missing.DueBy);
    }

    [Fact]
    public void ARememberedDateStillCountsWhileTheSourceIsAwayAndTheNewerOfTheTwoWins()
    {
        var now = Local(9, 29, 12);
        var away = BackupSchedule.Judge(Daily(remembered: Local(9, 29, 2)), new BackupReading(null, "PBS is down", "PBS"), now, Eastern);
        Assert.Equal(BackupState.Ok, away.State);
        Assert.Equal("PBS is down", away.Problem);

        // The source proving something older than what was remembered does not make it late.
        var older = BackupSchedule.Judge(Daily(remembered: Local(9, 29, 2)), new BackupReading(Local(9, 20, 2), null, "PBS"), now, Eastern);
        Assert.Equal(Local(9, 29, 2), older.LastSuccess);

        // Nor does a stale memory, when the source proves last night.
        var newer = BackupSchedule.Judge(Daily(remembered: Local(9, 1, 2)), new BackupReading(Local(9, 29, 2), null, "PBS"), now, Eastern);
        Assert.Equal(BackupState.Ok, newer.State);
    }

    [Fact]
    public void ARemovedSourceWithAnOldRememberedDateIsLateNotMissing()
    {
        var status = BackupSchedule.Judge(Daily(remembered: Local(9, 1, 2)), new BackupReading(null, "the connection was deleted", "x"),
            Local(9, 29, 12), Eastern);
        Assert.Equal(BackupState.Late, status.State);
    }

    // ---------- readings ----------

    [Theory]
    [InlineData("hours_since_backup", " h", 5, 5 * 60)]
    [InlineData("hours_since_ping:restic", " h", 1.5, 90)]
    [InlineData("result_age", " min", 30, 30)]
    [InlineData("backup_age_minutes", "", 45, 45)]
    [InlineData("days_since_backup", "", 2, 2 * 24 * 60)]
    [InlineData("last_run_age", " d", 1, 24 * 60)]
    [InlineData("seconds_since_success", "", 600, 10)]
    public void AnAgeIsMeasuredBackFromWhenItWasRead(string metric, string unit, double value, double minutesAgo)
    {
        var read = Local(9, 29, 12);
        Assert.Equal(read.AddMinutes(-minutesAgo), BackupSchedule.FromMetric(metric, unit, value, read));
    }

    [Fact]
    public void ATimestampIsTakenAsItIsInSecondsOrMilliseconds()
    {
        var at = DateTimeOffset.Parse("2026-09-29T06:00:00Z");
        var read = Local(9, 29, 12);
        Assert.Equal(at, BackupSchedule.FromMetric("last_success_timestamp_seconds", "", at.ToUnixTimeSeconds(), read));
        Assert.Equal(at, BackupSchedule.FromMetric("last_success", "", at.ToUnixTimeMilliseconds(), read));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ANonsenseReadingProvesNothing(double value) =>
        Assert.Null(BackupSchedule.FromMetric("hours_since_backup", " h", value, Local(9, 29, 12)));

    [Fact]
    public void ReadingsThatLookLikeProofAreOfferedFirst()
    {
        Assert.True(BackupSchedule.LooksLikeProof("hours_since_backup", " h"));
        Assert.True(BackupSchedule.LooksLikeProof("backup_last_success_timestamp", ""));
        Assert.True(BackupSchedule.LooksLikeProof("result_age_hours", " h"));
        Assert.False(BackupSchedule.LooksLikeProof("disk_percent", "%"));
    }

    // ---------- restore drills ----------

    [Fact]
    public void ADrillIsDueOneCadenceAfterTheLastTestOrAfterTheItemWasAdded()
    {
        var item = Daily() with { Drill = DrillCadence.Quarterly, CreatedAt = Local(1, 15, 9) };
        Assert.Equal(Local(4, 15, 9), BackupSchedule.DrillDue(item, Eastern));

        var tested = item with { Drill = DrillCadence.Monthly, LastRestoreTest = Local(8, 31, 20) };
        Assert.Equal(Local(9, 30, 20), BackupSchedule.DrillDue(tested, Eastern));

        Assert.Null(BackupSchedule.DrillDue(item with { Drill = DrillCadence.Off }, Eastern));

        var row = BackupSchedule.Row(tested, BackupReading.Manual, Local(9, 30, 20, 1), Eastern);
        Assert.True(row.DrillOverdue);
        Assert.True(row.NeedsAttention);
        Assert.False(BackupSchedule.Row(tested, BackupReading.Manual, Local(9, 30, 20), Eastern).DrillOverdue);
    }

    // ---------- notices go once ----------

    private static BackupRow LateRow(BackupItem item) =>
        BackupSchedule.Row(item with { LastSuccess = Local(9, 1, 2) }, BackupReading.Manual, Local(9, 29, 12), Eastern);

    [Fact]
    public void ALateBackupIsAnnouncedOnceAndItsRecoveryOnce()
    {
        var first = BackupNotices.For(LateRow(Daily()));
        Assert.True(first.SendLate);
        var after = first.Apply(Daily(), null, lateSent: true, drillSent: false);
        Assert.True(after.LateAnnounced);

        // The next sweep, still late: nothing.
        Assert.False(BackupNotices.For(LateRow(after)).Any);

        // Back on time: the recovery, and then nothing.
        var ok = BackupSchedule.Row(after with { LastSuccess = Local(9, 29, 2) }, BackupReading.Manual, Local(9, 29, 12), Eastern);
        var recovered = BackupNotices.For(ok);
        Assert.True(recovered.SendRecovered);
        var cleared = recovered.Apply(ok.Item, null, false, false);
        Assert.False(cleared.LateAnnounced);
        Assert.False(BackupNotices.For(ok with { Item = cleared }).Any);
    }

    [Fact]
    public void AHeldLateAlertIsTriedAgainAndOneThatIsSwitchedOffNever()
    {
        var held = BackupNotices.For(LateRow(Daily()));
        var after = held.Apply(Daily(), null, lateSent: false, drillSent: false);
        Assert.False(after.LateAnnounced);
        Assert.True(BackupNotices.For(LateRow(after)).SendLate);

        Assert.False(BackupNotices.For(LateRow(Daily() with { AlertWhenLate = false })).SendLate);
    }

    [Fact]
    public void ADrillIsRemindedOncePerDueDateAndAgainAfterTheNextTestFallsDue()
    {
        var item = Daily() with { LastSuccess = Local(9, 29, 2), Drill = DrillCadence.Monthly, LastRestoreTest = Local(7, 1, 12) };
        var now = Local(9, 29, 12);
        var row = BackupSchedule.Row(item, BackupReading.Manual, now, Eastern);
        var notices = BackupNotices.For(row);
        Assert.True(notices.SendDrill);
        Assert.False(notices.SendLate);

        // Held (quiet hours, say): still due next time.
        Assert.True(BackupNotices.For(row with { Item = notices.Apply(item, row.DrillDue, false, false) }).SendDrill);

        // Sent: not again for that due date, however many sweeps later.
        var reminded = notices.Apply(item, row.DrillDue, false, drillSent: true);
        Assert.Equal(Local(8, 1, 12), reminded.DrillRemindedFor);
        Assert.False(BackupNotices.For(BackupSchedule.Row(reminded, BackupReading.Manual, now.AddDays(3), Eastern)).SendDrill);

        // Tested, and a month on it is due again — a new date, so a new reminder.
        var tested = reminded with { LastRestoreTest = Local(9, 30, 12) };
        Assert.False(BackupNotices.For(BackupSchedule.Row(tested, BackupReading.Manual, Local(10, 15, 12), Eastern)).SendDrill);
        Assert.True(BackupNotices.For(BackupSchedule.Row(tested, BackupReading.Manual, Local(10, 30, 13), Eastern)).SendDrill);
    }

    // ---------- the item and the shortcode's options ----------

    [Fact]
    public void AnItemSaysWhatIsMissingFromIt()
    {
        Assert.NotNull(new BackupItem().Problem());
        Assert.NotNull(new BackupItem { Name = "PBS", Source = BackupSources.Metric, SourceTarget = "pbs" }.Problem());
        Assert.NotNull(new BackupItem { Name = "Offsite", Source = BackupSources.Offsite }.Problem());
        Assert.NotNull(new BackupItem { Name = "x", GraceHours = -1 }.Problem());
        Assert.Null(new BackupItem { Name = "PBS", Source = BackupSources.Metric, SourceTarget = "pbs", SourceMetric = "hours_since_backup" }.Problem());
        Assert.Equal("pbs", new BackupItem { Source = BackupSources.Metric, SourceTarget = "pbs" }.AboutConnectionId);
        Assert.Equal("nas", new BackupItem { ConnectionId = "nas", Source = BackupSources.Metric, SourceTarget = "pbs" }.AboutConnectionId);
    }

    private static Shortcode Code(string text) => Shortcodes.Parse(text)!;

    [Theory]
    [InlineData("{{backups}}")]
    [InlineData("{{backups: late}}")]
    [InlineData("{{backups: limit=3}}")]
    public void BackupsIsABlockThatMayBeWrittenBare(string text)
    {
        var code = Code(text);
        Assert.Equal("backups", code.Kind);
        Assert.True(code.IsBlock);
        Assert.True(code.IsKnown);
    }

    [Fact]
    public void TheOptionsDefaultToEverythingTenLinesAndSayWhatIsWrong()
    {
        var all = BackupListOptions.Parse(Code("{{backups}}"), out var problem);
        Assert.Null(problem);
        Assert.Equal(new BackupListOptions(false, 10), all);

        Assert.Equal(new BackupListOptions(true, 3), BackupListOptions.Parse(Code("{{backups: late limit=3}}"), out _));
        Assert.Equal(100, BackupListOptions.Parse(Code("{{backups: limit=5000}}"), out _)!.Limit);

        Assert.Null(BackupListOptions.Parse(Code("{{backups: gremlins}}"), out problem));
        Assert.Contains("late or all", problem);
        Assert.Null(BackupListOptions.Parse(Code("{{backups: limit=lots}}"), out problem));
        Assert.Contains("limit=10", problem);
    }

    [Fact]
    public void ListsPutLateFirstAndLateOnlyLeavesOutWhatIsFine()
    {
        var now = Local(9, 29, 12);
        BackupRow Row(string name, DateTimeOffset? last, int position) =>
            BackupSchedule.Row(Daily(last) with { Name = name, Position = position, Drill = DrillCadence.Off }, BackupReading.Manual, now, Eastern);

        var rows = new[] { Row("Fine", Local(9, 29, 2), 0), Row("Never", null, 1), Row("Late", Local(9, 1, 2), 2) };

        Assert.Equal(["Late", "Never", "Fine"], new BackupListOptions(false, 10).Pick(rows).Select(r => r.Item.Name));
        Assert.Equal(["Late", "Never"], new BackupListOptions(true, 10).Pick(rows).Select(r => r.Item.Name));
        Assert.Equal(["Late"], new BackupListOptions(false, 1).Pick(rows).Select(r => r.Item.Name));
    }

    // ---------- the weekly summary ----------

    private static WeeklySummaryData Week(params BackupLine[] backups) => new()
    {
        From = Local(9, 22, 9),
        To = Local(9, 29, 9),
        Zone = Eastern,
        Backups = backups,
    };

    [Fact]
    public void TheSummarySaysAllIsWellInALine()
    {
        var digest = WeeklySummary.Build(Week(
            new BackupLine("Photos", BackupState.Ok, Local(9, 29, 2), false, null),
            new BackupLine("Vault", BackupState.Ok, Local(9, 29, 3), false, Local(9, 1, 12))));
        Assert.Contains("🛟 Backups\nAll 2 backups are on time.", digest.Text);
    }

    [Fact]
    public void TheSummaryListsOnlyWhatNeedsDoingLateFirst()
    {
        var digest = WeeklySummary.Build(Week(
            new BackupLine("Photos", BackupState.Ok, Local(9, 29, 2), false, null),
            new BackupLine("Vault", BackupState.Ok, Local(9, 29, 3), true, Local(6, 1, 12)),
            new BackupLine("NAS", BackupState.Late, Local(9, 25, 2), false, null)));

        Assert.Contains("• NAS — late, last one Fri 02:00", digest.Text);
        Assert.Contains("• Vault — restore test due, last tried 1 Jun", digest.Text);
        Assert.DoesNotContain("Photos", digest.Text);
        Assert.True(digest.Text.IndexOf("NAS —", StringComparison.Ordinal) < digest.Text.IndexOf("Vault —", StringComparison.Ordinal));
        Assert.Contains("**NAS** — late", digest.Markdown);
    }

    [Fact]
    public void NoBackupsListedMeansNoSection() =>
        Assert.DoesNotContain("Backups", WeeklySummary.Build(Week()).Text);

    // ---------- per-job readings from the providers ----------

    private sealed class Answer(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task DuplicatiReportsEachJobsAgeSoEachCanBeHeldToItsOwnSchedule()
    {
        var recent = DateTimeOffset.UtcNow.AddHours(-2).ToString("O");
        var old = DateTimeOffset.UtcNow.AddHours(-50).ToString("O");
        var body = """
            [
              {"Backup": {"Name": "Photos", "Metadata": {"LastBackupFinished": "RECENT"}}},
              {"Backup": {"Name": "Documents", "Metadata": {"LastBackupFinished": "OLD"}}}
            ]
            """.Replace("RECENT", recent).Replace("OLD", old);
        using var handler = new Answer(body);
        var result = await new DuplicatiProvider(new Factory(handler))
            .ProbeAsync(new Connection { Settings = new SettingsBag { ["url"] = "http://duplicati" } }, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.NotNull(result.Metrics);
        Assert.InRange(result.Metrics["hours_since_backup"], 1.9, 2.1);
        Assert.InRange(result.Metrics["hours_since_backup:Photos"], 1.9, 2.1);
        Assert.InRange(result.Metrics["hours_since_backup:Documents"], 49.9, 50.1);
    }

    [Fact]
    public async Task HealthchecksReportsEachChecksLastPingButNotAPausedOnes()
    {
        var body = $$"""
            {"checks": [
              {"name": "restic nightly", "status": "up", "last_ping": "{{DateTimeOffset.UtcNow.AddHours(-3):O}}"},
              {"name": "old job", "status": "paused", "last_ping": "{{DateTimeOffset.UtcNow.AddDays(-30):O}}"}
            ]}
            """;
        using var handler = new Answer(body);
        var result = await new HealthchecksProvider(new Factory(handler))
            .ProbeAsync(new Connection { Settings = new SettingsBag { ["url"] = "http://hc", ["api_key"] = "k" } }, CancellationToken.None);

        Assert.NotNull(result.Metrics);
        Assert.InRange(result.Metrics["hours_since_ping:restic nightly"], 2.9, 3.1);
        Assert.False(result.Metrics.ContainsKey("hours_since_ping:old job"));
    }
}
