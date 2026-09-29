using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// Mute windows and escalation end to end: a real store, monitor, rule evaluator and alert
/// service, a provider whose reading the test sets, and two channels — "Email" and "Push" —
/// that record what they were sent. Time is passed in, and the zone is UTC, so a window and
/// an escalation fifteen minutes out cost no waiting; the clock-change cases are in
/// <see cref="MuteWindowTests"/>.
/// </summary>
public sealed class AlertFollowUpTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly FakeProvider _provider = new();
    private readonly RecordingChannel _channel = new();
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
        public IReadOnlyList<MetricSpec> Metrics => [new("disk_percent", "Disk used", "%", 1), new("temp_c", "Temperature", "°C", 1)];

        public double Disk { get; set; }
        public double Temperature { get; set; } = 20;
        public bool Reachable { get; set; } = true;

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(Reachable
                ? ProbeResult.Up(TimeSpan.FromMilliseconds(1), "OK",
                    new Dictionary<string, double> { ["disk_percent"] = Disk, ["temp_c"] = Temperature })
                : ProbeResult.Down(TimeSpan.FromMilliseconds(1), "unreachable"));
    }

    /// <summary>Records which channel connection was sent what.</summary>
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

    public AlertFollowUpTests()
    {
        _services = Build();
    }

    /// <summary>
    /// A whole app's worth of alerting on the test's database. Called again by the restart
    /// tests, which is the point: everything in memory goes, and only the database is left.
    /// </summary>
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
        var built = services.BuildServiceProvider();
        built.GetRequiredService<AlertService>().Zone = TimeZoneInfo.Utc;
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

    private async Task<AlertRule> SetUpAsync(AlertRule? rule = null)
    {
        _nas = new Connection { Provider = "faketest", Name = "NAS" };
        _email = new Connection { Provider = "recording", Name = "Email" };
        _push = new Connection { Provider = "recording", Name = "Push" };
        await Get<ConfigStore>().SaveConnectionAsync(_nas);
        await Get<ConfigStore>().SaveConnectionAsync(_email);
        await Get<ConfigStore>().SaveConnectionAsync(_push);

        rule ??= DiskAbove90();
        await Get<AlertRuleStore>().SaveAsync(rule);
        return rule;
    }

    private AlertRule DiskAbove90() => new()
    {
        Name = "Disk nearly full",
        Metric = "disk_percent",
        Comparison = Comparison.Above,
        Threshold = 90,
        ChannelId = _email?.Id,
    };

    /// <summary>Sets the reading, probes once, and runs one evaluation pass — with its follow-up — at the given time.</summary>
    private async Task TickAsync(double disk, DateTimeOffset at)
    {
        _provider.Disk = disk;
        await Get<HealthMonitor>().RefreshAsync(_nas);
        await Get<MetricAlertService>().EvaluateAsync(at, CancellationToken.None);
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

    private Task SaveWindowAsync(MuteWindow window) => Get<MuteWindowStore>().SaveAsync(window);

    /// <summary>01:00 to 05:00 every day, muting <paramref name="scope"/>.</summary>
    private static MuteWindow Nightly(MuteScope scope = MuteScope.Everything, params string[] targets) => new()
    {
        Name = "Backup window",
        Days = MuteWindow.Week,
        Start = new TimeOnly(1, 0),
        End = new TimeOnly(5, 0),
        Scope = scope,
        Targets = targets,
    };

    private Task SetDefaultEscalationAsync(EscalationPolicy policy) =>
        Get<AppSettingsStore>().SaveAsync(policy.ToSettings());

    // ---------- Mute windows ----------

    [Fact]
    public async Task AMutedAlertIsNotSentButStillShowsAsFiring()
    {
        var rule = await SetUpAsync();
        await SaveWindowAsync(Nightly());

        await TickAsync(95, Midnight.AddHours(2));

        Assert.Empty(Sent);
        Assert.True(Get<MetricAlertService>().IsFiring(rule.Id));
        Assert.Equal("Backup window", Get<AlertService>().MutedBy(rule.Id, _nas.Id, Midnight.AddHours(2))?.Name);
        Assert.Equal("Backup window", Get<AlertService>().Delivery(FiringAlert.RuleKey(rule.Id, _nas.Id))?.HeldBy);
    }

    [Fact]
    public async Task AWindowOnOneRuleLeavesTheOthersAlone()
    {
        var disk = await SetUpAsync();
        var temp = new AlertRule { Name = "Hot", Metric = "temp_c", Comparison = Comparison.Above, Threshold = 50, ChannelId = _email.Id };
        await Get<AlertRuleStore>().SaveAsync(temp);
        await SaveWindowAsync(Nightly(MuteScope.Rules, disk.Id));

        _provider.Temperature = 70;
        await TickAsync(95, Midnight.AddHours(2));

        var alert = Assert.Single(Sent).Alert;
        Assert.Contains("Temperature", alert.Title);
    }

    [Fact]
    public async Task AWindowOnAConnectionLeavesOtherConnectionsAlone()
    {
        var rule = await SetUpAsync(new AlertRule { Metric = "disk_percent", Comparison = Comparison.Above, Threshold = 90 });
        var other = new Connection { Provider = "faketest", Name = "Backup NAS" };
        await Get<ConfigStore>().SaveConnectionAsync(other);
        await SaveWindowAsync(Nightly(MuteScope.Connections, _nas.Id));

        _provider.Disk = 95;
        await Get<HealthMonitor>().RefreshAsync(_nas);
        await Get<HealthMonitor>().RefreshAsync(other);
        await Get<MetricAlertService>().EvaluateAsync(Midnight.AddHours(2), CancellationToken.None);

        // One rule on every connection, both breaching: only the unmuted one is sent, and
        // both are firing.
        Assert.All(Sent, s => Assert.Contains("Backup NAS", s.Alert.Title));
        Assert.Equal(2, Sent.Count); // every channel, since the rule names none
        Assert.Equal(2, Get<MetricAlertService>().Firing.Count(b => b.RuleId == rule.Id));
    }

    [Fact]
    public async Task AnAlertStillFiringWhenTheWindowEndsIsSentThenOnce()
    {
        await SetUpAsync();
        await SaveWindowAsync(Nightly());

        await TickAsync(95, Midnight.AddHours(2));
        await TickAsync(95, Midnight.AddHours(4).AddMinutes(59));
        Assert.Empty(Sent);

        await TickAsync(95, Midnight.AddHours(5));
        var alert = Assert.Single(SentTo("Email"));
        Assert.Equal(AlertLevel.Down, alert.Level);
        Assert.Contains("Held during “Backup window”", alert.Body);

        await TickAsync(95, Midnight.AddHours(5).AddMinutes(1));
        await TickAsync(95, Midnight.AddHours(6));
        Assert.Single(Sent);
    }

    [Fact]
    public async Task AnAlertThatClearsInsideTheWindowIsNeverMentioned()
    {
        await SetUpAsync();
        await SaveWindowAsync(Nightly());

        await TickAsync(95, Midnight.AddHours(2));
        await TickAsync(50, Midnight.AddHours(3));
        await TickAsync(50, Midnight.AddHours(6));

        Assert.Empty(Sent);
    }

    [Fact]
    public async Task ADownAlertInsideAConnectionWindowIsHeldAndSentWhenItEnds()
    {
        await SetUpAsync();
        await Get<AlertService>().StartAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        // A window open right now, whatever the time the test runs: the whole of today.
        var today = new MuteWindow
        {
            Name = "Rebuild",
            Days = [now.DayOfWeek],
            Start = TimeOnly.FromDateTime(now.UtcDateTime.AddMinutes(-1)),
            End = TimeOnly.FromDateTime(now.UtcDateTime.AddMinutes(-1)),
            Scope = MuteScope.Connections,
            Targets = [_nas.Id],
        };
        await SaveWindowAsync(today);

        await Get<HealthMonitor>().RefreshAsync(_nas);
        _provider.Reachable = false;
        await Get<HealthMonitor>().RefreshAsync(_nas);
        Assert.Empty(Sent);

        await Get<MuteWindowStore>().DeleteAsync(today.Id);
        await Get<AlertService>().FollowUpAsync(DateTimeOffset.Now, CancellationToken.None);

        var alert = Assert.Single(SentTo("Email"));
        Assert.Equal("NAS is down", alert.Title);
        Assert.Contains("Held during “Rebuild”", alert.Body);
    }

    // ---------- Escalation ----------

    [Fact]
    public async Task NothingEscalatesUntilSomebodyAsks()
    {
        await SetUpAsync();

        await TickAsync(95, Midnight);
        await TickAsync(95, Midnight.AddHours(6));

        Assert.Single(Sent);
    }

    [Fact]
    public async Task AnAlertStillFiringIsSentAgainToTheEscalationChannels()
    {
        var rule = await SetUpAsync();
        await Get<AlertRuleStore>().SaveAsync(rule with { EscalateAfterMinutes = 15, EscalateTo = _push.Id });

        await TickAsync(95, Midnight);
        Assert.Single(SentTo("Email"));
        Assert.Empty(SentTo("Push"));

        await TickAsync(95, Midnight.AddMinutes(14));
        Assert.Empty(SentTo("Push"));

        await TickAsync(95, Midnight.AddMinutes(15));
        var escalation = Assert.Single(SentTo("Push"));
        Assert.Equal(AlertLevel.Down, escalation.Level);
        Assert.StartsWith("Still firing · NAS · Disk used is 95.0%", escalation.Title);
        Assert.StartsWith("Not cleared after 15 minutes.", escalation.Body);

        // Once, with no repeat asked for.
        await TickAsync(95, Midnight.AddHours(2));
        Assert.Single(SentTo("Push"));
        Assert.Single(SentTo("Email"));
    }

    [Fact]
    public async Task ARepeatSendsItAgainEachIntervalWhileStillFiring()
    {
        var rule = await SetUpAsync();
        await Get<AlertRuleStore>().SaveAsync(rule with { EscalateAfterMinutes = 15, EscalateTo = _push.Id, EscalateRepeatMinutes = 10 });

        for (var minute = 0; minute <= 40; minute++)
            await TickAsync(95, Midnight.AddMinutes(minute));

        // 15, 25 and 35.
        Assert.Equal(3, SentTo("Push").Count);
        Assert.Equal(3, Get<AlertService>().Delivery(FiringAlert.RuleKey(rule.Id, _nas.Id))!.Escalations);
    }

    [Fact]
    public async Task TheRecoveryOfAnEscalatedAlertGoesToTheEscalationChannelsToo()
    {
        var rule = await SetUpAsync();
        await Get<AlertRuleStore>().SaveAsync(rule with { EscalateAfterMinutes = 15, EscalateTo = _push.Id });

        await TickAsync(95, Midnight);
        await TickAsync(95, Midnight.AddMinutes(15));
        await TickAsync(50, Midnight.AddMinutes(20));

        Assert.Equal(AlertLevel.Up, SentTo("Email").Last().Level);
        Assert.Equal(AlertLevel.Up, SentTo("Push").Last().Level);
        Assert.Null(Get<AlertService>().Delivery(FiringAlert.RuleKey(rule.Id, _nas.Id)));
    }

    [Fact]
    public async Task TheRecoveryOfAnAlertNeverEscalatedStaysOnItsOwnChannel()
    {
        var rule = await SetUpAsync();
        await Get<AlertRuleStore>().SaveAsync(rule with { EscalateAfterMinutes = 15, EscalateTo = _push.Id });

        await TickAsync(95, Midnight);
        await TickAsync(50, Midnight.AddMinutes(5));

        Assert.Equal(2, SentTo("Email").Count);
        Assert.Empty(SentTo("Push"));
    }

    [Fact]
    public async Task TheDefaultCoversARuleThatNeverChoseButNotOneThatOptedOut()
    {
        var rule = await SetUpAsync();
        var quiet = new AlertRule { Name = "Leave it", Metric = "temp_c", Comparison = Comparison.Above, Threshold = 50, ChannelId = _email.Id, EscalateAfterMinutes = 0 };
        await Get<AlertRuleStore>().SaveAsync(quiet);
        await SetDefaultEscalationAsync(new EscalationPolicy(10, [_push.Id], 0));

        _provider.Temperature = 70;
        await TickAsync(95, Midnight);
        await TickAsync(95, Midnight.AddMinutes(10));

        var escalation = Assert.Single(SentTo("Push"));
        Assert.Contains("Disk used", escalation.Title);
    }

    [Fact]
    public async Task ADownAlertEscalatesByTheDefault()
    {
        await SetUpAsync();
        await Get<AlertRuleStore>().DeleteAsync((await Get<AlertRuleStore>().AllAsync()).Single().Id);
        await SetDefaultEscalationAsync(new EscalationPolicy(15, [_push.Id], 0));
        await Get<AlertService>().StartAsync(CancellationToken.None);

        await Get<HealthMonitor>().RefreshAsync(_nas);
        _provider.Reachable = false;
        await Get<HealthMonitor>().RefreshAsync(_nas);
        Assert.Equal(2, Sent.Count); // "NAS is down" to both channels
        lock (_channel.Sent)
            _channel.Sent.Clear();

        await Get<AlertService>().FollowUpAsync(DateTimeOffset.Now.AddMinutes(16), CancellationToken.None);
        var escalation = Assert.Single(Sent);
        Assert.Equal("Push", escalation.Channel);
        Assert.Equal("Still firing · NAS is down", escalation.Alert.Title);

        _provider.Reachable = true;
        await Get<HealthMonitor>().RefreshAsync(_nas);
        Assert.Contains(Sent, s => s.Alert.Level == AlertLevel.Up);
        Assert.Empty(await Get<AlertService>().FiringAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MaintenanceHoldsAnEscalationAndItGoesWhenMaintenanceEnds()
    {
        var rule = await SetUpAsync();
        await Get<AlertRuleStore>().SaveAsync(rule with { EscalateAfterMinutes = 15, EscalateTo = _push.Id });

        await TickAsync(95, Midnight);
        await Get<AppSettingsStore>().SaveAsync(Maintenance.Key, Maintenance.Value(TimeSpan.FromHours(1), Midnight.AddMinutes(10)));

        await TickAsync(95, Midnight.AddMinutes(20));
        await TickAsync(95, Midnight.AddMinutes(69));
        Assert.Empty(SentTo("Push"));

        await TickAsync(95, Midnight.AddMinutes(71));
        Assert.Single(SentTo("Push"));
        await TickAsync(95, Midnight.AddMinutes(80));
        Assert.Single(SentTo("Push"));
    }

    [Fact]
    public async Task QuietHoursInNothingModeHoldAnEscalationButDownOnlyLetsItThrough()
    {
        var rule = await SetUpAsync();
        await Get<AlertRuleStore>().SaveAsync(rule with { EscalateAfterMinutes = 15, EscalateTo = _push.Id });

        // Fired at 22:30, before quiet hours; due at 22:45, inside them.
        var evening = Midnight.AddHours(-1.5);
        await TickAsync(95, evening);
        await Get<AppSettingsStore>().SaveAsync(new Dictionary<string, string>
        {
            [AlertPolicy.FromKey] = "22:40",
            [AlertPolicy.ToKey] = "07:00",
            [AlertPolicy.ModeKey] = AlertPolicy.Nothing,
        });

        await TickAsync(95, evening.AddMinutes(20));
        await TickAsync(95, Midnight.AddHours(6));
        Assert.Empty(SentTo("Push"));

        await TickAsync(95, Midnight.AddHours(7));
        Assert.Single(SentTo("Push"));

        // The same, but "only a service going down": an escalation is one, so it goes.
        await Get<AppSettingsStore>().SaveAsync(AlertPolicy.ModeKey, AlertPolicy.DownOnly);
        await Get<AlertRuleStore>().SaveAsync(rule with { EscalateAfterMinutes = 15, EscalateTo = _push.Id, EscalateRepeatMinutes = 30 });
        await TickAsync(95, Midnight.AddHours(23).AddMinutes(30));
        Assert.Equal(2, SentTo("Push").Count);
    }

    [Fact]
    public async Task AMuteWindowHoldsAnEscalation()
    {
        var rule = await SetUpAsync();
        await Get<AlertRuleStore>().SaveAsync(rule with { EscalateAfterMinutes = 30, EscalateTo = _push.Id });

        await TickAsync(95, Midnight.AddMinutes(45));      // 00:45, delivered
        await SaveWindowAsync(Nightly());                  // 01:00–05:00
        await TickAsync(95, Midnight.AddHours(1).AddMinutes(20));
        await TickAsync(95, Midnight.AddHours(4));
        Assert.Empty(SentTo("Push"));

        await TickAsync(95, Midnight.AddHours(5));
        Assert.Single(SentTo("Push"));
    }

    [Fact]
    public async Task AHeldAlertStartsItsEscalationClockWhenTheWindowEnds()
    {
        var rule = await SetUpAsync();
        await Get<AlertRuleStore>().SaveAsync(rule with { EscalateAfterMinutes = 15, EscalateTo = _push.Id });
        await SaveWindowAsync(Nightly());

        await TickAsync(95, Midnight.AddHours(2));
        await TickAsync(95, Midnight.AddHours(5));        // delivered now
        await TickAsync(95, Midnight.AddHours(5).AddMinutes(14));
        Assert.Empty(SentTo("Push"));

        await TickAsync(95, Midnight.AddHours(5).AddMinutes(15));
        Assert.Single(SentTo("Push"));
    }

    // ---------- Restarts ----------

    [Fact]
    public async Task ARestartDoesNotSendAFiringAlertOrItsEscalationAgain()
    {
        var rule = await SetUpAsync();
        await Get<AlertRuleStore>().SaveAsync(rule with { EscalateAfterMinutes = 15, EscalateTo = _push.Id });
        await TickAsync(95, Midnight);
        await TickAsync(95, Midnight.AddMinutes(15));
        Assert.Equal(2, Sent.Count);

        await RestartAsync();
        await TickAsync(95, Midnight.AddMinutes(16));
        await TickAsync(95, Midnight.AddMinutes(60));

        Assert.Equal(2, Sent.Count);
        Assert.True(Get<MetricAlertService>().IsFiring(rule.Id));
    }

    [Fact]
    public async Task AnEscalationThatCameDueWhileStoppedIsSentOnceOnStarting()
    {
        var rule = await SetUpAsync();
        await Get<AlertRuleStore>().SaveAsync(rule with { EscalateAfterMinutes = 15, EscalateTo = _push.Id, EscalateRepeatMinutes = 30 });
        await TickAsync(95, Midnight);

        await RestartAsync();

        // Stopped for three hours: five repeats' worth, and one escalation for all of it.
        await TickAsync(95, Midnight.AddHours(3));
        await TickAsync(95, Midnight.AddHours(3).AddMinutes(1));
        Assert.Single(SentTo("Push"));
        Assert.Single(SentTo("Email"));

        await TickAsync(95, Midnight.AddHours(3).AddMinutes(30));
        Assert.Equal(2, SentTo("Push").Count);
    }

    [Fact]
    public async Task ARecoveryWhileStoppedIsAnnouncedOnStarting()
    {
        var rule = await SetUpAsync();
        await Get<AlertRuleStore>().SaveAsync(rule with { EscalateAfterMinutes = 15, EscalateTo = _push.Id });
        await TickAsync(95, Midnight);
        await TickAsync(95, Midnight.AddMinutes(15));

        await RestartAsync();
        await TickAsync(50, Midnight.AddMinutes(30));

        Assert.Equal(AlertLevel.Up, SentTo("Email").Last().Level);
        Assert.Equal(AlertLevel.Up, SentTo("Push").Last().Level);
        Assert.Empty(Get<MetricAlertService>().Firing);
    }

    [Fact]
    public async Task AMuteWindowStillOwesItsAlertAfterARestart()
    {
        await SetUpAsync();
        await SaveWindowAsync(Nightly());
        await TickAsync(95, Midnight.AddHours(2));

        await RestartAsync();
        await TickAsync(95, Midnight.AddHours(3));
        Assert.Empty(Sent);

        await TickAsync(95, Midnight.AddHours(5));
        Assert.Single(Sent);
    }

    [Fact]
    public async Task DeletingAFiringRuleForgetsItWithoutARecovery()
    {
        var rule = await SetUpAsync();
        await TickAsync(95, Midnight);

        await Get<AlertRuleStore>().DeleteAsync(rule.Id);
        await Get<MetricAlertService>().EvaluateAsync(Midnight.AddMinutes(1), CancellationToken.None);

        Assert.Single(Sent);
        Assert.Empty(await Get<AlertService>().FiringAsync(CancellationToken.None));
    }

    // ---------- Backups ----------

    [Fact]
    public async Task MuteWindowsAndEscalationTravelInABackup()
    {
        var rule = await SetUpAsync();
        await Get<AlertRuleStore>().SaveAsync(rule with { EscalateAfterMinutes = 15, EscalateTo = _push.Id, EscalateRepeatMinutes = 5 });
        var window = Nightly(MuteScope.Rules, rule.Id);
        await SaveWindowAsync(window);

        var transfer = new ConfigTransfer(Get<ConfigStore>(), Get<AlertRuleStore>(), Get<Registry>(), null, Get<MuteWindowStore>());
        var json = await transfer.ExportAsync(includeSecrets: false);

        await Get<MuteWindowStore>().DeleteAsync(window.Id);
        await Get<AlertRuleStore>().SaveAsync(rule);
        await transfer.ImportAsync(json);

        var restored = Assert.Single(await Get<MuteWindowStore>().AllAsync());
        Assert.Equal(window.Name, restored.Name);
        Assert.Equal(MuteScope.Rules, restored.Scope);
        Assert.Equal([rule.Id], restored.Targets);
        Assert.Equal(7, restored.Days.Count);

        var back = (await Get<AlertRuleStore>().GetAsync(rule.Id))!;
        Assert.Equal(15, back.EscalateAfterMinutes);
        Assert.Equal(_push.Id, back.EscalateTo);
        Assert.Equal(5, back.EscalateRepeatMinutes);
    }
}
