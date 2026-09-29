using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// Self-healing end to end: a real store, monitor, rule evaluator, alert service and change
/// feed, a provider whose reading the test sets, two recording channels, and a fake action
/// that counts how often it was asked to "restart plex". Time is passed in and the zone is
/// UTC, so five minutes' delay and half an hour's cooldown cost no waiting.
/// </summary>
public sealed class RemediationTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly FakeProvider _provider = new();
    private readonly RecordingChannel _channel = new();
    private readonly FakeActions _actions = new();
    private ServiceProvider _services;

    private Connection _nas = null!;
    private Connection _email = null!;
    private Connection _push = null!;

    /// <summary>Monday 2 February 2026, 00:00 UTC.</summary>
    private static readonly DateTimeOffset Midnight = DateTimeOffset.Parse("2026-02-02T00:00:00Z");

    private sealed class FakeProvider : IConnectionProvider
    {
        public string Type => "faketest";
        public string DisplayName => "Fake";
        public string Icon => "🧪";
        public string Description => "Test double.";
        public IReadOnlyList<FieldSpec> Fields => [];
        public IReadOnlyList<MetricSpec> Metrics => [new("disk_percent", "Disk used", "%", 1)];

        public double Disk { get; set; }
        public bool Reachable { get; set; } = true;

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(Reachable
                ? ProbeResult.Up(TimeSpan.FromMilliseconds(1), "OK", new Dictionary<string, double> { ["disk_percent"] = Disk })
                : ProbeResult.Down(TimeSpan.FromMilliseconds(1), "unreachable"));
    }

    private sealed class RecordingChannel : IAlertChannel
    {
        public string Type => "recording";
        public string DisplayName => "Recording channel";
        public string Icon => "📼";
        public string Description => "Test double.";
        public IReadOnlyList<FieldSpec> Fields => [];
        public List<(string Channel, Alert Alert)> Sent { get; } = [];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.Zero));

        public Task SendAsync(Connection channel, Alert alert, CancellationToken ct)
        {
            lock (Sent)
                Sent.Add((channel.Name, alert));
            return Task.CompletedTask;
        }
    }

    /// <summary>Restarts nothing; counts. <see cref="Refuse"/> and <see cref="Fail"/> stand in for the guardrails and a Docker error.</summary>
    private sealed class FakeActions : IRemediationActions
    {
        public List<string> Ran { get; } = [];
        public string? Refuse { get; set; }
        public bool Fail { get; set; }

        public Task<string> DescribeAsync(Remediation remediation, CancellationToken ct) =>
            Task.FromResult($"restart {remediation.Container}");

        public Task<RemediationPlan> PrepareAsync(Remediation remediation, CancellationToken ct)
        {
            var name = remediation.Container;
            if (Refuse is { } why)
                return Task.FromResult(RemediationPlan.Refuse($"restart {name}", $"Restarted {name}", why));
            return Task.FromResult(new RemediationPlan($"restart {name}", $"Restarted {name}", null, _ =>
            {
                lock (Ran)
                    Ran.Add(name);
                return Task.FromResult(Fail ? ActionResult.Failed("no such container") : ActionResult.Done());
            }));
        }
    }

    public RemediationTests()
    {
        _services = Build();
    }

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddTestStorage(_directory, options => options.FailuresBeforeDown = 1);
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddHttpClient();
        services.AddSingleton<IConnectionProvider>(_provider);
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
        services.AddSingleton<TemplateStore>();
        services.AddSingleton<ChangeStore>();
        services.AddSingleton<RemediationStore>();
        services.AddSingleton<IRemediationActions>(_actions);
        services.AddSingleton<RemediationService>();
        var built = services.BuildServiceProvider();
        var alerts = built.GetRequiredService<AlertService>();
        alerts.Zone = TimeZoneInfo.Utc;
        // What StartAsync does, without subscribing to the evaluator — a pass it started
        // would run at the real time, not the test's.
        alerts.Notes = built.GetRequiredService<RemediationService>();
        return built;
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public void Dispose() => TestHost.Teardown(_services, _directory);

    /// <summary>Everything in memory gone, the database kept — a container update.</summary>
    private async Task RestartAsync()
    {
        var connectionString = Get<Db>().ConnectionString;
        await _services.DisposeAsync();
        using (var pooled = new SqliteConnection(connectionString))
            SqliteConnection.ClearPool(pooled);
        _services = Build();
    }

    private async Task<AlertRule> SetUpAsync(Remediation? fix = null, AlertRule? rule = null)
    {
        _nas = new Connection { Provider = "faketest", Name = "NAS" };
        _email = new Connection { Provider = "recording", Name = "Email" };
        _push = new Connection { Provider = "recording", Name = "Push" };
        await Get<ConfigStore>().SaveConnectionAsync(_nas);
        await Get<ConfigStore>().SaveConnectionAsync(_email);
        await Get<ConfigStore>().SaveConnectionAsync(_push);

        rule ??= new AlertRule
        {
            Name = "Disk nearly full",
            Metric = "disk_percent",
            Comparison = Comparison.Above,
            Threshold = 90,
            ChannelId = _email.Id,
        };
        await Get<AlertRuleStore>().SaveAsync(rule);
        await Get<RemediationStore>().SaveAsync((fix ?? RestartPlex()) with { Trigger = Remediation.RuleTrigger(rule.Id) });
        return rule;
    }

    private static Remediation RestartPlex() => new()
    {
        Kind = RemediationKind.RestartContainer,
        TargetConnectionId = "docker",
        Container = "plex",
        AfterMinutes = 5,
        MaxAttempts = 1,
        CooldownMinutes = 30,
        CheckAfterMinutes = 5,
    };

    /// <summary>Sets the reading, probes, runs the alert pass and then the self-healing pass, at the given time.</summary>
    private async Task TickAsync(double disk, DateTimeOffset at)
    {
        _provider.Disk = disk;
        await Get<HealthMonitor>().RefreshAsync(_nas);
        await Get<MetricAlertService>().EvaluateAsync(at, CancellationToken.None);
        await Get<RemediationService>().EvaluateAsync(at, CancellationToken.None);
    }

    private List<(string Channel, Alert Alert)> Sent
    {
        get
        {
            lock (_channel.Sent)
                return [.. _channel.Sent];
        }
    }

    private List<Alert> SentTo(string channel) => [.. Sent.Where(s => s.Channel == channel).Select(s => s.Alert)];

    private async Task<IReadOnlyList<Change>> FeedAsync() =>
        (await Get<ChangeStore>().QueryAsync(new ChangeQuery(Midnight.AddDays(-1), Midnight.AddDays(2), [ChangeKinds.Remediation])))
            .OrderBy(c => c.Id).ToList();

    // ---------- The plan, the run, the verdict ----------

    [Fact]
    public async Task TheAlertSaysWhatLabbyTwoWillTry()
    {
        await SetUpAsync();

        await TickAsync(95, Midnight);

        var alert = Assert.Single(SentTo("Email"));
        Assert.EndsWith("LabbyTwo will try: restart plex in 5 min.", alert.Body);
        Assert.Empty(_actions.Ran);
    }

    [Fact]
    public async Task ItRunsOnceAfterTheDelayAndTheRecoverySaysItWorked()
    {
        await SetUpAsync();

        await TickAsync(95, Midnight);
        await TickAsync(95, Midnight.AddMinutes(4));
        Assert.Empty(_actions.Ran);

        await TickAsync(95, Midnight.AddMinutes(5));
        Assert.Equal(["plex"], _actions.Ran);

        await TickAsync(95, Midnight.AddMinutes(6));
        Assert.Single(_actions.Ran);

        await TickAsync(50, Midnight.AddMinutes(7));
        var recovery = SentTo("Email").Last();
        Assert.Equal(AlertLevel.Up, recovery.Level);
        Assert.EndsWith("Restarted plex at 00:05 — it recovered.", recovery.Body);

        var feed = await FeedAsync();
        Assert.Equal([ChangeActions.Remediated, ChangeActions.Helped], feed.Select(c => c.Action));
        Assert.All(feed, c => Assert.Equal(_nas.Id, c.ConnectionId));
    }

    [Fact]
    public async Task WhenItDoesNotHelpItSaysSoOnceAndStops()
    {
        await SetUpAsync();

        await TickAsync(95, Midnight);
        await TickAsync(95, Midnight.AddMinutes(5));
        await TickAsync(95, Midnight.AddMinutes(9));
        Assert.Single(SentTo("Email"));

        await TickAsync(95, Midnight.AddMinutes(10));
        var notice = SentTo("Email").Last();
        Assert.Equal(AlertLevel.Down, notice.Level);
        Assert.Contains("Restarted plex at 00:05 — it didn't help.", notice.Body);

        // Out of attempts: nothing more, however long it stays broken.
        await TickAsync(95, Midnight.AddMinutes(45));
        await TickAsync(95, Midnight.AddHours(3));
        Assert.Single(_actions.Ran);
        Assert.Equal(2, SentTo("Email").Count);
        Assert.Contains(await FeedAsync(), c => c.Action == ChangeActions.NotHelped);
    }

    [Fact]
    public async Task EscalateSendsItToTheEscalationChannelsAndRecordsTheEscalation()
    {
        var rule = await SetUpAsync(RestartPlex() with { IfNotFixed = IfNotFixed.Escalate });
        await Get<AlertRuleStore>().SaveAsync(rule with { EscalateAfterMinutes = 120, EscalateTo = _push.Id });

        await TickAsync(95, Midnight);
        await TickAsync(95, Midnight.AddMinutes(5));
        await TickAsync(95, Midnight.AddMinutes(10));

        var escalation = Assert.Single(SentTo("Push"));
        Assert.Contains("didn't help", escalation.Body);
        Assert.Equal(1, Get<AlertService>().Delivery(FiringAlert.RuleKey(rule.Id, _nas.Id))?.Escalations);

        // The recovery follows it there, as it would any escalation.
        await TickAsync(50, Midnight.AddMinutes(20));
        Assert.Equal(AlertLevel.Up, SentTo("Push").Last().Level);
    }

    [Fact]
    public async Task SeveralTriesAreSpacedByTheCooldown()
    {
        await SetUpAsync(RestartPlex() with { MaxAttempts = 2, CooldownMinutes = 30 });

        await TickAsync(95, Midnight);
        await TickAsync(95, Midnight.AddMinutes(5));
        await TickAsync(95, Midnight.AddMinutes(10)); // did not help; one try left
        await TickAsync(95, Midnight.AddMinutes(34));
        Assert.Single(_actions.Ran);

        await TickAsync(95, Midnight.AddMinutes(35));
        Assert.Equal(2, _actions.Ran.Count);

        await TickAsync(95, Midnight.AddMinutes(40)); // gives up
        await TickAsync(95, Midnight.AddHours(2));
        Assert.Equal(2, _actions.Ran.Count);
        Assert.Contains(SentTo("Email"), a => a.Body.Contains("didn't help"));
    }

    // ---------- Loops ----------

    [Fact]
    public async Task AnAlertThatComesBackInsideTheCooldownIsTheSameOutage()
    {
        await SetUpAsync();

        await TickAsync(95, Midnight);
        await TickAsync(95, Midnight.AddMinutes(5));  // restarts plex
        await TickAsync(50, Midnight.AddMinutes(6));  // it came back…
        await TickAsync(95, Midnight.AddMinutes(8));  // …and the restart knocked it over again
        await TickAsync(95, Midnight.AddMinutes(14));
        await TickAsync(95, Midnight.AddMinutes(25));

        Assert.Single(_actions.Ran);
        var refire = SentTo("Email").Last(a => a.Level == AlertLevel.Down);
        Assert.Contains("LabbyTwo already tried to restart plex at 00:05 and will not try again for this outage.", refire.Body);
    }

    [Fact]
    public async Task AnAlertAfterTheCooldownIsANewOutage()
    {
        await SetUpAsync();

        await TickAsync(95, Midnight);
        await TickAsync(95, Midnight.AddMinutes(5));
        await TickAsync(50, Midnight.AddMinutes(6));

        await TickAsync(95, Midnight.AddMinutes(60));
        Assert.EndsWith("LabbyTwo will try: restart plex in 5 min.", SentTo("Email").Last().Body);
        await TickAsync(95, Midnight.AddMinutes(65));

        Assert.Equal(2, _actions.Ran.Count);
    }

    [Fact]
    public async Task TheAttemptIsRememberedAcrossARestart()
    {
        await SetUpAsync();

        await TickAsync(95, Midnight);
        await TickAsync(95, Midnight.AddMinutes(5));
        Assert.Single(_actions.Ran);

        await RestartAsync();
        await TickAsync(95, Midnight.AddMinutes(10));
        await TickAsync(95, Midnight.AddMinutes(50));

        Assert.Single(_actions.Ran);
        Assert.Contains(await FeedAsync(), c => c.Action == ChangeActions.NotHelped);
    }

    // ---------- Guardrails ----------

    [Fact]
    public async Task NothingRunsDuringMaintenanceAndTheFeedSaysWhyOnce()
    {
        await SetUpAsync();
        await Get<AppSettingsStore>().SaveAsync(Maintenance.Key, Maintenance.Indefinite);

        await TickAsync(95, Midnight);
        await TickAsync(95, Midnight.AddMinutes(5));
        await TickAsync(95, Midnight.AddMinutes(6));
        await TickAsync(95, Midnight.AddMinutes(7));

        Assert.Empty(_actions.Ran);
        var skipped = Assert.Single(await FeedAsync());
        Assert.Equal(ChangeActions.Skipped, skipped.Action);
        Assert.Contains("silenced", skipped.Detail);

        // Maintenance over, still broken: now it runs.
        await Get<AppSettingsStore>().SaveAsync(Maintenance.Key, Maintenance.Cleared);
        await TickAsync(95, Midnight.AddMinutes(8));
        Assert.Single(_actions.Ran);
    }

    [Fact]
    public async Task TheKillSwitchStopsEverythingIncludingThePromise()
    {
        await SetUpAsync();
        await Get<AppSettingsStore>().SaveAsync(new RemediationSettings(false, 5).ToSettings());

        await TickAsync(95, Midnight);
        await TickAsync(95, Midnight.AddMinutes(10));

        Assert.Empty(_actions.Ran);
        Assert.DoesNotContain("LabbyTwo will try", Assert.Single(SentTo("Email")).Body);
    }

    [Fact]
    public async Task TheHourlyCapCoversTheWholeInstall()
    {
        await SetUpAsync();
        var second = new AlertRule { Name = "Disk very full", Metric = "disk_percent", Comparison = Comparison.Above, Threshold = 80 };
        await Get<AlertRuleStore>().SaveAsync(second);
        await Get<RemediationStore>().SaveAsync(RestartPlex() with { Trigger = Remediation.RuleTrigger(second.Id), Container = "sonarr" });
        await Get<AppSettingsStore>().SaveAsync(new RemediationSettings(true, 1).ToSettings());

        await TickAsync(95, Midnight);
        await TickAsync(95, Midnight.AddMinutes(5));

        Assert.Single(_actions.Ran);
        Assert.Contains(await FeedAsync(), c => c.Action == ChangeActions.Skipped && c.Detail.Contains("in the last hour"));
    }

    [Fact]
    public async Task ARefusedTargetIsNotAnAttempt()
    {
        await SetUpAsync();
        _actions.Refuse = "“labbytwo” is the container LabbyTwo itself runs in, which it never restarts by itself.";

        await TickAsync(95, Midnight);
        await TickAsync(95, Midnight.AddMinutes(5));
        await TickAsync(95, Midnight.AddMinutes(6));

        var skipped = Assert.Single(await FeedAsync());
        Assert.Equal(ChangeActions.Skipped, skipped.Action);
        Assert.Contains("itself runs in", skipped.Detail);

        // The refusal lifted — somebody fixed the setting — and it has its attempt still.
        _actions.Refuse = null;
        await TickAsync(95, Midnight.AddMinutes(7));
        Assert.Single(_actions.Ran);
    }

    [Fact]
    public async Task AnActionThatFailsUsesItsAttemptAndSaysSo()
    {
        await SetUpAsync();
        _actions.Fail = true;

        await TickAsync(95, Midnight);
        await TickAsync(95, Midnight.AddMinutes(5));
        await TickAsync(95, Midnight.AddMinutes(30));

        Assert.Single(_actions.Ran);
        Assert.Contains(SentTo("Email"), a => a.Body.Contains("LabbyTwo tried to restart plex at 00:05, but it failed: no such container."));
        Assert.Contains(await FeedAsync(), c => c.Action == ChangeActions.Failed);
    }

    [Fact]
    public async Task ADownConnectionIsRestartedAndItsRecoverySaysSo()
    {
        await SetUpAsync();
        await Get<RemediationStore>().SaveAsync(RestartPlex() with { Trigger = Remediation.DownTrigger(_nas.Id), AfterMinutes = 0 });
        await Get<AlertService>().StartAsync(CancellationToken.None);

        await Get<HealthMonitor>().RefreshAsync(_nas);
        _provider.Reachable = false;
        await Get<HealthMonitor>().RefreshAsync(_nas);

        var down = SentTo("Email").Single(a => a.Title == "NAS is down");
        Assert.EndsWith("LabbyTwo will try: restart plex now.", down.Body);

        await Get<RemediationService>().EvaluateAsync(DateTimeOffset.Now, CancellationToken.None);
        Assert.Single(_actions.Ran);

        _provider.Reachable = true;
        await Get<HealthMonitor>().RefreshAsync(_nas);
        var back = SentTo("Email").Single(a => a.Title == "NAS is back");
        Assert.Contains("— it recovered.", back.Body);
    }

    // ---------- The pure parts ----------

    private static ContainerRow Row(string name, string id = "", string? service = null) =>
        new(id.Length > 0 ? id : name + "0123456789abcdef", name, "img", "sha256:1", "running", "Up", DateTimeOffset.UnixEpoch, [],
            service is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [ContainerRow.ServiceLabel] = service });

    [Fact]
    public void LabbyTwosOwnContainerIsNeverRestartedEvenWithTheOptIn()
    {
        var self = Row("labbytwo", id: "abcdef012345" + new string('0', 52));
        Assert.NotNull(RemediationGuards.ContainerRefusal(self, "abcdef012345", [], allowProtected: true));
        Assert.NotNull(RemediationGuards.ContainerRefusal(Row("labbytwo"), "labbytwo", [], allowProtected: true));
    }

    [Fact]
    public void AProtectedContainerNeedsTheOptIn()
    {
        var tunnel = Row("tunnel-cloudflared-1", service: "cloudflared");
        var list = RemediationGuards.ProtectedList([
            new Tab { Kind = RemediationGuards.ContainersTabKind, Settings = new SettingsBag { ["protected"] = "cloudflared\ntraefik" } },
            new Tab { Kind = "grid", Settings = new SettingsBag { ["protected"] = "plex" } },
        ]);

        Assert.Equal(["cloudflared", "traefik"], list);
        Assert.Contains("protected", RemediationGuards.ContainerRefusal(tunnel, "somewhere-else", list, allowProtected: false));
        Assert.Null(RemediationGuards.ContainerRefusal(tunnel, "somewhere-else", list, allowProtected: true));
        Assert.Null(RemediationGuards.ContainerRefusal(Row("plex"), "somewhere-else", list, allowProtected: false));
    }

    [Fact]
    public void ADangerousActionNeedsTheOptInAndOneThatAsksForInputNeverRuns()
    {
        var shutdown = new ProviderAction("shutdown", "Shut down") { Dangerous = true };
        var disable = new ProviderAction("disable", "Disable blocking") { Fields = [new FieldSpec("minutes", "Minutes", FieldKind.Number, Required: true)] };

        Assert.NotNull(RemediationGuards.ActionRefusal(shutdown, "NAS", allowProtected: false));
        Assert.Null(RemediationGuards.ActionRefusal(shutdown, "NAS", allowProtected: true));
        Assert.NotNull(RemediationGuards.ActionRefusal(disable, "Pi-hole", allowProtected: true));
    }

    [Fact]
    public void AContainerIsFoundByNameOrComposeService()
    {
        var rows = new[] { Row("media-plex-1", service: "plex"), Row("sonarr") };
        Assert.Equal("media-plex-1", RemediationGuards.Find(rows, "plex")?.Name);
        Assert.Equal("sonarr", RemediationGuards.Find(rows, "/Sonarr")?.Name);
        Assert.Null(RemediationGuards.Find(rows, "radarr"));
    }

    [Theory]
    [InlineData("status:abc", "down:abc")]
    [InlineData("rule:r1:abc", "rule:r1")]
    [InlineData("nonsense", "")]
    public void AnAlertKeyNamesItsTrigger(string key, string trigger) =>
        Assert.Equal(trigger, RemediationService.TriggerOf(key));

    [Fact]
    public void TheSettingsReadBackAsWritten()
    {
        var bag = new SettingsBag(new RemediationSettings(false, 3).ToSettings());
        Assert.Equal(new RemediationSettings(false, 3), RemediationSettings.From(bag));
        Assert.Equal(new RemediationSettings(true, RemediationSettings.DefaultMaxPerHour), RemediationSettings.From(new SettingsBag()));
    }
}
