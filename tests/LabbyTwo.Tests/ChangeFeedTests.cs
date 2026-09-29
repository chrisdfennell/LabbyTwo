#pragma warning disable BL0006 // The interactive test renderer is a Renderer, which is the whole point of it.
using System.Net;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// The change feed and incidents against a real database: what is stored and read back,
/// what the retention removes, that every query seeks rather than scans, that the watcher
/// records what the monitor and the evaluator notice, that the tracker turns an outage into
/// an incident, and that the two shortcodes draw them.
/// </summary>
public sealed class ChangeFeedTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly Stub _stub = new();
    private InteractiveRenderer? _renderer;

    /// <summary>A connection whose probe reports whatever the test sets.</summary>
    private sealed class Stub : IConnectionProvider
    {
        public string Type => "stub";
        public string DisplayName => "Stub";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];
        public IReadOnlyList<MetricSpec> Metrics => [new("disk_percent", "Disk used", "%", 1)];

        public bool Up { get; set; } = true;
        public double Disk { get; set; } = 50;

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(Up
                ? ProbeResult.Up(TimeSpan.FromMilliseconds(3), "OK", new Dictionary<string, double> { ["disk_percent"] = Disk })
                : ProbeResult.Down(TimeSpan.Zero, "Connection refused"));
    }

    public ChangeFeedTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        // One failed probe is down, so a test can take something down in one step.
        services.AddTestStorage(_directory, options => options.FailuresBeforeDown = 1);
        services.AddSingleton<IConnectionProvider>(_stub);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<CapacityForecasts>();
        services.AddSingleton<MetricBaselines>();
        services.AddSingleton<MetricAlertService>();
        services.AddSingleton<LatestReadings>();
        services.AddSingleton<Offload>();
        services.AddSingleton<DisplayUnits>();
        services.AddSingleton<SharedSeries>();
        services.AddSingleton<Markdown>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<ChangeStore>();
        services.AddSingleton<IncidentStore>();
        services.AddSingleton<ChangeWatcher>();
        services.AddSingleton<IncidentTracker>();
        services.AddSingleton<DnsCheck>();
        services.AddSingleton<NotesStore>();
        services.AddSingleton<ProbableCauses>();
        services.AddSingleton<IncidentWriteUps>();
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_renderer is not null)
            await _renderer.DisposeAsync();
        await Get<ChangeWatcher>().StopAsync(CancellationToken.None);
        await Get<IncidentTracker>().StopAsync(CancellationToken.None);
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private InteractiveRenderer Renderer => _renderer ??= new InteractiveRenderer(_services);

    private async Task<Connection> ConnectionAsync(string name, string provider = "stub")
    {
        var connection = new Connection { Provider = provider, Name = name };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        return connection;
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> read, Func<T, bool>? done = null) where T : class
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var value = await read();
            if (value is not null && (done is null || done(value)))
                return value;
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Never happened.");
            await Task.Delay(20);
        }
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // ---------- the store ----------

    [Fact]
    public async Task ChangesComeBackNewestFirstAndFilterByKindConnectionAndWindow()
    {
        var store = Get<ChangeStore>();
        var heard = new List<Change>();
        store.Recorded += heard.Add;

        await store.RecordAsync(new Change(Now.AddHours(-30), ChangeKinds.Status, ChangeActions.Down, "nas", "", "NAS went down"));
        await store.RecordAsync(new Change(Now.AddMinutes(-20), ChangeKinds.Container, ChangeActions.Restarted, "docker", "plex", "plex restarted"));
        // Two in the same second: the one written second is newer.
        await store.RecordAsync(new Change(Now.AddMinutes(-5), ChangeKinds.Status, ChangeActions.Down, "nas", "", "NAS went down again"));
        await store.RecordAsync(new Change(Now.AddMinutes(-5), ChangeKinds.Alert, ChangeActions.Firing, "nas", "r1", "Alert fired: NAS · disk"));

        Assert.Equal(4, heard.Count);
        Assert.All(heard, c => Assert.True(c.Id > 0));

        var day = await store.QueryAsync(new ChangeQuery(Now.AddDays(-1), Now));
        Assert.Equal(["Alert fired: NAS · disk", "NAS went down again", "plex restarted"], day.Select(c => c.Title));

        var nas = await store.QueryAsync(new ChangeQuery(Now.AddDays(-2), Now, ConnectionId: "nas"));
        Assert.Equal(3, nas.Count);

        var kinds = await store.QueryAsync(new ChangeQuery(Now.AddDays(-2), Now, [ChangeKinds.Container, ChangeKinds.Alert]));
        Assert.Equal(["Alert fired: NAS · disk", "plex restarted"], kinds.Select(c => c.Title));

        var one = Assert.Single(await store.QueryAsync(new ChangeQuery(Now.AddDays(-2), Now, Limit: 1)));
        Assert.Equal("r1", one.Subject);
        Assert.Equal(ChangeKinds.Alert, one.Kind);
    }

    [Fact]
    public async Task PruningDropsOnlyWhatIsPastTheRetention()
    {
        var store = Get<ChangeStore>();
        await store.RecordAsync(new Change(Now.AddDays(-120), ChangeKinds.Status, ChangeActions.Down, "nas", "", "old"));
        await store.RecordAsync(new Change(Now.AddDays(-80), ChangeKinds.Status, ChangeActions.Down, "nas", "", "kept"));

        Assert.Equal(1, await store.PruneAsync(Now, CancellationToken.None));
        Assert.Equal("kept", Assert.Single(await store.QueryAsync(new ChangeQuery(Now.AddYears(-1), Now))).Title);
        Assert.Equal(0, await store.PruneAsync(Now, CancellationToken.None));
    }

    [Fact]
    public async Task IncidentsRoundTripAndOpenOnesAreFoundHoweverOld()
    {
        var store = Get<IncidentStore>();
        var start = Now.AddDays(-40);
        var old = await store.SaveAsync(new Incident(0, start, null, start, true,
            [new IncidentMember("status:nas", ChangeKinds.Status, "nas", "NAS", start, null)]));
        var recent = await store.SaveAsync(new Incident(0, Now.AddHours(-2), Now.AddHours(-1), Now.AddHours(-1), false,
        [
            new IncidentMember("status:plex", ChangeKinds.Status, "plex", "Plex", Now.AddHours(-2), Now.AddHours(-1)),
            new IncidentMember("alert:r1:plex", ChangeKinds.Alert, "plex", "Plex · CPU above 90", Now.AddHours(-2), Now.AddHours(-1)),
        ]));
        Assert.True(old.Id > 0);

        // Updating a member rather than adding one.
        await store.SaveAsync(old with { EndedAt = null, Members = [old.Members[0] with { UpAt = null }] });

        var listed = await store.RecentAsync(Now.AddDays(-7), 10);
        Assert.Equal([recent.Id, old.Id], listed.Select(i => i.Id));
        var read = listed.Single(i => i.Id == recent.Id);
        Assert.Equal(2, read.Members.Count);
        Assert.Equal(Now.AddHours(-1).ToUnixTimeSeconds(), read.EndedAt!.Value.ToUnixTimeSeconds());
        Assert.True(listed.Single(i => i.Id == old.Id).Maintenance);

        Assert.Equal([old.Id], (await store.RecentAsync(Now.AddDays(-7), 10, openOnly: true)).Select(i => i.Id));
        Assert.Equal(recent.Id, (await store.GetAsync(recent.Id))!.Id);
    }

    [Fact]
    public async Task PruningIncidentsKeepsOpenOnesAndRemovesMembers()
    {
        var store = Get<IncidentStore>();
        var ancient = Now.AddDays(-400);
        var closed = await store.SaveAsync(new Incident(0, ancient, ancient.AddHours(1), ancient.AddHours(1), false,
            [new IncidentMember("status:nas", ChangeKinds.Status, "nas", "NAS", ancient, ancient.AddHours(1))]));
        var open = await store.SaveAsync(new Incident(0, ancient, null, ancient, false,
            [new IncidentMember("status:plex", ChangeKinds.Status, "plex", "Plex", ancient, null)]));

        Assert.Equal(1, await store.PruneAsync(Now, CancellationToken.None));
        Assert.Null(await store.GetAsync(closed.Id));
        Assert.NotNull(await store.GetAsync(open.Id));

        await using var db = await Get<Db>().OpenAsync();
        var count = db.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM incident_members";
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    // ---------- query plans ----------

    /// <summary>
    /// The plan of <paramref name="sql"/> against the real, migrated schema — not a copy of
    /// it — with every parameter bound to something, since EXPLAIN needs them bound.
    /// </summary>
    private async Task<List<string>> PlanAsync(string sql)
    {
        await using var db = await Get<Db>().OpenAsync();
        var explain = db.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(sql, @"\$\w+"))
        {
            if (!explain.Parameters.Contains(match.Value))
                explain.Parameters.AddWithValue(match.Value, match.Value == "$connection" || match.Value.StartsWith("$k") ? "x" : 1);
        }
        var steps = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            steps.Add(reader.GetString(3));
        return steps;
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(3, true)]
    public async Task TheFeedSeeksItsTimeIndexAndNeverSorts(int kinds, bool connection)
    {
        var plan = await PlanAsync(ChangeStore.QuerySql(kinds, connection));

        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN changes", StringComparison.Ordinal));
        Assert.Contains(plan, step => step.StartsWith("SEARCH changes USING INDEX ix_changes_ts", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PruningTheFeedSeeksItsTimeIndex()
    {
        var plan = await PlanAsync(ChangeStore.PruneSql);
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN changes", StringComparison.Ordinal));
        Assert.Contains(plan, step => step.Contains("ix_changes_ts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IncidentQueriesSeekTheirIndexes()
    {
        var open = await PlanAsync(IncidentStore.OpenSql);
        Assert.Contains(open, step => step.Contains("ix_incidents_open", StringComparison.Ordinal));
        Assert.DoesNotContain(open, step => step.StartsWith("SCAN incidents ", StringComparison.Ordinal) && !step.Contains("INDEX"));

        var recent = await PlanAsync(IncidentStore.RecentSql);
        Assert.Contains(recent, step => step.StartsWith("SEARCH incidents USING INDEX ix_incidents_started", StringComparison.Ordinal));

        var members = await PlanAsync(IncidentStore.MembersSql);
        Assert.Contains(members, step => step.StartsWith("SEARCH incident_members USING PRIMARY KEY", StringComparison.Ordinal));

        foreach (var sql in new[] { IncidentStore.PruneMembersSql, IncidentStore.PruneIncidentsSql })
        {
            var plan = await PlanAsync(sql);
            Assert.DoesNotContain(plan, step => step.StartsWith("SCAN incident", StringComparison.Ordinal));
        }

        var one = await PlanAsync(IncidentStore.ByIdSql);
        Assert.Contains(one, step => step.StartsWith("SEARCH incidents USING INTEGER PRIMARY KEY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWriteUpsNoteIsFoundByItsPrimaryKey()
    {
        var plan = await PlanAsync(NotesStore.ByIdSql);
        Assert.Contains(plan, step => step.StartsWith("SEARCH notes USING INDEX sqlite_autoindex_notes_1", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN notes", StringComparison.Ordinal));
    }

    // ---------- the watcher ----------

    private static ContainerRow Row(string name, string id, string imageId, string state, string status) =>
        new(id, name, "lscr.io/linuxserver/" + name + ":latest", imageId, state, status, DateTimeOffset.UnixEpoch, [],
            new Dictionary<string, string>());

    [Fact]
    public async Task TheFirstLookAtAHostOnlyRemembersItAndLaterLooksRecordWhatChanged()
    {
        var docker = await ConnectionAsync("Docker", "docker");
        var watcher = Get<ChangeWatcher>();
        var at = DateTimeOffset.Now;

        var first = await watcher.NoteContainersAsync(docker,
            [Row("plex", "c1", "sha256:aaaa", "running", "Up 3 hours"), Row("sonarr", "c2", "sha256:bbbb", "running", "Up 3 hours")],
            at, CancellationToken.None);
        Assert.Empty(first);

        // Thirty seconds on: plex up for 10 seconds, so it restarted; sonarr stopped.
        var second = await watcher.NoteContainersAsync(docker,
            [Row("plex", "c1", "sha256:aaaa", "running", "Up 10 seconds"), Row("sonarr", "c2", "sha256:bbbb", "exited", "Exited (1) 5 seconds ago")],
            at.AddSeconds(30), CancellationToken.None);
        Assert.Equal([ChangeActions.Restarted, ChangeActions.Stopped], second.Select(c => c.Action));

        var feed = await Get<ChangeStore>().QueryAsync(new ChangeQuery(at.AddMinutes(-1), at.AddMinutes(1), [ChangeKinds.Container]));
        Assert.Equal(2, feed.Count);
        Assert.All(feed, c => Assert.Equal(docker.Id, c.ConnectionId));
        Assert.Contains(feed, c => c is { Subject: "sonarr", Title: "sonarr stopped" } && c.Detail.Contains("code 1"));

        // A restart of LabbyTwo: a new watcher, with nothing in memory, compares against the
        // stored baseline — and sees the image plex was moved to while it was away.
        var afterRestart = ActivatorUtilities.CreateInstance<ChangeWatcher>(_services);
        var third = await afterRestart.NoteContainersAsync(docker,
            [Row("plex", "c9", "sha256:cccc", "running", "Up 5 seconds"), Row("sonarr", "c2", "sha256:bbbb", "exited", "Exited (1) 2 minutes ago")],
            at.AddMinutes(5), CancellationToken.None);
        var image = Assert.Single(third);
        Assert.Equal(ChangeActions.Image, image.Action);
        Assert.Equal("plex", image.Name);
    }

    [Fact]
    public async Task ANewVersionIsAnUpdateAndTheFirstStartIsNot()
    {
        var watcher = Get<ChangeWatcher>();

        Assert.Null(await watcher.NoteVersionAsync("v1.3.5", Now, CancellationToken.None));
        Assert.Null(await watcher.NoteVersionAsync("v1.3.5", Now, CancellationToken.None));

        var update = await watcher.NoteVersionAsync("v1.3.6", Now, CancellationToken.None);
        Assert.NotNull(update);
        Assert.Equal("LabbyTwo updated to v1.3.6", update.Title);
        Assert.Equal("It was v1.3.5.", update.Detail);

        // Unstamped builds all say "dev": nothing to compare.
        Assert.Null(await watcher.NoteVersionAsync("dev", Now, CancellationToken.None));
        Assert.Null(await watcher.NoteVersionAsync("v1.4.0", Now, CancellationToken.None));
    }

    [Fact]
    public async Task ALanNameThatMovedIsRecordedWhenTheCheckIsRun()
    {
        var dns = Get<DnsCheck>();
        DnsCheck.Lookup Lookup(string host, bool isPublic, params string[] addresses) =>
            new(host, isPublic, true, TimeSpan.FromMilliseconds(2), addresses, null, ["NAS"]);

        // First check only remembers.
        Assert.Empty(await dns.NoteAnswersAsync([Lookup("nas.lan", false, "192.168.1.10"), Lookup("cdn.example.com", true, "1.1.1.1")], Now, default));
        // The same addresses in another order are the same answer; a public name never counts.
        Assert.Empty(await dns.NoteAnswersAsync([Lookup("nas.lan", false, "192.168.1.10"), Lookup("cdn.example.com", true, "2.2.2.2")], Now, default));

        var moved = Assert.Single(await dns.NoteAnswersAsync([Lookup("nas.lan", false, "192.168.1.20")], Now, default));
        Assert.Equal("nas.lan now resolves to 192.168.1.20", moved.Title);
        Assert.Contains("It was 192.168.1.10", moved.Detail);
        Assert.Equal(ChangeKinds.Dns, moved.Kind);
    }

    // ---------- end to end: an outage becomes an incident ----------

    [Fact]
    public async Task AnOutageIsRecordedAndBecomesAnIncidentThatClosesWhenItRecovers()
    {
        await Get<ChangeWatcher>().StartAsync(CancellationToken.None);
        await Get<IncidentTracker>().StartAsync(CancellationToken.None);
        var nas = await ConnectionAsync("QNAP NAS");
        var plex = await ConnectionAsync("Plex");
        var monitor = Get<HealthMonitor>();
        await monitor.RefreshAsync(nas);
        await monitor.RefreshAsync(plex);

        _stub.Up = false;
        await monitor.RefreshAsync(nas);
        await monitor.RefreshAsync(plex);

        var incidents = Get<IncidentStore>();
        var incident = await EventuallyAsync(async () => (await incidents.RecentAsync(Now.AddDays(-1), 10)).FirstOrDefault(),
            i => i.Members.Count == 2);
        Assert.True(incident.IsOpen);
        Assert.Equal("QNAP NAS and Plex", incident.Title);

        var feed = await Get<ChangeStore>().QueryAsync(new ChangeQuery(Now.AddMinutes(-5), DateTimeOffset.UtcNow, [ChangeKinds.Status]));
        Assert.Contains(feed, c => c.Title == "QNAP NAS went down" && c.Detail == "Connection refused");

        _stub.Up = true;
        await monitor.RefreshAsync(nas);
        await monitor.RefreshAsync(plex);

        var closed = await EventuallyAsync(() => incidents.GetAsync(incident.Id), i => !i.IsOpen);
        Assert.All(closed.Members, m => Assert.True(m.IsRecovered));
        Assert.Contains(await Get<ChangeStore>().QueryAsync(new ChangeQuery(Now.AddMinutes(-5), DateTimeOffset.UtcNow)),
            c => c.Title == "Plex came back");
    }

    [Fact]
    public async Task AnAlertFiringDuringMaintenanceIsRecordedAndMarked()
    {
        await Get<ChangeWatcher>().StartAsync(CancellationToken.None);
        await Get<IncidentTracker>().StartAsync(CancellationToken.None);
        await Get<AppSettingsStore>().SaveAsync(Maintenance.Key, Maintenance.Value(TimeSpan.FromHours(1), DateTimeOffset.Now));

        var nas = await ConnectionAsync("NAS");
        await Get<AlertRuleStore>().SaveAsync(new AlertRule { Metric = "disk_percent", Comparison = Comparison.Above, Threshold = 90 });
        _stub.Disk = 95;
        await Get<HealthMonitor>().RefreshAsync(nas);
        await Get<MetricAlertService>().EvaluateAsync(DateTimeOffset.Now, CancellationToken.None);

        var fired = await EventuallyAsync(async () =>
            (await Get<ChangeStore>().QueryAsync(new ChangeQuery(Now.AddMinutes(-5), DateTimeOffset.UtcNow, [ChangeKinds.Alert]))).FirstOrDefault());
        Assert.Equal(ChangeActions.Firing, fired.Action);
        Assert.StartsWith("Alert fired: NAS", fired.Title);
        Assert.Contains("95", fired.Detail);

        var incident = await EventuallyAsync(async () => (await Get<IncidentStore>().RecentAsync(Now.AddDays(-1), 10)).FirstOrDefault());
        Assert.True(incident.Maintenance);
        Assert.Equal(ChangeKinds.Alert, Assert.Single(incident.Members).Kind);

        // Clearing is recorded too, and ends the incident.
        _stub.Disk = 50;
        await Get<HealthMonitor>().RefreshAsync(nas);
        await Get<MetricAlertService>().EvaluateAsync(DateTimeOffset.Now, CancellationToken.None);
        await EventuallyAsync(() => Get<IncidentStore>().GetAsync(incident.Id), i => !i.IsOpen);
    }

    [Fact]
    public async Task AnAlertMemberWhoseRuleWasDeletedIsClosedByReconciling()
    {
        var nas = await ConnectionAsync("NAS");
        var tracker = Get<IncidentTracker>();
        await tracker.StartAsync(CancellationToken.None);
        var opened = await tracker.ApplyAsync(new IncidentSignal(IncidentMember.AlertKey("gone", nas.Id), ChangeKinds.Alert, nas.Id,
            "NAS · Disk above 90", true, DateTimeOffset.Now));
        Assert.NotNull(opened);

        // An evaluation pass that no longer knows the rule.
        await Get<MetricAlertService>().EvaluateAsync(DateTimeOffset.Now, CancellationToken.None);
        await EventuallyAsync(() => Get<IncidentStore>().GetAsync(opened.Id), i => !i.IsOpen);
    }

    // ---------- the shortcodes ----------

    [Fact]
    public async Task ChangesDrawsTheFeedNewestFirstAndFollowsNewChanges()
    {
        var nas = await ConnectionAsync("QNAP NAS");
        var store = Get<ChangeStore>();
        await store.RecordAsync(new Change(DateTimeOffset.Now.AddHours(-2), ChangeKinds.Container, ChangeActions.Image, nas.Id, "plex",
            "plex is on a new image", "lscr.io/linuxserver/plex:latest"));
        await store.RecordAsync(new Change(DateTimeOffset.Now.AddDays(-3), ChangeKinds.Status, ChangeActions.Down, nas.Id, "", "Too old"));

        await Renderer.RenderMarkdownAsync("{{changes}}");
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("plex is on a new image"));
        Assert.Contains("· QNAP NAS", html);
        Assert.DoesNotContain("Too old", html);

        await store.RecordAsync(new Change(DateTimeOffset.Now, ChangeKinds.Status, ChangeActions.Down, nas.Id, "", "QNAP NAS went down", "<script>alert(1)</script>"));
        html = await Renderer.WaitForAsync(h => h.Contains("QNAP NAS went down", StringComparison.Ordinal));
        // The newest first, and a detail that looks like markup stays text.
        Assert.True(html.IndexOf("QNAP NAS went down", StringComparison.Ordinal) < html.IndexOf("plex is on a new image", StringComparison.Ordinal));
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public async Task ChangesFiltersByKindAndSaysWhenThereIsNothing()
    {
        await Get<ChangeStore>().RecordAsync(new Change(DateTimeOffset.Now, ChangeKinds.Status, ChangeActions.Down, null, "", "Something went down"));

        await Renderer.RenderMarkdownAsync("{{changes: containers last=2h}}");
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Nothing changed"));
        Assert.Contains("Nothing changed in the last 2 hours", html);

        await Renderer.RenderMarkdownAsync("{{changes: gremlins}}");
        Assert.Contains("not a kind of change", WebUtility.HtmlDecode(await Renderer.WaitForAsync("sc-problem")));

        await Renderer.RenderMarkdownAsync("Inline {{changes}} is a mistake.");
        Assert.Contains("sc-problem", await Renderer.WaitForAsync("sc-problem"));
    }

    [Fact]
    public async Task IncidentsListsOpenAndRecentOnesMarked()
    {
        var store = Get<IncidentStore>();
        var start = DateTimeOffset.Now.AddMinutes(-40);
        await store.SaveAsync(new Incident(0, start, start.AddMinutes(12), start.AddMinutes(12), true,
        [
            new IncidentMember("status:nas", ChangeKinds.Status, "nas", "QNAP NAS", start, start.AddMinutes(12)),
            new IncidentMember("status:plex", ChangeKinds.Status, "plex", "Plex", start.AddMinutes(1), start.AddMinutes(10)),
        ]));
        var open = await store.SaveAsync(new Incident(0, DateTimeOffset.Now.AddMinutes(-5), null, DateTimeOffset.Now.AddMinutes(-5), false,
            [new IncidentMember("status:router", ChangeKinds.Status, "router", "Router", DateTimeOffset.Now.AddMinutes(-5), null)]));

        await Renderer.RenderMarkdownAsync("{{incidents}}");
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("QNAP NAS and Plex"));
        Assert.Contains("during maintenance", html);
        Assert.Contains("back after 12m", html);
        Assert.Contains($"incidents#incident-{open.Id}", html);
        Assert.True(html.IndexOf("Router", StringComparison.Ordinal) < html.IndexOf("QNAP NAS and Plex", StringComparison.Ordinal));
        // Nothing recorded around either, and no connections to tie them together: said plainly.
        Assert.Contains("Probably: No obvious cause", html);

        await Renderer.RenderMarkdownAsync("{{incidents: open}}");
        html = WebUtility.HtmlDecode(await Renderer.WaitForAsync(h => h.Contains("Router", StringComparison.Ordinal)
                                                                  && !h.Contains("QNAP NAS and Plex", StringComparison.Ordinal)));
        Assert.Contains(">open<", html);
    }

    [Fact]
    public async Task IncidentsSayWhatProbablyCausedThem()
    {
        var plex = await ConnectionAsync("Plex");
        plex = plex with { Settings = new SettingsBag { ["url"] = "http://plex:32400" } };
        await Get<ConfigStore>().SaveConnectionAsync(plex);

        var down = DateTimeOffset.Now.AddMinutes(-20);
        await Get<ChangeStore>().RecordAsync(new Change(down.AddMinutes(-2), ChangeKinds.Container, ChangeActions.Image, "docker", "plex",
            "plex is on a new image", "lscr.io/linuxserver/plex:latest"));
        await Get<IncidentStore>().SaveAsync(new Incident(0, down, down.AddMinutes(3), down.AddMinutes(3), false,
            [new IncidentMember(IncidentMember.StatusKey(plex.Id), ChangeKinds.Status, plex.Id, "Plex", down, down.AddMinutes(3))]));

        await Renderer.RenderMarkdownAsync("{{incidents}}");
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Probably"));
        Assert.Contains("Probably: Plex went down 2 minutes after it got a new image.", html);
    }

    [Fact]
    public async Task ADownNotificationIsToldTheCauseWhenOneIsFoundInTime()
    {
        var plex = await ConnectionAsync("Plex");
        plex = plex with { Settings = new SettingsBag { ["url"] = "http://plex:32400" } };
        await Get<ConfigStore>().SaveConnectionAsync(plex);
        var router = await ConnectionAsync("Router");

        var now = DateTimeOffset.Now;
        await Get<ChangeStore>().RecordAsync(new Change(now.AddMinutes(-4), ChangeKinds.Container, ChangeActions.Recreated, "docker", "plex",
            "plex was recreated"));

        var causes = Get<ProbableCauses>();
        Assert.Equal("Plex went down 4 minutes after it was recreated.", await causes.ExplainDownAsync(plex, now, CancellationToken.None));
        // Nothing about the router: no cause, rather than somebody else's.
        Assert.Null(await causes.ExplainDownAsync(router, now, CancellationToken.None));

        // And the alerting is handed it when the causes start.
        await causes.StartAsync(CancellationToken.None);
        Assert.NotNull(Get<AlertService>().ExplainDown);
        await causes.StopAsync(CancellationToken.None);
        Assert.Null(Get<AlertService>().ExplainDown);
    }

    [Fact]
    public async Task AnIncidentIsWrittenUpOnceOnATabOfItsOwnAndLinkedBothWays()
    {
        var nas = await ConnectionAsync("QNAP NAS");
        var start = DateTimeOffset.Now.AddHours(-1);
        var incident = await Get<IncidentStore>().SaveAsync(new Incident(0, start, start.AddMinutes(9), start.AddMinutes(9), false,
            [new IncidentMember(IncidentMember.StatusKey(nas.Id), ChangeKinds.Status, nas.Id, "QNAP NAS", start, start.AddMinutes(9))]));
        await Get<ChangeStore>().RecordAsync(new Change(start, ChangeKinds.Status, ChangeActions.Down, nas.Id, "", "QNAP NAS went down",
            "Connection refused"));

        var writeUps = Get<IncidentWriteUps>();
        var link = await writeUps.CreateAsync(incident);

        // A notes tab made for it, found again by its setting rather than its name.
        var tab = Assert.Single(await Get<ConfigStore>().TabsAsync(), t => t.Settings.GetBool(IncidentWriteUps.TabSetting));
        Assert.Equal(TabKinds.Notes, tab.Kind);
        Assert.Equal(tab.Slug, link.TabSlug);
        Assert.Equal($"t/{tab.Slug}?edit={link.NoteId}", link.EditUrl);

        // The note links to the incident...
        var note = Assert.Single(await Get<NotesStore>().ForTabAsync(tab.Id));
        Assert.Contains($"(incidents#incident-{incident.Id})", note.Content);
        Assert.Contains("QNAP NAS went down — Connection refused", note.Content);
        Assert.Contains("{{status: QNAP NAS}}", note.Content);
        Assert.StartsWith("Incident: QNAP NAS — ", note.Title);

        // ...and the incident to the note.
        var stored = await Get<IncidentStore>().GetAsync(incident.Id);
        Assert.Equal(link.NoteId, stored!.WriteUpNoteId);
        var links = await writeUps.LinksAsync([stored]);
        Assert.Equal($"t/{tab.Slug}#note-{link.NoteId}", links[incident.Id].Url);

        // Pressing it again opens the same one; a second incident goes on the same tab.
        Assert.Equal(link.NoteId, (await writeUps.CreateAsync(stored)).NoteId);
        var other = await Get<IncidentStore>().SaveAsync(incident with { Id = 0 });
        await writeUps.CreateAsync(other);
        Assert.Equal(2, (await Get<NotesStore>().ForTabAsync(tab.Id)).Count);
        Assert.Single(await Get<ConfigStore>().TabsAsync(), t => t.Settings.GetBool(IncidentWriteUps.TabSetting));

        // The tracker saving the incident again does not forget its write-up.
        await Get<IncidentStore>().SaveAsync(incident with { LastActivity = start.AddMinutes(10) });
        Assert.Equal(link.NoteId, (await Get<IncidentStore>().GetAsync(incident.Id))!.WriteUpNoteId);

        // A deleted note is no write-up: the incident offers to write one again.
        await Get<NotesStore>().DeleteAsync(link.NoteId);
        Assert.Empty(await writeUps.LinksAsync([stored]));
    }

    [Fact]
    public async Task NoIncidentsSaysSo()
    {
        await Renderer.RenderMarkdownAsync("{{incidents: open}}");
        Assert.Contains("No incident is open", WebUtility.HtmlDecode(await Renderer.WaitForAsync("No incident")));
    }
}
