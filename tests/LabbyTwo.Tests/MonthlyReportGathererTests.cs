using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// A month gathered from a real database, seeded with one of everything the report reads —
/// an outage, an incident, alerts, backups proven and tested, a roll-back, self-healing —
/// and some of each just outside the month, which must not be counted. Then the note: made
/// on its own tab, kept when the schedule comes round again, written over when asked.
/// </summary>
public sealed class MonthlyReportGathererTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;

    /// <summary>Last month, in UTC — inside the change feed's retention whenever the test runs.</summary>
    private readonly DateOnly _month = MonthlySchedule.ReportedMonth(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
    private readonly DateTimeOffset _from;
    private readonly DateTimeOffset _to;

    private sealed class Stub : IConnectionProvider
    {
        public string Type => "stub";
        public string DisplayName => "Stub";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];
        public IReadOnlyList<MetricSpec> Metrics => [new("latency_ms", "Response time", " ms")];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.FromMilliseconds(3), "OK", new Dictionary<string, double>()));
    }

    public MonthlyReportGathererTests()
    {
        (_from, _to) = MonthlySchedule.Span(_month, TimeZoneInfo.Utc);
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);
        services.AddSingleton<IConnectionProvider>(new Stub());
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<CapacityForecasts>();
        services.AddSingleton<ChangeStore>();
        services.AddSingleton<IncidentStore>();
        services.AddSingleton<SafeUpdateStore>();
        services.AddSingleton<NotesStore>();
        services.AddSingleton<ProbableCauses>();
        services.AddSingleton<MonthlyReportGatherer>();
        services.AddSingleton<MonthlyReports>();
        services.AddSingleton<MonthlyReportJob>();
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        TestHost.Teardown(_services, _directory);
        return ValueTask.CompletedTask;
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private async Task<Connection> ConnectionAsync(string name)
    {
        var connection = new Connection { Provider = "stub", Name = name };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        return connection;
    }

    private async Task SqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var db = await Get<Db>().OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task StatusAsync(string id, DateTimeOffset at, bool up) =>
        SqlAsync("INSERT INTO status_events (connection_id, ts, is_up, message) VALUES ($c, $t, $u, $m)",
            ("$c", id), ("$t", at.ToUnixTimeSeconds()), ("$u", up ? 1 : 0), ("$m", up ? "HTTP 200" : "Connection refused"));

    private Task ChangeAsync(DateTimeOffset at, string kind, string action, string? connection, string subject, string title) =>
        Get<ChangeStore>().RecordAsync(new Change(at, kind, action, connection, subject, title));

    /// <summary>One of everything in the month, and a decoy of each just before it.</summary>
    private async Task<(Connection Nas, Connection Plex)> SeedAsync()
    {
        var nas = await ConnectionAsync("NAS");
        var plex = await ConnectionAsync("Plex");
        var day10 = _from.AddDays(9);

        // NAS: up since before the month, down for two hours on the 10th. Plex: up throughout.
        await StatusAsync(nas.Id, _from.AddDays(-40), true);
        await StatusAsync(nas.Id, day10, false);
        await StatusAsync(nas.Id, day10.AddHours(2), true);
        await StatusAsync(plex.Id, _from.AddDays(-40), true);
        // After the month: not this month's outage.
        await StatusAsync(plex.Id, _to.AddMinutes(1), false);

        for (var at = _from.AddHours(1); at < _to; at = at.AddHours(6))
        {
            await SqlAsync("INSERT INTO samples (connection_id, metric, ts, value) VALUES ($c, 'latency_ms', $t, 20)",
                ("$c", nas.Id), ("$t", at.ToUnixTimeSeconds()));
        }

        await Get<IncidentStore>().SaveAsync(new Incident(0, day10, day10.AddHours(2), day10.AddHours(2), false,
            [new IncidentMember(IncidentMember.StatusKey(nas.Id), ChangeKinds.Status, nas.Id, "NAS", day10, day10.AddHours(2))]));
        // Long over before the month began.
        await Get<IncidentStore>().SaveAsync(new Incident(0, _from.AddDays(-20), _from.AddDays(-20).AddHours(1), _from.AddDays(-20).AddHours(1), false,
            [new IncidentMember(IncidentMember.StatusKey(plex.Id), ChangeKinds.Status, plex.Id, "Plex", _from.AddDays(-20), _from.AddDays(-20).AddHours(1))]));

        for (var i = 0; i < 3; i++)
            await ChangeAsync(day10.AddMinutes(i * 30), ChangeKinds.Alert, ChangeActions.Firing, nas.Id, "rule-cpu", "Alert fired: CPU pinned");
        await ChangeAsync(_from.AddHours(-1), ChangeKinds.Alert, ChangeActions.Firing, nas.Id, "rule-cpu", "Alert fired: CPU pinned");
        await ChangeAsync(day10, ChangeKinds.Alert, ChangeActions.Cleared, nas.Id, "rule-cpu", "Alert cleared: CPU pinned");

        await ChangeAsync(_from.AddDays(1), ChangeKinds.Backup, ChangeActions.Completed, null, "item-1", "Nextcloud backed up");
        await ChangeAsync(_from.AddDays(2), ChangeKinds.Backup, ChangeActions.Completed, null, "item-1", "Nextcloud backed up");
        await ChangeAsync(_from.AddDays(3), ChangeKinds.Backup, ChangeActions.Tested, null, "item-1", "Restore of Nextcloud tested");

        await ChangeAsync(day10.AddMinutes(5), ChangeKinds.Remediation, ChangeActions.Remediated, nas.Id, "status", "Restarted nas automatically");
        await ChangeAsync(day10.AddMinutes(15), ChangeKinds.Remediation, ChangeActions.Helped, nas.Id, "", "Restarted nas — NAS recovered");
        await ChangeAsync(_from.AddMinutes(-5), ChangeKinds.Remediation, ChangeActions.Remediated, nas.Id, "status", "Before the month");

        await Get<SafeUpdateStore>().AddAsync(new SafeUpdate(0, nas.Id, "radarr", "abc", "linuxserver/radarr:latest", "sha256:old", "", "",
            _from.AddDays(5), true, TimeSpan.FromMinutes(10), SafeUpdateState.RolledBack, Reason: "It kept restarting."));
        await Get<SafeUpdateStore>().AddAsync(new SafeUpdate(0, nas.Id, "sonarr", "def", "linuxserver/sonarr:latest", "sha256:old", "", "",
            _from.AddDays(-3), true, TimeSpan.FromMinutes(10), SafeUpdateState.Passed));
        return (nas, plex);
    }

    [Fact]
    public async Task AMonthIsGatheredFromItsOwnRowsOnly()
    {
        await SeedAsync();

        var data = await Get<MonthlyReportGatherer>().GatherAsync(_month, TimeZoneInfo.Utc, CancellationToken.None);

        Assert.Equal(_from, data.From);
        Assert.Equal(_to, data.To);
        var nas = Assert.Single(data.Services, s => s.Name == "NAS");
        var plex = Assert.Single(data.Services, s => s.Name == "Plex");
        Assert.Equal(TimeSpan.FromHours(2), nas.Down);
        Assert.Single(nas.Outages);
        Assert.True(nas.UptimePercent < 100);
        Assert.Equal(20, nas.AverageLatencyMs);
        Assert.Equal(100, plex.UptimePercent);
        Assert.Empty(plex.Outages);

        var incident = Assert.Single(data.Incidents);
        Assert.Equal("NAS", incident.Title);
        Assert.Equal(TimeSpan.FromHours(2), incident.End - incident.Start);

        var alert = Assert.Single(data.Alerts);
        Assert.Equal(new AlertCount("CPU pinned", "NAS", 3), alert);

        var backup = Assert.Single(data.Backups);
        Assert.Equal("Nextcloud", backup.Name);
        Assert.Equal(2, backup.Proven);
        Assert.Equal(1, backup.RestoreTests);

        var update = Assert.Single(data.Updates);
        Assert.Equal(("radarr", "NAS", SafeUpdateState.RolledBack), (update.Container, update.Host, update.State));

        Assert.Equal([ChangeActions.Remediated, ChangeActions.Helped], data.Healing.OrderBy(h => h.At).Select(h => h.Action));
        Assert.Null(data.Power);
        Assert.Null(data.FeedFrom);

        var note = MonthlyReport.Build(data);
        foreach (var heading in new[] { "## Uptime", "## Incidents", "## Alerts that fired most", "## Backups", "## Updates", "## Self-healing" })
            Assert.Contains(heading, note.Markdown);
        Assert.DoesNotContain("## Power", note.Markdown);
        Assert.Contains("{{chart: NAS / latency_ms", note.Markdown);
    }

    [Fact]
    public async Task TheNoteGoesOnItsOwnTabAndIsKeptOrWrittenOver()
    {
        await SeedAsync();
        var reports = Get<MonthlyReports>();

        var (made, link) = await reports.MakeAsync(_month, TimeZoneInfo.Utc, replace: false);
        Assert.NotNull(made);
        Assert.False(link.Replaced);
        var tab = Assert.Single(await Get<ConfigStore>().TabsAsync(), t => t.Settings.GetBool(MonthlyReports.TabSetting));
        Assert.Equal(TabKinds.Notes, tab.Kind);
        Assert.Equal(MonthlyReports.TabName, tab.Name);
        Assert.Equal($"t/{tab.Slug}#note-{link.NoteId}", link.Url);

        var note = Assert.Single(await Get<NotesStore>().ForTabAsync(tab.Id));
        Assert.Equal($"Monthly report · {MonthlySchedule.Name(_month)}", note.Title);
        Assert.Equal(made!.Markdown, note.Content);

        // Somebody writes in it. The schedule coming round again keeps what they wrote.
        await Get<NotesStore>().SaveAsync(note.Id, tab.Id, note.Title, note.Content + "\nMy notes.");
        var (again, kept) = await reports.MakeAsync(_month, TimeZoneInfo.Utc, replace: false);
        Assert.Null(again);
        Assert.Equal(note.Id, kept.NoteId);
        Assert.EndsWith("My notes.", Assert.Single(await Get<NotesStore>().ForTabAsync(tab.Id)).Content);

        // The button writes it over, in the same note on the same tab.
        var (redone, replaced) = await reports.MakeAsync(_month, TimeZoneInfo.Utc, replace: true);
        Assert.True(replaced.Replaced);
        Assert.Equal(note.Id, replaced.NoteId);
        Assert.Equal(redone!.Markdown, Assert.Single(await Get<NotesStore>().ForTabAsync(tab.Id)).Content);
        Assert.Single(await Get<ConfigStore>().TabsAsync(), t => t.Settings.GetBool(MonthlyReports.TabSetting));
    }

    [Fact]
    public async Task TheJobMakesItOnceWhenDue()
    {
        await SeedAsync();
        var settings = Get<AppSettingsStore>();
        var job = Get<MonthlyReportJob>();
        var zone = TimeZoneInfo.Utc;
        var schedule = new MonthlySchedule(true, 1, new TimeOnly(8, 0), false, []);
        var due = schedule.In(MonthlySchedule.FirstOfMonth(DateTimeOffset.UtcNow, zone), zone);

        await settings.SaveAsync(schedule.ToSettings());
        // Switched on without being armed: the first wake arms it, and makes nothing.
        Assert.Null(await job.TickAsync(due.AddMinutes(1), zone, CancellationToken.None));

        await settings.SaveAsync(MonthlySchedule.CoveredKey, WeeklySchedule.FormatInstant(due.AddDays(-3)));
        Assert.Null(await job.TickAsync(due.AddMinutes(-1), zone, CancellationToken.None));

        var link = await job.TickAsync(due, zone, CancellationToken.None);
        Assert.NotNull(link);
        Assert.Equal(_month, link!.Month);
        Assert.Null(await job.TickAsync(due.AddMinutes(1), zone, CancellationToken.None));

        var bag = await settings.AllAsync();
        Assert.Equal(due, WeeklySchedule.ParseInstant(bag.Get(MonthlySchedule.LastMadeKey)));
    }
}
