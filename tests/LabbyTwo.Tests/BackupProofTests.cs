#pragma warning disable BL0006 // The interactive test renderer is a Renderer, which is the whole point of it.
using System.Net;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Services.Offsite;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// The Backups page against a real database, monitor and alert service: each kind of proof
/// read, the sweep recording completions and lateness in the feed and sending each notice
/// once — held by maintenance, quiet hours and mute windows until they lift — restore
/// tests recorded, the queries seeking their index, and {{backups}} drawn.
/// </summary>
public sealed class BackupProofTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly Stub _stub = new();
    private readonly RecordingChannel _channel = new();
    private InteractiveRenderer? _renderer;

    /// <summary>A backup server whose "hours since the last backup" the test sets.</summary>
    private sealed class Stub : IConnectionProvider
    {
        public string Type => "stubpbs";
        public string DisplayName => "Stub PBS";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];
        public IReadOnlyList<MetricSpec> Metrics => [new("hours_since_backup", "Since the last backup", " h", 1)];

        public double? Hours { get; set; } = 2;

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.FromMilliseconds(2), "OK",
                Hours is { } hours ? new Dictionary<string, double> { ["hours_since_backup"] = hours } : []));
    }

    private sealed class RecordingChannel : IAlertChannel
    {
        public string Type => "recording";
        public string DisplayName => "Recording channel";
        public string Icon => "📼";
        public string Description => "Test double.";
        public IReadOnlyList<FieldSpec> Fields => [];
        public List<Alert> Sent { get; } = [];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) => Task.FromResult(ProbeResult.Up(TimeSpan.Zero));

        public Task SendAsync(Connection channel, Alert alert, CancellationToken ct)
        {
            lock (Sent)
                Sent.Add(alert);
            return Task.CompletedTask;
        }
    }

    public BackupProofTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);
        services.AddSingleton<IConnectionProvider>(_stub);
        services.AddSingleton<IConnectionProvider>(_channel);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<LatestReadings>();
        services.AddSingleton<Offload>();
        services.AddSingleton<DisplayUnits>();
        services.AddSingleton<SharedSeries>();
        services.AddSingleton<Markdown>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<ChangeStore>();
        services.AddSingleton<OffsiteSettingsStore>();
        services.AddSingleton<BackupStore>();
        services.AddSingleton<BackupProof>();
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        Get<AlertService>().Zone = TimeZoneInfo.Utc;
        Get<BackupProof>().Zone = TimeZoneInfo.Utc;
    }

    public async ValueTask DisposeAsync()
    {
        if (_renderer is not null)
            await _renderer.DisposeAsync();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private InteractiveRenderer Renderer => _renderer ??= new InteractiveRenderer(_services);

    private BackupProof Proof => Get<BackupProof>();

    private async Task<Connection> ConnectionAsync(string name, string provider = "stubpbs")
    {
        var connection = new Connection { Provider = provider, Name = name };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        return connection;
    }

    private async Task<BackupItem> ItemAsync(BackupItem item)
    {
        await Proof.SaveAsync(item);
        return item;
    }

    private async Task<IReadOnlyList<Change>> FeedAsync() =>
        await Get<ChangeStore>().QueryAsync(new ChangeQuery(DateTimeOffset.Now.AddYears(-1), DateTimeOffset.Now.AddYears(1), [ChangeKinds.Backup]));

    private List<Alert> Sent
    {
        get
        {
            lock (_channel.Sent)
                return [.. _channel.Sent];
        }
    }

    // ---------- a connection's reading ----------

    [Fact]
    public async Task ALateBackupIsRecordedAndAnnouncedOnceThenItsReturn()
    {
        var pbs = await ConnectionAsync("PBS");
        await ConnectionAsync("Email", "recording");
        var item = await ItemAsync(new BackupItem
        {
            Name = "VMs", Source = BackupSources.Metric, SourceTarget = pbs.Id, SourceMetric = "hours_since_backup",
            Frequency = BackupFrequency.Daily, Drill = DrillCadence.Off,
        });
        var monitor = Get<HealthMonitor>();

        // Forty hours since the last one: a daily backup with six hours' grace is late.
        _stub.Hours = 40;
        await monitor.RefreshAsync(pbs);
        var row = Assert.Single(await Proof.SweepAsync(DateTimeOffset.Now, CancellationToken.None));
        Assert.Equal(BackupState.Late, row.Status.State);
        Assert.Equal("PBS · hours_since_backup", row.Reading.Source);

        var late = Assert.Single(Sent);
        Assert.Equal(AlertLevel.Down, late.Level);
        Assert.Equal("The backup of VMs is late", late.Title);
        Assert.Equal("backups", late.Link);
        var recorded = Assert.Single(await FeedAsync());
        Assert.Equal(ChangeActions.Late, recorded.Action);
        Assert.Equal(item.Id, recorded.Subject);

        // Every sweep after, still late: nothing more said or recorded.
        await monitor.RefreshAsync(pbs);
        await Proof.SweepAsync(DateTimeOffset.Now, CancellationToken.None);
        await Proof.SweepAsync(DateTimeOffset.Now, CancellationToken.None);
        Assert.Single(Sent);
        Assert.Single(await FeedAsync());

        // Last night's ran after all.
        _stub.Hours = 1;
        await monitor.RefreshAsync(pbs);
        row = Assert.Single(await Proof.SweepAsync(DateTimeOffset.Now, CancellationToken.None));
        Assert.Equal(BackupState.Ok, row.Status.State);
        Assert.Equal(2, Sent.Count);
        Assert.Equal(AlertLevel.Up, Sent[1].Level);
        Assert.Equal("VMs is backed up again", Sent[1].Title);
        var completed = (await FeedAsync()).Single(c => c.Action == ChangeActions.Completed);
        Assert.Equal(ChangeActions.Completed, completed.Action);
        Assert.Equal("VMs backed up", completed.Title);
        Assert.Contains("Proven by PBS · hours_since_backup", completed.Detail);

        // Nothing new: no new line.
        await Proof.SweepAsync(DateTimeOffset.Now, CancellationToken.None);
        Assert.Equal(2, (await FeedAsync()).Count);
        Assert.Equal(2, Sent.Count);
    }

    [Fact]
    public async Task ASourceThatCannotBeAskedIsMissingAndSaysWhy()
    {
        var pbs = await ConnectionAsync("PBS");
        await ItemAsync(new BackupItem { Name = "VMs", Source = BackupSources.Metric, SourceTarget = pbs.Id, SourceMetric = "hours_since_backup" });

        var row = Assert.Single(await Proof.RowsAsync(DateTimeOffset.Now));
        Assert.Equal(BackupState.Missing, row.Status.State);
        Assert.Equal("PBS has not been checked yet", row.Status.Problem);

        _stub.Hours = null;
        await Get<HealthMonitor>().RefreshAsync(pbs);
        row = Assert.Single(await Proof.RowsAsync(DateTimeOffset.Now));
        Assert.Equal("PBS does not report hours_since_backup", row.Status.Problem);

        await Get<ConfigStore>().DeleteConnectionAsync(pbs.Id);
        row = Assert.Single(await Proof.RowsAsync(DateTimeOffset.Now));
        Assert.Contains("deleted", row.Status.Problem);
    }

    [Fact]
    public async Task PointingAnItemAtAnotherSourceForgetsWhatTheOldOneProved()
    {
        var pbs = await ConnectionAsync("PBS");
        var item = await ItemAsync(new BackupItem { Name = "VMs", Source = BackupSources.Metric, SourceTarget = pbs.Id, SourceMetric = "hours_since_backup" });
        await Get<HealthMonitor>().RefreshAsync(pbs);
        await Proof.SweepAsync(DateTimeOffset.Now, CancellationToken.None);
        Assert.NotNull((await Get<BackupStore>().GetAsync(item.Id))!.LastSuccess);

        // A rename keeps it.
        await Proof.SaveAsync(item with { Name = "Virtual machines" });
        Assert.NotNull((await Get<BackupStore>().GetAsync(item.Id))!.LastSuccess);

        await Proof.SaveAsync(item with { Source = BackupSources.Manual, SourceTarget = "", SourceMetric = "" });
        var manual = (await Get<BackupStore>().GetAsync(item.Id))!;
        Assert.Null(manual.LastSuccess);
        Assert.Equal(BackupState.Never, Assert.Single(await Proof.RowsAsync(DateTimeOffset.Now)).Status.State);
    }

    // ---------- LabbyTwo's own backups ----------

    [Fact]
    public async Task LabbyTwosOwnBackupIsDatedByTheNewestCopyInItsFolder()
    {
        await ItemAsync(new BackupItem { Name = "LabbyTwo", Source = BackupSources.LabbyTwo });
        Assert.Equal(BackupState.Never, Assert.Single(await Proof.RowsAsync(DateTimeOffset.Now)).Status.State);

        var folder = Path.Combine(_directory, "backups");
        Directory.CreateDirectory(folder);
        var older = Path.Combine(folder, "labbytwo-2026-09-27.db");
        var newer = Path.Combine(folder, "labbytwo-2026-09-28.db");
        await File.WriteAllTextAsync(older, "x");
        await File.WriteAllTextAsync(newer, "x");
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "not a backup");
        var at = new DateTime(2026, 9, 28, 2, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(older, at.AddDays(-1));
        File.SetLastWriteTimeUtc(newer, at);
        File.SetLastWriteTimeUtc(Path.Combine(folder, "notes.txt"), at.AddDays(1));

        var row = Assert.Single(await Proof.RowsAsync(new DateTimeOffset(at.AddHours(3))));
        Assert.Equal(BackupState.Ok, row.Status.State);
        Assert.Equal(new DateTimeOffset(at), row.Status.LastSuccess);
        Assert.Null(row.Status.Problem);

        await Get<AppSettingsStore>().SaveAsync(BackupJob.EnabledKey, "false");
        row = Assert.Single(await Proof.RowsAsync(new DateTimeOffset(at.AddDays(3))));
        Assert.Equal(BackupState.Late, row.Status.State);
        Assert.Contains("switched off", row.Status.Problem);
    }

    [Fact]
    public async Task AnOffsiteCopyIsDatedByItsLastSuccess()
    {
        var offsite = Get<OffsiteSettingsStore>();
        var destination = new OffsiteDestination { Name = "USB disk", Path = "/offsite" };
        await offsite.SaveDestinationsAsync([destination]);
        var at = DateTimeOffset.Parse("2026-09-28T02:05:00Z");
        await offsite.SaveStatusesAsync(new Dictionary<string, DestinationStatus>
        {
            [destination.Id] = new() { LastRunAt = at, Ok = true, LastSuccessAt = at },
        });

        await ItemAsync(new BackupItem { Name = "LabbyTwo off-site", Source = BackupSources.Offsite, SourceTarget = destination.Id });
        var row = Assert.Single(await Proof.RowsAsync(at.AddHours(1)));
        Assert.Equal(at, row.Status.LastSuccess);
        Assert.Equal("the off-site copy to USB disk", row.Reading.Source);

        await offsite.SaveDestinationsAsync([]);
        row = Assert.Single(await Proof.RowsAsync(at.AddHours(1)));
        Assert.Equal(BackupState.Missing, row.Status.State);
        Assert.Contains("removed", row.Status.Problem);
    }

    // ---------- by hand, and restore drills ----------

    [Fact]
    public async Task MarkingBackedUpByHandIsTheDateAndIsRecorded()
    {
        var item = await ItemAsync(new BackupItem { Name = "Laptop", Frequency = BackupFrequency.Weekly, Drill = DrillCadence.Off });
        var at = DateTimeOffset.Parse("2026-09-27T18:30:00Z");

        await Proof.MarkBackedUpAsync(item.Id, "chris", at);

        var row = Assert.Single(await Proof.RowsAsync(at.AddDays(2)));
        Assert.Equal(BackupState.Ok, row.Status.State);
        Assert.Equal(at, row.Status.LastSuccess);
        Assert.Equal("ticked by chris", row.Reading.Source);
        var change = Assert.Single(await FeedAsync());
        Assert.Equal("Laptop backed up", change.Title);
        Assert.Equal("Ticked by chris.", change.Detail);

        Assert.Equal(BackupState.Late, Assert.Single(await Proof.RowsAsync(at.AddDays(8.1))).Status.State);
    }

    [Fact]
    public async Task ADueRestoreTestIsRemindedOnceAndATestMovesItOn()
    {
        await ConnectionAsync("Email", "recording");
        var now = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
        var item = await ItemAsync(new BackupItem
        {
            Name = "Vault", Drill = DrillCadence.Quarterly, CreatedAt = now.AddMonths(-4), AlertWhenLate = false,
        });

        await Proof.SweepAsync(now, CancellationToken.None);
        await Proof.SweepAsync(now.AddMinutes(5), CancellationToken.None);
        await Proof.SweepAsync(now.AddDays(3), CancellationToken.None);
        var reminder = Assert.Single(Sent);
        Assert.Equal(AlertLevel.Info, reminder.Level);
        Assert.Equal("Time to test restoring Vault", reminder.Title);
        Assert.Contains("never been restored", reminder.Body);

        var test = await Proof.MarkTestedAsync(item.Id, "chris", "Restored to /tmp/vault; it opened.", now.AddDays(3));
        Assert.NotNull(test);
        var tests = await Proof.TestsAsync(item.Id);
        Assert.Equal("Restored to /tmp/vault; it opened.", Assert.Single(tests).Notes);
        var change = Assert.Single(await FeedAsync());
        Assert.Equal(ChangeActions.Tested, change.Action);
        Assert.Equal("Restore of Vault tested", change.Title);
        Assert.Contains("By chris.", change.Detail);

        var row = Assert.Single(await Proof.SweepAsync(now.AddDays(4), CancellationToken.None));
        Assert.False(row.DrillOverdue);
        Assert.Equal(now.AddDays(3).AddMonths(3), row.DrillDue);
        Assert.Equal("chris", row.Item.LastRestoreTestBy);
        Assert.Single(Sent);

        // A quarter on, due again: a new reminder, naming the last test.
        await Proof.SweepAsync(now.AddDays(3).AddMonths(3).AddHours(1), CancellationToken.None);
        Assert.Equal(2, Sent.Count);
        Assert.Contains("by chris", Sent[1].Body);
    }

    [Fact]
    public async Task AReminderHeldByMaintenanceGoesOnceWhenItLifts()
    {
        await ConnectionAsync("Email", "recording");
        var now = DateTimeOffset.Now;
        await ItemAsync(new BackupItem { Name = "Vault", Drill = DrillCadence.Monthly, CreatedAt = now.AddMonths(-2), AlertWhenLate = false });

        await Get<AppSettingsStore>().SaveAsync(Maintenance.Key, Maintenance.Value(TimeSpan.FromHours(1), now));
        await Proof.SweepAsync(now, CancellationToken.None);
        Assert.Empty(Sent);

        await Get<AppSettingsStore>().SaveAsync(Maintenance.Key, Maintenance.Cleared);
        await Proof.SweepAsync(now, CancellationToken.None);
        await Proof.SweepAsync(now, CancellationToken.None);
        Assert.Single(Sent);
    }

    [Fact]
    public async Task ALateAlertWaitsOutQuietHoursEvenInDownOnlyMode()
    {
        await ConnectionAsync("Email", "recording");
        var item = await ItemAsync(new BackupItem { Name = "Laptop", Drill = DrillCadence.Off });
        var night = DateTimeOffset.Parse("2026-09-29T03:00:00Z");
        await Proof.MarkBackedUpAsync(item.Id, "chris", night.AddDays(-3));
        await Get<AppSettingsStore>().SaveAsync(new Dictionary<string, string>
        {
            [AlertPolicy.FromKey] = "23:00", [AlertPolicy.ToKey] = "07:00", [AlertPolicy.ModeKey] = AlertPolicy.DownOnly,
        });

        await Proof.SweepAsync(night, CancellationToken.None);
        Assert.Empty(Sent);
        Assert.Equal("quiet hours", await Proof.HeldAsync(item, night, CancellationToken.None));

        await Proof.SweepAsync(night.AddHours(5), CancellationToken.None);
        await Proof.SweepAsync(night.AddHours(6), CancellationToken.None);
        Assert.Equal("The backup of Laptop is late", Assert.Single(Sent).Title);
        // The feed recorded it when it happened, not when it could be said.
        Assert.Contains(await FeedAsync(), c => c.Action == ChangeActions.Late && c.At == night);
    }

    [Fact]
    public async Task AMuteWindowOnTheConnectionItIsAboutHoldsItsAlerts()
    {
        await ConnectionAsync("Email", "recording");
        var nas = await ConnectionAsync("NAS");
        var item = await ItemAsync(new BackupItem { Name = "Shares", ConnectionId = nas.Id, Drill = DrillCadence.Off });
        var now = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
        await Proof.MarkBackedUpAsync(item.Id, "chris", now.AddDays(-3));

        var window = new MuteWindow
        {
            Name = "NAS scrub", Days = MuteWindow.Week, Start = new TimeOnly(0, 0), End = new TimeOnly(0, 0),
            Scope = MuteScope.Connections, Targets = [nas.Id],
        };
        await Get<MuteWindowStore>().SaveAsync(window);
        await Proof.SweepAsync(now, CancellationToken.None);
        Assert.Empty(Sent);
        Assert.Equal("muted by NAS scrub", await Proof.HeldAsync(item, now, CancellationToken.None));

        await Get<MuteWindowStore>().DeleteAsync(window.Id);
        await Proof.SweepAsync(now.AddMinutes(5), CancellationToken.None);
        Assert.Single(Sent);
    }

    // ---------- query plans ----------

    private async Task<List<string>> PlanAsync(string sql)
    {
        await using var db = await Get<Db>().OpenAsync();
        var explain = db.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(sql, @"\$\w+"))
        {
            if (!explain.Parameters.Contains(match.Value))
                explain.Parameters.AddWithValue(match.Value, match.Value == "$item" ? "x" : 1);
        }
        var steps = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            steps.Add(reader.GetString(3));
        return steps;
    }

    [Fact]
    public async Task TheListSeeksEachItemsNewestTestRatherThanReadingThemAll()
    {
        var plan = await PlanAsync(BackupStore.ItemsSql);
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN restore_tests", StringComparison.Ordinal));
        Assert.Equal(2, plan.Count(step => step.StartsWith("SEARCH t USING", StringComparison.Ordinal) && step.Contains("ix_restore_tests_item")));
    }

    [Fact]
    public async Task OneItemsTestsSeekTheIndexNewestFirstWithoutSorting()
    {
        var plan = await PlanAsync(BackupStore.TestsSql);
        Assert.Contains(plan, step => step.StartsWith("SEARCH restore_tests USING INDEX ix_restore_tests_item", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    // ---------- the shortcode ----------

    [Fact]
    public async Task BackupsListsLateFirstAndLateOnlyLeavesOutTheRest()
    {
        var now = DateTimeOffset.Now;
        var fine = await ItemAsync(new BackupItem { Name = "Photos", Position = 0, Drill = DrillCadence.Off });
        var late = await ItemAsync(new BackupItem { Name = "Vault <b>", Position = 1, Drill = DrillCadence.Off });
        await Proof.MarkBackedUpAsync(fine.Id, "chris", now.AddHours(-2));
        await Proof.MarkBackedUpAsync(late.Id, "chris", now.AddDays(-5));

        await Renderer.RenderMarkdownAsync("{{backups}}");
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Photos"));
        Assert.True(html.IndexOf("Vault", StringComparison.Ordinal) < html.IndexOf("Photos", StringComparison.Ordinal));
        Assert.Contains("late — last one", html);
        Assert.Contains("last backup 2h ago", html);
        Assert.Contains("All backups and restore tests", html);
        Assert.DoesNotContain("<b>", await Renderer.HtmlAsync());

        await Renderer.RenderMarkdownAsync("{{backups: late}}");
        html = WebUtility.HtmlDecode(await Renderer.WaitForAsync(h => h.Contains("Vault", StringComparison.Ordinal)
                                                                  && !h.Contains("Photos", StringComparison.Ordinal)));
        Assert.Contains("Vault", html);

        // Marked, it drops out of the late list as soon as it is saved.
        await Proof.MarkBackedUpAsync(late.Id, "chris", now);
        html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Every backup is on time"));
        Assert.DoesNotContain("Vault", html);
    }

    [Fact]
    public async Task BackupsSaysWhenNothingIsListedAndWhenAnOptionIsWrong()
    {
        await Renderer.RenderMarkdownAsync("{{backups}}");
        Assert.Contains("Nothing is on the", WebUtility.HtmlDecode(await Renderer.WaitForAsync("Nothing is on the")));

        await Renderer.RenderMarkdownAsync("{{backups: gremlins}}");
        Assert.Contains("late or all", WebUtility.HtmlDecode(await Renderer.WaitForAsync("sc-problem")));
    }

    [Fact]
    public async Task ThePageShowsEachItemWithItsDatesAndMarksTheLateOne()
    {
        var now = DateTimeOffset.Now;
        var late = await ItemAsync(new BackupItem { Name = "Vault", Drill = DrillCadence.Quarterly, CreatedAt = now.AddMonths(-4) });
        await Proof.MarkBackedUpAsync(late.Id, "chris", now.AddDays(-3));
        await ItemAsync(new BackupItem { Name = "Photos", Source = BackupSources.LabbyTwo, Drill = DrillCadence.Off });

        await Renderer.RenderAsync<LabbyTwo.Components.Pages.BackupsPage>(new Dictionary<string, object?>());
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Photos"));
        Assert.Contains("backup-row is-late", html);
        Assert.Contains("restore test due", html);
        Assert.Contains("by chris", html);
        Assert.Contains("Mark backed up", html);
        Assert.Contains("1 late", html);
        Assert.Contains("never backed up", html);
    }
}

/// <summary>
/// Lets the test renderer draw a page that declares <c>@rendermode InteractiveServer</c>: it
/// already renders interactively, so the page is simply made here rather than refused.
/// </summary>
internal sealed partial class InteractiveRenderer
{
    protected override IComponent ResolveComponentForRenderMode(
        Type componentType, int? parentComponentId, IComponentActivator componentActivator, IComponentRenderMode renderMode) =>
        componentActivator.CreateInstance(componentType);
}
