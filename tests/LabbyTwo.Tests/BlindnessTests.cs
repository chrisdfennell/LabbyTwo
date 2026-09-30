using System.Net.Sockets;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// LabbyTwo telling "the lab is down" apart from "LabbyTwo cannot see the lab". The night
/// that forced it: a NAS recreated fifteen containers, Docker's DNS inside LabbyTwo's
/// container stopped answering for eleven hours, and every probe's EAI_AGAIN was reported
/// as that service down — paged, opened as one sixteen-hour incident, and used the next
/// morning to roll back two containers that were fine. Everything here is either pure or
/// runs against stub probes; nothing touches a network.
/// </summary>
public sealed class BlindnessTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 21, 0, 0, TimeSpan.Zero);

    private readonly string _directory = TestHost.TempDirectory();
    private readonly Lab _lab = new();
    private readonly RecordingChannel _channel = new();
    private readonly ServiceProvider _services;

    // ---- how a failure is classified -----------------------------------------------------

    private static HttpRequestException Resolver(SocketError code) =>
        new("Resource temporarily unavailable (api.github.com:443)", new SocketException((int)code));

    [Theory]
    [InlineData(SocketError.TryAgain, ProbeFailure.Dns)]
    [InlineData(SocketError.HostNotFound, ProbeFailure.Dns)]
    [InlineData(SocketError.NoData, ProbeFailure.Dns)]
    [InlineData(SocketError.ConnectionRefused, ProbeFailure.Refused)]
    [InlineData(SocketError.HostUnreachable, ProbeFailure.NoRoute)]
    public void Socket_failures_are_classified_by_their_error_code(SocketError code, ProbeFailure expected)
    {
        Assert.Equal(expected, ProbeError.Classify(Resolver(code), "https://api.github.com"));
    }

    [Fact]
    public void A_timeout_is_the_docker_socket_only_when_the_target_is_a_local_socket()
    {
        var timeout = new TaskCanceledException("A task was canceled.", new TimeoutException());

        Assert.Equal(ProbeFailure.DockerSocket, ProbeError.Classify(timeout, "/var/run/docker.sock"));
        Assert.Equal(ProbeFailure.DockerSocket, ProbeError.Classify(timeout, "unix:///var/run/docker.sock"));
        Assert.Equal(ProbeFailure.Timeout, ProbeError.Classify(timeout, "http://gluetun:8000"));
        Assert.Equal(ProbeFailure.Timeout, ProbeError.Classify(timeout, "tcp://docker-proxy:2375"));
    }

    [Fact]
    public void A_database_error_is_classified_by_its_type()
    {
        var locked = new SqliteException("SQLite Error 5: 'database is locked'.", 5);
        Assert.Equal(ProbeFailure.Database, ProbeError.Classify(locked));
        Assert.Equal(ProbeFailure.Other, ProbeError.Classify(new InvalidOperationException("HTTP 500")));
    }

    [Fact]
    public void Eai_again_is_described_as_the_resolver_not_answering()
    {
        var message = ProbeError.Describe(Resolver(SocketError.TryAgain), "https://api.github.com");

        Assert.StartsWith("DNS lookup failed for now at https://api.github.com", message);
        Assert.Contains("not the service", message);
    }

    [Theory]
    [InlineData("No answer within 40 seconds, so this check was abandoned.", ProbeFailure.Abandoned)]
    [InlineData("Resource temporarily unavailable at https://api.github.com", ProbeFailure.Dns)]
    [InlineData("SQLite Error 5: 'database is locked'.", ProbeFailure.Database)]
    [InlineData("Timed out — nothing answered at /var/run/docker.sock.", ProbeFailure.DockerSocket)]
    [InlineData("Connection refused at http://192.168.1.10:8989.", ProbeFailure.Other)]
    [InlineData("HTTP 401 — check the API key.", ProbeFailure.Other)]
    public void A_message_that_did_not_come_through_describe_is_still_recognised(string message, ProbeFailure expected)
    {
        Assert.Equal(expected, ProbeError.ClassifyMessage(message));
    }

    [Fact]
    public async Task A_capture_hears_what_describe_saw_on_its_own_flow_only()
    {
        // Outside any capture, describing is harmless.
        ProbeError.Describe(Resolver(SocketError.TryAgain));

        using (var capture = ProbeError.Capture())
        {
            // Inside a task the probe awaits, as a provider's HTTP call would be.
            await Task.Run(() => ProbeError.Describe(Resolver(SocketError.TryAgain), "https://api.github.com"));
            Assert.Equal(ProbeFailure.Dns, capture.Kind);

            using (var inner = ProbeError.Capture())
            {
                ProbeError.Describe(new SqliteException("locked", 5));
                Assert.Equal(ProbeFailure.Database, inner.Kind);
            }

            // The inner one did not leak into the outer.
            Assert.Equal(ProbeFailure.Dns, capture.Kind);
        }
    }

    [Fact]
    public void Only_failures_that_are_labbytwos_own_count_as_blind()
    {
        Assert.True(ProbeFailure.Dns.IsBlind());
        Assert.True(ProbeFailure.Abandoned.IsBlind());
        Assert.True(ProbeFailure.DockerSocket.IsBlind());
        Assert.True(ProbeFailure.Database.IsBlind());
        Assert.False(ProbeFailure.Timeout.IsBlind());
        Assert.False(ProbeFailure.Refused.IsBlind());
        Assert.False(ProbeFailure.Other.IsBlind());
    }

    // ---- the rules ------------------------------------------------------------------------

    private static readonly TimeSpan Period = TimeSpan.FromSeconds(30);

    private static List<ProbeOutcome> Sweep(int ok, params ProbeFailure[] failures) =>
    [
        .. Enumerable.Range(0, ok).Select(i => new ProbeOutcome($"ok{i}", true, ProbeFailure.None)),
        .. failures.Select((f, i) => new ProbeOutcome($"bad{i}", false, f)),
    ];

    private static ProbeFailure[] Many(ProbeFailure failure, int count) => [.. Enumerable.Repeat(failure, count)];

    [Fact]
    public void Half_the_lab_failing_dns_at_once_is_labbytwo_that_cannot_see()
    {
        var after = BlindnessRules.Assess(Blindness.Clear, Sweep(4, Many(ProbeFailure.Dns, 5)), Period, Period, T0);

        Assert.True(after.Impaired);
        Assert.Equal(BlindCause.Dns, after.Cause);
        Assert.Equal(T0, after.Since);
        Assert.Equal(5, after.Blind);
        Assert.Equal(9, after.Probed);
        Assert.Equal("LabbyTwo can't see the lab right now: DNS lookups are failing inside its container — this is not your services.",
            after.Headline);
    }

    [Fact]
    public void A_few_names_that_do_not_resolve_are_a_few_real_problems()
    {
        // Three is below the minimum however large a share it is…
        Assert.False(BlindnessRules.Assess(Blindness.Clear, Sweep(0, Many(ProbeFailure.Dns, 3)), Period, Period, T0).Impaired);

        // …and four of twelve is below the share.
        Assert.False(BlindnessRules.Assess(Blindness.Clear, Sweep(8, Many(ProbeFailure.Dns, 4)), Period, Period, T0).Impaired);

        // Real outages — refusals and timeouts — never count, however many.
        Assert.False(BlindnessRules.Assess(Blindness.Clear,
            Sweep(0, [.. Many(ProbeFailure.Refused, 6), .. Many(ProbeFailure.Timeout, 6)]), Period, Period, T0).Impaired);
    }

    [Fact]
    public void Mixed_blind_failures_add_up_and_are_named_after_the_commonest()
    {
        var after = BlindnessRules.Assess(Blindness.Clear,
            Sweep(2, [ProbeFailure.Dns, ProbeFailure.Dns, ProbeFailure.Abandoned, ProbeFailure.Abandoned, ProbeFailure.Abandoned]),
            Period, Period, T0);

        Assert.True(after.Impaired);
        Assert.Equal(BlindCause.Abandoned, after.Cause);

        // A tie goes to DNS, which is the root when both happen.
        var tie = BlindnessRules.Assess(Blindness.Clear,
            Sweep(0, [ProbeFailure.Abandoned, ProbeFailure.Abandoned, ProbeFailure.Dns, ProbeFailure.Dns]), Period, Period, T0);
        Assert.Equal(BlindCause.Dns, tie.Cause);
    }

    [Fact]
    public void The_local_docker_socket_not_answering_is_enough_on_its_own()
    {
        var after = BlindnessRules.Assess(Blindness.Clear, Sweep(10, ProbeFailure.DockerSocket), Period, Period, T0);

        Assert.True(after.Impaired);
        Assert.Equal(BlindCause.Docker, after.Cause);
        Assert.Contains("Docker socket", after.Headline);
    }

    [Fact]
    public void A_sweep_that_overran_badly_means_labbytwo_is_struggling()
    {
        var after = BlindnessRules.Assess(Blindness.Clear, Sweep(10), TimeSpan.FromMinutes(6), Period, T0);

        Assert.True(after.Impaired);
        Assert.Equal(BlindCause.Stalled, after.Cause);
        Assert.Contains("6 minutes", after.Reason);

        // Slow but not that slow is not.
        Assert.False(BlindnessRules.Assess(Blindness.Clear, Sweep(10), TimeSpan.FromSeconds(90), Period, T0).Impaired);
        Assert.Equal(TimeSpan.FromMinutes(2), BlindnessRules.StallLimit(Period));
        Assert.Equal(TimeSpan.FromMinutes(20), BlindnessRules.StallLimit(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void It_takes_two_clean_sweeps_to_end_and_keeps_its_start()
    {
        var blind = BlindnessRules.Assess(Blindness.Clear, Sweep(0, Many(ProbeFailure.Dns, 6)), Period, Period, T0);

        var still = BlindnessRules.Assess(blind, Sweep(0, Many(ProbeFailure.Dns, 6)), Period, Period, T0.AddMinutes(5));
        Assert.Equal(T0, still.Since);

        var once = BlindnessRules.Assess(still, Sweep(6), Period, Period, T0.AddMinutes(6));
        Assert.True(once.Impaired);
        Assert.Equal(1, once.ClearSweeps);

        // Blind again before the second clean sweep: the count starts over, the spell goes on.
        var again = BlindnessRules.Assess(once, Sweep(0, Many(ProbeFailure.Dns, 6)), Period, Period, T0.AddMinutes(7));
        Assert.Equal(0, again.ClearSweeps);
        Assert.Equal(T0, again.Since);

        var cleared = BlindnessRules.Assess(
            BlindnessRules.Assess(again, Sweep(6), Period, Period, T0.AddMinutes(8)), Sweep(6), Period, Period, T0.AddMinutes(9));
        Assert.Equal(Blindness.Clear, cleared);
        Assert.Equal(TimeSpan.Zero, cleared.For(T0.AddMinutes(9)));
        Assert.Equal(TimeSpan.FromMinutes(9), again.For(T0.AddMinutes(9)));
    }

    // ---- the monitor, alerting, incidents -----------------------------------------------

    /// <summary>A lab whose every probe answers however the test says, by connection name.</summary>
    private sealed class Lab : IConnectionProvider
    {
        public string Type => "lab";
        public string DisplayName => "Lab";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];

        /// <summary>Connection name → how its probe fails; absent means it answers.</summary>
        public Dictionary<string, ProbeFailure> Failing { get; } = [];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
        {
            if (!Failing.TryGetValue(connection.Name, out var failure))
                return Task.FromResult(ProbeResult.Up(TimeSpan.FromMilliseconds(2), "OK"));

            // Through Describe, the way the providers report — which is what the monitor listens to.
            Exception ex = failure switch
            {
                ProbeFailure.Dns => Resolver(SocketError.TryAgain),
                ProbeFailure.Refused => new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)),
                _ => new InvalidOperationException("HTTP 500"),
            };
            return Task.FromResult(ProbeResult.Down(TimeSpan.FromMilliseconds(2), ProbeError.Describe(ex, $"http://{connection.Name}")));
        }
    }

    private sealed class RecordingChannel : IAlertChannel
    {
        public string Type => "recording";
        public string DisplayName => "Recording";
        public string Icon => "";
        public string Description => "";
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

    public BlindnessTests()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        // One failed probe is down, so a real outage shows in one sweep.
        services.AddTestStorage(_directory, options => options.FailuresBeforeDown = 1);
        services.AddSingleton<IConnectionProvider>(_lab);
        services.AddSingleton<IConnectionProvider>(_channel);
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
        services.AddSingleton<ChangeStore>();
        services.AddSingleton<IncidentStore>();
        services.AddSingleton<IncidentTracker>();
        services.AddSingleton<BlindnessWatcher>();
        services.AddSingleton<BackgroundJobRunner>();
        services.AddSingleton<StorageManager>();
        services.AddSingleton<SelfWatch>();
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        Get<AlertService>().Zone = TimeZoneInfo.Utc;
    }

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private async Task<List<Connection>> LabAsync(params string[] names)
    {
        var connections = new List<Connection>();
        foreach (var name in names)
        {
            var connection = new Connection { Provider = "lab", Name = name };
            await Get<ConfigStore>().SaveConnectionAsync(connection);
            connections.Add(connection);
        }
        await Get<ConfigStore>().SaveConnectionAsync(new Connection { Provider = "recording", Name = "Phone" });
        return connections;
    }

    [Fact]
    public async Task A_sweep_where_labbytwo_cannot_see_turns_nothing_red_and_tells_nobody()
    {
        var monitor = Get<HealthMonitor>();
        await Get<AlertService>().StartAsync(CancellationToken.None);
        var changes = new List<HealthMonitor.StatusChange>();
        monitor.StatusChanged += change =>
        {
            lock (changes)
                changes.Add(change);
            return Task.CompletedTask;
        };
        var transitions = new List<(Blindness Before, Blindness After)>();
        monitor.BlindnessChanged += (before, after) => transitions.Add((before, after));

        var lab = await LabAsync("github", "gluetun", "cloudflare", "weather", "sonarr", "plex");
        await monitor.SweepOnceAsync();
        Assert.All(lab, c => Assert.True(monitor.State(c.Id)!.IsUp));

        // Five of six fail DNS at once.
        foreach (var name in new[] { "github", "gluetun", "cloudflare", "weather", "sonarr" })
            _lab.Failing[name] = ProbeFailure.Dns;
        await monitor.SweepOnceAsync();

        Assert.True(monitor.IsBlind);
        Assert.Equal(BlindCause.Dns, monitor.Blindness.Cause);
        Assert.Equal(BlindCause.Dns, monitor.Status.Blindness.Cause);
        Assert.Single(transitions);
        foreach (var connection in lab)
        {
            var state = monitor.State(connection.Id)!;
            Assert.True(state.IsUp);
            Assert.Equal(0, state.ConsecutiveFailures);
        }
        Assert.Contains("DNS lookup failed", monitor.State(lab[0].Id)!.CantCheck);
        Assert.Null(monitor.State(lab[5].Id)!.CantCheck);
        Assert.Empty(changes);
        Assert.Empty(_channel.Sent);

        // Anything that would alert by another road is held too — but a recovery is not.
        Assert.NotNull(await Get<AlertService>().SuppressedAsync(lab[0], isRecovery: false, CancellationToken.None));
        Assert.Null(await Get<AlertService>().SuppressedAsync(lab[0], isRecovery: true, CancellationToken.None));

        // DNS comes back: two clean sweeps and it is over, with nothing having gone down.
        _lab.Failing.Clear();
        await monitor.SweepOnceAsync();
        Assert.True(monitor.IsBlind);
        await monitor.SweepOnceAsync();
        Assert.False(monitor.IsBlind);
        Assert.Equal(2, transitions.Count);
        Assert.All(lab, c => Assert.Null(monitor.State(c.Id)!.CantCheck));
        Assert.Empty(changes);
        Assert.Null(await Get<AlertService>().SuppressedAsync(lab[0], isRecovery: false, CancellationToken.None));
    }

    [Fact]
    public async Task One_name_that_does_not_resolve_is_still_reported_down()
    {
        var monitor = Get<HealthMonitor>();
        var changes = new List<HealthMonitor.StatusChange>();
        monitor.StatusChanged += change =>
        {
            changes.Add(change);
            return Task.CompletedTask;
        };
        var lab = await LabAsync("github", "gluetun", "cloudflare", "weather", "sonarr", "plex");
        await monitor.SweepOnceAsync();

        _lab.Failing["github"] = ProbeFailure.Dns;
        _lab.Failing["plex"] = ProbeFailure.Refused;
        await monitor.SweepOnceAsync();

        Assert.False(monitor.IsBlind);
        Assert.False(monitor.State(lab[0].Id)!.IsUp);
        Assert.False(monitor.State(lab[5].Id)!.IsUp);
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public async Task A_real_failure_during_a_blind_spell_is_reported_once_sight_returns()
    {
        var monitor = Get<HealthMonitor>();
        var lab = await LabAsync("a", "b", "c", "d", "e");
        await monitor.SweepOnceAsync();

        foreach (var name in new[] { "a", "b", "c", "d" })
            _lab.Failing[name] = ProbeFailure.Dns;
        _lab.Failing["e"] = ProbeFailure.Refused;
        await monitor.SweepOnceAsync();

        // Held with the rest while LabbyTwo cannot tell whose fault anything is.
        Assert.True(monitor.IsBlind);
        Assert.True(monitor.State(lab[4].Id)!.IsUp);

        _lab.Failing.Remove("a");
        _lab.Failing.Remove("b");
        _lab.Failing.Remove("c");
        _lab.Failing.Remove("d");
        await monitor.SweepOnceAsync();
        await monitor.SweepOnceAsync();

        // Sight is back, and what is really down is down.
        Assert.False(monitor.IsBlind);
        Assert.False(monitor.State(lab[4].Id)!.IsUp);
        Assert.True(monitor.State(lab[0].Id)!.IsUp);
    }

    [Fact]
    public async Task No_incident_is_opened_or_grown_while_labbytwo_cannot_see()
    {
        var monitor = Get<HealthMonitor>();
        var tracker = Get<IncidentTracker>();
        await tracker.StartAsync(CancellationToken.None);
        var feed = Get<ChangeStore>();
        var lab = await LabAsync("a", "b", "c", "d", "e");
        await monitor.SweepOnceAsync();

        foreach (var name in new[] { "a", "b", "c", "d" })
            _lab.Failing[name] = ProbeFailure.Dns;
        await monitor.SweepOnceAsync();
        Assert.True(monitor.IsBlind);

        // Something else in the feed saying "down" while blind…
        await feed.RecordAsync(new Change(DateTimeOffset.Now, ChangeKinds.Status, ChangeActions.Down, lab[4].Id, "", "e went down"));

        _lab.Failing.Clear();
        await monitor.SweepOnceAsync();
        await monitor.SweepOnceAsync();
        Assert.False(monitor.IsBlind);

        // …and then a real one after. Only the real one makes an incident.
        await feed.RecordAsync(new Change(DateTimeOffset.Now, ChangeKinds.Status, ChangeActions.Down, lab[3].Id, "", "d went down"));

        Incident? incident = null;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (incident is null && DateTime.UtcNow < until)
        {
            incident = (await Get<IncidentStore>().RecentAsync(DateTimeOffset.Now.AddDays(-1), 10)).FirstOrDefault();
            if (incident is null)
                await Task.Delay(20);
        }

        Assert.NotNull(incident);
        Assert.Equal(lab[3].Id, Assert.Single(incident.Members).ConnectionId);
        await tracker.StopAsync(CancellationToken.None);
    }

    /// <summary>What LabbyTwo's watch on itself sees during a blind spell: a monitor that is otherwise sweeping on time.</summary>
    private static SelfFacts Seeing(DateTimeOffset now, Blindness blindness) => new(
        now, TimeSpan.FromHours(1),
        new MonitorStatus(T0.AddHours(-1), TimeSpan.FromSeconds(30), RestoreOutcome.Completed, null, null, null, 100,
            null, now.AddSeconds(-5), now, TimeSpan.FromSeconds(5), 4, null, [], []) { Blindness = blindness },
        [], WriteHealthStatus.Unknown, null, null, null, new Dictionary<string, string>());

    [Fact]
    public async Task A_long_blind_spell_is_told_once_when_it_starts_and_once_when_it_ends()
    {
        // The notice moved from BlindnessWatcher to SelfWatch, beside LabbyTwo's other news
        // about itself: same rule for the start, and now a notice when it ends too.
        await LabAsync();
        var watch = Get<SelfWatch>();
        watch.Zone = TimeZoneInfo.Utc;
        var spell = new Blindness(true, BlindCause.Dns, T0, 8, 10);

        await watch.PassAsync(T0.AddMinutes(10), Seeing(T0.AddMinutes(10), spell), CancellationToken.None);
        Assert.Empty(_channel.Sent);

        await watch.PassAsync(T0.AddMinutes(16), Seeing(T0.AddMinutes(16), spell), CancellationToken.None);
        await watch.PassAsync(T0.AddHours(3), Seeing(T0.AddHours(3), spell with { Blind = 9 }), CancellationToken.None);
        var sent = Assert.Single(_channel.Sent);
        Assert.Equal("LabbyTwo can't see the lab", sent.Title);
        Assert.Contains("DNS lookups are failing inside its container", sent.Body);
        Assert.Equal("labbytwo:blind", sent.Tag);

        await watch.PassAsync(T0.AddHours(3).AddMinutes(1), Seeing(T0.AddHours(3).AddMinutes(1), Blindness.Clear), CancellationToken.None);
        await watch.PassAsync(T0.AddHours(3).AddMinutes(2), Seeing(T0.AddHours(3).AddMinutes(2), Blindness.Clear), CancellationToken.None);
        Assert.Equal(2, _channel.Sent.Count);
        Assert.Equal(AlertLevel.Up, _channel.Sent[1].Level);
        Assert.Equal("LabbyTwo can see the lab again", _channel.Sent[1].Title);

        // A new spell later is a new notice.
        var next = spell with { Since = T0.AddDays(1) };
        await watch.PassAsync(T0.AddDays(1).AddMinutes(20), Seeing(T0.AddDays(1).AddMinutes(20), next), CancellationToken.None);
        Assert.Equal(3, _channel.Sent.Count);

        var start = BlindnessWatcher.Entry(Blindness.Clear, spell, T0);
        Assert.Equal(ChangeKinds.Monitor, start.Kind);
        Assert.Equal("LabbyTwo can't see the lab: DNS lookups are failing inside its container", start.Title);
        Assert.Null(start.ConnectionId);
        Assert.Null(IncidentTracker.SignalFor(start, maintenance: false));

        var end = BlindnessWatcher.Entry(spell, Blindness.Clear, T0.AddHours(11).AddMinutes(21));
        Assert.Equal("LabbyTwo can see the lab again after 11h 21m", end.Title);
        Assert.True(end.IsRecovery);
    }
}
