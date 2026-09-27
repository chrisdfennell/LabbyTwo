using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace LabbyTwo.Tests;

/// <summary>
/// The evaluator against a real store and monitor, with a provider whose reading the test
/// controls and an alert channel that records instead of sending. The timing is driven by
/// passing "now" in, so a five-minute sustain window costs no wall-clock time.
/// </summary>
public sealed class MetricAlertServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly ServiceProvider _services;
    private readonly FakeProvider _provider = new();
    private readonly RecordingChannel _channel = new();

    /// <summary>A provider whose next reading the test sets.</summary>
    private sealed class FakeProvider : IConnectionProvider
    {
        public string Type => "faketest";
        public string DisplayName => "Fake";
        public string Icon => "🧪";
        public string Description => "Test double.";
        public IReadOnlyList<FieldSpec> Fields => [];
        public IReadOnlyList<MetricSpec> Metrics => [new("disk_percent", "Disk used", "%", 1)];

        public double Value { get; set; }
        public bool Reachable { get; set; } = true;

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(Reachable
                ? ProbeResult.Up(TimeSpan.FromMilliseconds(1), "OK",
                    new Dictionary<string, double> { ["disk_percent"] = Value })
                : ProbeResult.Down(TimeSpan.FromMilliseconds(1), "unreachable"));
    }

    /// <summary>An alert channel that keeps what it was asked to send.</summary>
    private sealed class RecordingChannel : IAlertChannel
    {
        public string Type => "recording";
        public string DisplayName => "Recording channel";
        public string Icon => "📼";
        public string Description => "Test double.";
        public IReadOnlyList<FieldSpec> Fields => [];
        public List<Alert> Sent { get; } = [];

        /// <summary>When set, a send does not finish until the test says so — a slow webhook.</summary>
        public TaskCompletionSource? Hold { get; set; }

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.Zero));

        public Task SendAsync(Connection channel, Alert alert, CancellationToken ct)
        {
            lock (Sent)
                Sent.Add(alert);
            return Hold?.Task ?? Task.CompletedTask;
        }
    }

    public MetricAlertServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "labbytwo-alerts-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddSingleton<IHostEnvironment>(new Env(_directory));
        services.AddSingleton(Options.Create(new LabbyOptions { DatabasePath = Path.Combine(_directory, "t.db") }));

        // Only the doubles: discovery would drag in every real provider, and the point
        // here is a reading the test decides.
        services.AddSingleton<IConnectionProvider>(_provider);
        services.AddSingleton<IConnectionProvider>(_channel);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<Db>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<AppSettingsStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<CapacityForecasts>();
        services.AddSingleton<MetricAlertService>();
        _services = services.BuildServiceProvider();
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private async Task<Connection> SetUpAsync(AlertRule rule, bool withChannel = true)
    {
        var connection = new Connection { Provider = "faketest", Name = "NAS" };
        await Get<ConfigStore>().SaveConnectionAsync(connection);

        if (withChannel)
            await Get<ConfigStore>().SaveConnectionAsync(new Connection { Provider = "recording", Name = "Webhook" });

        await Get<AlertRuleStore>().SaveAsync(rule);
        return connection;
    }

    /// <summary>Sets the reading, probes once, and runs one evaluation pass at the given time.</summary>
    private async Task TickAsync(Connection connection, double value, DateTimeOffset at)
    {
        _provider.Value = value;
        await Get<HealthMonitor>().RefreshAsync(connection);
        await Get<MetricAlertService>().EvaluateAsync(at, CancellationToken.None);
    }

    private static AlertRule DiskAbove(double threshold, int forMinutes = 0, double? clear = null) => new()
    {
        Metric = "disk_percent",
        Comparison = Comparison.Above,
        Threshold = threshold,
        ForMinutes = forMinutes,
        ClearThreshold = clear,
    };

    [Fact]
    public async Task ARuleWithNoSustainWindowFiresOnTheFirstBreach()
    {
        var connection = await SetUpAsync(DiskAbove(90));
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        await TickAsync(connection, 50, start);
        Assert.Empty(_channel.Sent);

        await TickAsync(connection, 95, start.AddSeconds(30));

        var alert = Assert.Single(_channel.Sent);
        Assert.Equal(AlertLevel.Down, alert.Level);
        Assert.Contains("NAS", alert.Title);
        Assert.Contains("Disk used", alert.Title);
    }

    [Fact]
    public async Task ASustainWindowDelaysTheAlertUntilTheConditionHasHeld()
    {
        var connection = await SetUpAsync(DiskAbove(90, forMinutes: 5));
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        await TickAsync(connection, 95, start);
        Assert.Empty(_channel.Sent);

        await TickAsync(connection, 95, start.AddMinutes(4));
        Assert.Empty(_channel.Sent);

        await TickAsync(connection, 95, start.AddMinutes(5));
        Assert.Single(_channel.Sent);
    }

    [Fact]
    public async Task ASpikeThatPassesBeforeTheWindowElapsesNeverAlerts()
    {
        // The nightly backup case: briefly over, back under, no message.
        var connection = await SetUpAsync(DiskAbove(90, forMinutes: 5));
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        await TickAsync(connection, 95, start);
        await TickAsync(connection, 60, start.AddMinutes(2));
        await TickAsync(connection, 95, start.AddMinutes(3));
        await TickAsync(connection, 95, start.AddMinutes(6));

        // The window restarted at minute 3, so minute 6 is only three minutes in.
        Assert.Empty(_channel.Sent);

        await TickAsync(connection, 95, start.AddMinutes(8));
        Assert.Single(_channel.Sent);
    }

    [Fact]
    public async Task ItFiresOnceNotEverySweep()
    {
        var connection = await SetUpAsync(DiskAbove(90));
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        for (var i = 0; i < 5; i++)
            await TickAsync(connection, 95, start.AddMinutes(i));

        Assert.Single(_channel.Sent);
    }

    [Fact]
    public async Task RecoveryIsAnnouncedOnceWhenTheValueComesBack()
    {
        var connection = await SetUpAsync(DiskAbove(90));
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        await TickAsync(connection, 95, start);
        await TickAsync(connection, 50, start.AddMinutes(1));
        await TickAsync(connection, 50, start.AddMinutes(2));

        Assert.Equal(2, _channel.Sent.Count);
        Assert.Equal(AlertLevel.Up, _channel.Sent[1].Level);
        Assert.Contains("back to", _channel.Sent[1].Title);
    }

    [Fact]
    public async Task HysteresisStopsAValueOnTheLineFromAlertingRepeatedly()
    {
        var connection = await SetUpAsync(DiskAbove(90, clear: 85));
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        await TickAsync(connection, 91, start);
        Assert.Single(_channel.Sent);

        // Inside the dead band: still considered a problem, no new message either way.
        await TickAsync(connection, 88, start.AddMinutes(1));
        await TickAsync(connection, 91, start.AddMinutes(2));
        await TickAsync(connection, 87, start.AddMinutes(3));
        Assert.Single(_channel.Sent);

        // Properly recovered.
        await TickAsync(connection, 84, start.AddMinutes(4));
        Assert.Equal(2, _channel.Sent.Count);
        Assert.Equal(AlertLevel.Up, _channel.Sent[1].Level);
    }

    [Fact]
    public async Task ADisabledRuleIsNotEvaluated()
    {
        var connection = await SetUpAsync(DiskAbove(90) with { Enabled = false });
        await TickAsync(connection, 99, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        Assert.Empty(_channel.Sent);
        Assert.Empty(Get<MetricAlertService>().Firing);
    }

    [Fact]
    public async Task AMutedConnectionIsMutedForThresholdsToo()
    {
        var connection = new Connection { Provider = "faketest", Name = "NAS", AlertsEnabled = false };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        await Get<ConfigStore>().SaveConnectionAsync(new Connection { Provider = "recording", Name = "Webhook" });
        await Get<AlertRuleStore>().SaveAsync(DiskAbove(90));

        await TickAsync(connection, 99, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        Assert.Empty(_channel.Sent);
    }

    [Fact]
    public async Task ARuleWithNoConnectionWatchesEveryConnectionReportingTheMetric()
    {
        await Get<ConfigStore>().SaveConnectionAsync(new Connection { Provider = "recording", Name = "Webhook" });
        var first = new Connection { Provider = "faketest", Name = "NAS one" };
        var second = new Connection { Provider = "faketest", Name = "NAS two" };
        await Get<ConfigStore>().SaveConnectionAsync(first);
        await Get<ConfigStore>().SaveConnectionAsync(second);
        await Get<AlertRuleStore>().SaveAsync(DiskAbove(90));

        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        _provider.Value = 95;
        await Get<HealthMonitor>().RefreshAsync(first);
        await Get<HealthMonitor>().RefreshAsync(second);
        await Get<MetricAlertService>().EvaluateAsync(start, CancellationToken.None);

        // One rule, two breaches — each connection alerts on its own.
        Assert.Equal(2, _channel.Sent.Count);
        Assert.Equal(2, Get<MetricAlertService>().Firing.Count);
        Assert.Contains(_channel.Sent, a => a.Title.Contains("NAS one"));
        Assert.Contains(_channel.Sent, a => a.Title.Contains("NAS two"));
    }

    [Fact]
    public async Task AnUnreachableServiceReportsNoMetricAndDoesNotCountTowardsTheWindow()
    {
        var connection = await SetUpAsync(DiskAbove(90, forMinutes: 5));
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        await TickAsync(connection, 95, start);

        // Down: no fresh reading, so nothing to evaluate. Up/down alerting covers this.
        _provider.Reachable = false;
        await Get<HealthMonitor>().RefreshAsync(connection);
        await Get<MetricAlertService>().EvaluateAsync(start.AddMinutes(6), CancellationToken.None);
        Assert.Empty(_channel.Sent);

        // Back with a breaching value: the window starts again rather than counting the
        // minutes it was unreachable as sustained breach.
        _provider.Reachable = true;
        await TickAsync(connection, 95, start.AddMinutes(7));
        Assert.Empty(_channel.Sent);

        await TickAsync(connection, 95, start.AddMinutes(12));
        Assert.Single(_channel.Sent);
    }

    [Fact]
    public async Task DeletingARuleStopsItShowingAsFiring()
    {
        var rule = DiskAbove(90);
        var connection = await SetUpAsync(rule);
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        await TickAsync(connection, 95, start);
        Assert.Single(Get<MetricAlertService>().Firing);

        await Get<AlertRuleStore>().DeleteAsync(rule.Id);
        await Get<MetricAlertService>().EvaluateAsync(start.AddMinutes(1), CancellationToken.None);

        Assert.Empty(Get<MetricAlertService>().Firing);
    }

    [Fact]
    public async Task OneFailedProbeDoesNotMakeAFiringAlertFireAgain()
    {
        var connection = await SetUpAsync(DiskAbove(90));
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        await TickAsync(connection, 95, start);
        Assert.Single(_channel.Sent);

        // A dropped packet: the probe fails and carries no metrics. That is not the rule or
        // the connection going away, so the breach must still be there afterwards.
        _provider.Reachable = false;
        await Get<HealthMonitor>().RefreshAsync(connection);
        await Get<MetricAlertService>().EvaluateAsync(start.AddMinutes(1), CancellationToken.None);
        Assert.Single(Get<MetricAlertService>().Firing);

        _provider.Reachable = true;
        await TickAsync(connection, 95, start.AddMinutes(2));

        // Still the one alert, not a second "firing" for the same breach.
        Assert.Single(_channel.Sent);
    }

    [Fact]
    public async Task ARecoveryAfterAFailedProbeIsStillAnnounced()
    {
        var connection = await SetUpAsync(DiskAbove(90));
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        await TickAsync(connection, 95, start);

        _provider.Reachable = false;
        await Get<HealthMonitor>().RefreshAsync(connection);
        await Get<MetricAlertService>().EvaluateAsync(start.AddMinutes(1), CancellationToken.None);

        _provider.Reachable = true;
        await TickAsync(connection, 50, start.AddMinutes(2));

        Assert.Equal(2, _channel.Sent.Count);
        Assert.Equal(AlertLevel.Up, _channel.Sent[1].Level);
        Assert.Empty(Get<MetricAlertService>().Firing);
    }

    [Fact]
    public async Task TwoPassesAtOnceSendOneAlert()
    {
        // "Test all" finishes several sweeps close together. With the send slow, the second
        // pass used to read the breach before the first had written it, and both sent.
        var connection = await SetUpAsync(DiskAbove(90));
        _provider.Value = 95;
        await Get<HealthMonitor>().RefreshAsync(connection);

        _channel.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var evaluator = Get<MetricAlertService>();

        var first = evaluator.EvaluateAsync(at, CancellationToken.None);
        await WaitUntilAsync(() => _channel.Sent.Count > 0);

        var second = evaluator.EvaluateAsync(at.AddSeconds(1), CancellationToken.None);
        await Task.Delay(100);

        _channel.Hold.SetResult();
        await Task.WhenAll(first, second);

        Assert.Single(_channel.Sent);
    }

    // ---------- Forecast rules ----------

    /// <summary>
    /// Writes a fortnight of hourly history ending now. Straight into the table because the
    /// app only ever records "now", and a forecast needs days of past to fit.
    /// </summary>
    private async Task WriteHistoryAsync(Connection connection, Func<double, double> valueAtDay, double days = 14)
    {
        await using var db = await Get<Db>().OpenAsync();
        var clear = db.CreateCommand();
        clear.CommandText = "DELETE FROM samples WHERE connection_id = $c";
        clear.Parameters.AddWithValue("$c", connection.Id);
        await clear.ExecuteNonQueryAsync();

        var start = DateTimeOffset.UtcNow.AddDays(-days);
        for (var hour = 0; hour <= days * 24; hour++)
        {
            var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO samples (connection_id, metric, ts, value) VALUES ($c, 'disk_percent', $t, $v)";
            insert.Parameters.AddWithValue("$c", connection.Id);
            insert.Parameters.AddWithValue("$t", start.AddHours(hour).ToUnixTimeSeconds());
            insert.Parameters.AddWithValue("$v", valueAtDay(hour / 24.0));
            await insert.ExecuteNonQueryAsync();
        }
    }

    private static AlertRule FullWithin(double days) => new()
    {
        Metric = CapacityMetric.KeyFor("disk_percent"),
        Comparison = Comparison.Below,
        Threshold = days,
        ClearThreshold = days * 1.5,
    };

    private async Task ForecastAndEvaluateAsync()
    {
        await Get<CapacityForecasts>().RunAsync(CancellationToken.None);
        await Get<MetricAlertService>().EvaluateAsync(DateTimeOffset.Now, CancellationToken.None);
    }

    [Fact]
    public async Task AForecastRuleFiresWhenTheVolumeWillFillInsideTheWindow()
    {
        var rule = FullWithin(30) with { ConnectionId = null };
        var connection = await SetUpAsync(rule);
        await Get<AlertRuleStore>().SaveAsync(rule with { ConnectionId = connection.Id });

        // 70% a fortnight ago, a point a day: 84% now, about sixteen days to go.
        await WriteHistoryAsync(connection, day => 70 + day);
        await ForecastAndEvaluateAsync();

        var alert = Assert.Single(_channel.Sent);
        Assert.Equal(AlertLevel.Down, alert.Level);
        Assert.Equal("NAS · Disk used full in about 2 weeks", alert.Title);
        Assert.DoesNotContain("∞", alert.Body);
    }

    [Fact]
    public async Task AForecastRuleWithTooLittleHistoryStaysQuiet()
    {
        var connection = await SetUpAsync(FullWithin(30));

        await WriteHistoryAsync(connection, day => 90 + day * 5, days: 1);
        await ForecastAndEvaluateAsync();

        Assert.Empty(_channel.Sent);
        Assert.Empty(Get<MetricAlertService>().Firing);
    }

    [Fact]
    public async Task AVolumeThatStopsFillingClearsItsForecastAlert()
    {
        var rule = FullWithin(30);
        var connection = new Connection { Provider = "faketest", Name = "NAS" };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        await Get<ConfigStore>().SaveConnectionAsync(new Connection { Provider = "recording", Name = "Webhook" });
        await Get<AlertRuleStore>().SaveAsync(rule with { ConnectionId = connection.Id });

        await WriteHistoryAsync(connection, day => 70 + day);
        await ForecastAndEvaluateAsync();
        Assert.Single(_channel.Sent);

        // Somebody deleted the old backups a fortnight ago and it has held level since.
        await WriteHistoryAsync(connection, _ => 55);
        await ForecastAndEvaluateAsync();

        Assert.Equal(2, _channel.Sent.Count);
        Assert.Equal(AlertLevel.Up, _channel.Sent[1].Level);
        Assert.Contains("no longer close to full", _channel.Sent[1].Title);
        Assert.Contains("not filling", _channel.Sent[1].Body);
    }

    [Fact]
    public async Task AForecastIsNeverWrittenToHistory()
    {
        var connection = await SetUpAsync(FullWithin(30));
        await WriteHistoryAsync(connection, day => 70 + day);
        await ForecastAndEvaluateAsync();

        var recorded = await Get<HistoryStore>().MetricsAsync(connection.Id);
        Assert.Equal(["disk_percent"], recorded);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the send to start.");
            await Task.Delay(10);
        }
    }

    private sealed class Env(string root) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "LabbyTwo.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class NullFileProvider : IFileProvider
    {
        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
        public IFileInfo GetFileInfo(string subpath) => new NotFoundFileInfo(subpath);
        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
    }
}
