using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// The weekly summary job against a real database and a channel that records what it was
/// asked to send. A restart is a new job and a new settings store over the same file —
/// which is all a restart leaves behind — so "never twice" is tested against exactly what
/// survives one.
/// </summary>
public sealed class WeeklySummaryJobTests : IDisposable
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Eastern", TimeSpan.FromHours(-5), "Test Eastern", "Test Eastern", "Test Eastern Daylight",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        ]);

    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly RecordingChannel _channel = new();

    private sealed class FakeProvider : IConnectionProvider
    {
        public string Type => "faketest";
        public string DisplayName => "Fake";
        public string Icon => "🧪";
        public string Description => "Test double.";
        public IReadOnlyList<FieldSpec> Fields => [];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.FromMilliseconds(1)));
    }

    private sealed class RecordingChannel : IAlertChannel
    {
        public string Type => "recording";
        public string DisplayName => "Recording channel";
        public string Icon => "📼";
        public string Description => "Test double.";
        public IReadOnlyList<FieldSpec> Fields => [];
        public List<(string Channel, Alert Alert)> Sent { get; } = [];
        public bool Broken { get; set; }

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.Zero));

        public Task SendAsync(Connection channel, Alert alert, CancellationToken ct)
        {
            if (Broken)
                throw new InvalidOperationException("The webhook answered HTTP 502.");
            lock (Sent)
                Sent.Add((channel.Name, alert));
            return Task.CompletedTask;
        }
    }

    public WeeklySummaryJobTests()
    {
        Directory.CreateDirectory(_directory);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);

        services.AddSingleton<IConnectionProvider>(new FakeProvider());
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
        services.AddSingleton<UpdateChecker>();
        services.AddSingleton<WeeklySummaryGatherer>();
        services.AddSingleton<WeeklySummaryJob>();
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private static DateTimeOffset Local(int month, int day, int hour, int minute = 0) =>
        WeeklySchedule.At(new DateOnly(2026, month, day), new TimeOnly(hour, minute), Eastern);

    /// <summary>What a restart leaves: the same database, and nothing in memory.</summary>
    private WeeklySummaryJob Restarted() =>
        new(new AppSettingsStore(Get<Db>()), Get<WeeklySummaryGatherer>(), Get<AlertService>(),
            NullLogger<WeeklySummaryJob>.Instance);

    /// <summary>Monday 09:00, switched on as though from the Settings page on <paramref name="armedAt"/>.</summary>
    private async Task EnableAsync(DateTimeOffset armedAt, params string[] channels)
    {
        var values = new WeeklySchedule(true, DayOfWeek.Monday, new TimeOnly(9, 0), channels).ToSettings();
        values[WeeklySchedule.CoveredKey] = WeeklySchedule.FormatInstant(armedAt);
        await Get<AppSettingsStore>().SaveAsync(values);
    }

    private async Task<Connection> AddAsync(string provider, string name)
    {
        var connection = new Connection { Provider = provider, Name = name };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        return connection;
    }

    private async Task StatusAsync(Connection connection, DateTimeOffset at, bool isUp, string message = "")
    {
        await using var db = await Get<Db>().OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO status_events (connection_id, ts, is_up, message) VALUES ($c, $t, $u, $m)";
        cmd.Parameters.AddWithValue("$c", connection.Id);
        cmd.Parameters.AddWithValue("$t", at.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$u", isUp ? 1 : 0);
        cmd.Parameters.AddWithValue("$m", message);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ItSendsOnceAtTheTimeAndNotAgainAfterARestart()
    {
        await AddAsync("recording", "Discord");
        await EnableAsync(Local(9, 16, 12));

        var job = Get<WeeklySummaryJob>();
        Assert.False(await job.TickAsync(Local(9, 21, 8, 59), Eastern, CancellationToken.None));
        Assert.True(await job.TickAsync(Local(9, 21, 9), Eastern, CancellationToken.None));
        Assert.False(await job.TickAsync(Local(9, 21, 9, 1), Eastern, CancellationToken.None));

        // The process restarts a minute after sending.
        Assert.False(await Restarted().TickAsync(Local(9, 21, 9, 2), Eastern, CancellationToken.None));
        Assert.False(await Restarted().TickAsync(Local(9, 22, 9), Eastern, CancellationToken.None));

        var (_, alert) = Assert.Single(_channel.Sent);
        Assert.Equal(AlertLevel.Info, alert.Level);
        Assert.False(alert.Urgent);
        Assert.StartsWith("Weekly summary · ", alert.Title);
        Assert.NotNull(alert.Markdown);

        // And the next week's goes out as normal.
        Assert.True(await Restarted().TickAsync(Local(9, 28, 9), Eastern, CancellationToken.None));
        Assert.Equal(2, _channel.Sent.Count);
    }

    [Fact]
    public async Task AfterWeeksAwayItCatchesUpOnce()
    {
        await AddAsync("recording", "Discord");
        await EnableAsync(Local(8, 3, 9));

        // Down from August until a Wednesday in September: three Mondays missed.
        var back = Local(9, 23, 14);
        Assert.True(await Restarted().TickAsync(back, Eastern, CancellationToken.None));
        Assert.False(await Restarted().TickAsync(back.AddMinutes(1), Eastern, CancellationToken.None));
        Assert.False(await Restarted().TickAsync(back.AddHours(5), Eastern, CancellationToken.None));

        Assert.Single(_channel.Sent);
    }

    [Fact]
    public async Task SwitchedOnWithoutArmingItWaitsForTheNextTime()
    {
        await AddAsync("recording", "Discord");
        await Get<AppSettingsStore>().SaveAsync(
            new WeeklySchedule(true, DayOfWeek.Monday, new TimeOnly(9, 0), []).ToSettings());

        var job = Get<WeeklySummaryJob>();
        Assert.False(await job.TickAsync(Local(9, 23, 12), Eastern, CancellationToken.None));
        Assert.False(await job.TickAsync(Local(9, 23, 12, 1), Eastern, CancellationToken.None));
        Assert.True(await job.TickAsync(Local(9, 28, 9), Eastern, CancellationToken.None));
    }

    [Fact]
    public async Task QuietHoursHoldItUntilTheyEnd()
    {
        await AddAsync("recording", "Discord");
        await EnableAsync(Local(9, 16, 12));
        await Get<AppSettingsStore>().SaveAsync(new Dictionary<string, string>
        {
            [AlertPolicy.FromKey] = "08:00",
            [AlertPolicy.ToKey] = "10:30",
        });

        var job = Get<WeeklySummaryJob>();
        Assert.False(await job.TickAsync(Local(9, 21, 9), Eastern, CancellationToken.None));
        Assert.False(await job.TickAsync(Local(9, 21, 10, 29), Eastern, CancellationToken.None));
        Assert.True(await job.TickAsync(Local(9, 21, 10, 30), Eastern, CancellationToken.None));
        Assert.Single(_channel.Sent);
    }

    [Fact]
    public async Task WhenEveryChannelFailsItTriesAgainLaterRatherThanCountingItSent()
    {
        await AddAsync("recording", "Discord");
        await EnableAsync(Local(9, 16, 12));
        _channel.Broken = true;

        var job = Get<WeeklySummaryJob>();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.TickAsync(Local(9, 21, 9), Eastern, CancellationToken.None));

        // Not hammered every minute while it is broken…
        _channel.Broken = false;
        Assert.False(await job.TickAsync(Local(9, 21, 9, 5), Eastern, CancellationToken.None));

        // …but sent once it has had time to recover.
        Assert.True(await job.TickAsync(Local(9, 21, 9, 15), Eastern, CancellationToken.None));
        Assert.False(await job.TickAsync(Local(9, 21, 9, 30), Eastern, CancellationToken.None));
        Assert.Single(_channel.Sent);
    }

    [Fact]
    public async Task OnlyTheChosenChannelsHearIt()
    {
        await AddAsync("recording", "Discord");
        var email = await AddAsync("recording", "Email");
        await EnableAsync(Local(9, 16, 12), email.Id);

        Assert.True(await Get<WeeklySummaryJob>().TickAsync(Local(9, 21, 9), Eastern, CancellationToken.None));

        var (channel, _) = Assert.Single(_channel.Sent);
        Assert.Equal("Email", channel);
    }

    [Fact]
    public async Task TheWeekIsReadFromHistory()
    {
        var nas = await AddAsync("faketest", "NAS");
        await AddAsync("recording", "Discord");

        var now = Local(9, 21, 9);
        await StatusAsync(nas, now.AddDays(-30), true);
        await StatusAsync(nas, Local(9, 15, 3, 10), false, "Connection refused");
        await StatusAsync(nas, Local(9, 15, 5, 2), true);

        var data = await Get<WeeklySummaryGatherer>().GatherAsync(now, Eastern, CancellationToken.None);

        var service = Assert.Single(data.Services);
        var outage = Assert.Single(service.Outages);
        Assert.Equal(TimeSpan.FromMinutes(112), outage.Duration);
        Assert.Equal("Connection refused", outage.Message);

        // Alert channels are not services, so they have no uptime to report.
        Assert.DoesNotContain(data.Services, s => s.Name == "Discord");

        Assert.Contains("🔻 Outages\nNAS — 1h 52m, Tue 03:10", WeeklySummary.Build(data).Text);
    }

    [Fact]
    public async Task AnAverageSpansRawReadingsAndHourlySummariesWeightedByCount()
    {
        var from = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 / 3600 * 3600);
        await using (var db = await Get<Db>().OpenAsync())
        {
            var cmd = db.CreateCommand();
            // An old hour of 120 readings averaging 100, and two recent raw readings of 400.
            cmd.CommandText = """
                INSERT INTO samples_hourly (connection_id, metric, hour_ts, min, max, avg, count, last_ts, last_value)
                VALUES ('c', 'latency_ms', $h, 50, 150, 100, 120, $h, 100);
                INSERT INTO samples (connection_id, metric, ts, value) VALUES ('c', 'latency_ms', $r, 400), ('c', 'latency_ms', $r + 30, 400);
                INSERT INTO samples (connection_id, metric, ts, value) VALUES ('c', 'latency_ms', $before, 9999);
                """;
            cmd.Parameters.AddWithValue("$h", from.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$r", from.ToUnixTimeSeconds() + 86400);
            cmd.Parameters.AddWithValue("$before", from.ToUnixTimeSeconds() - 1);
            await cmd.ExecuteNonQueryAsync();
        }

        var aggregate = await Get<HistoryStore>().AggregateAsync("c", "latency_ms", from, from.AddDays(7));

        Assert.NotNull(aggregate);
        Assert.Equal(122, aggregate.Count);
        Assert.Equal((100.0 * 120 + 800) / 122, aggregate.Average, 6);
        Assert.Equal(50, aggregate.Min);
        Assert.Equal(400, aggregate.Max);

        Assert.Null(await Get<HistoryStore>().AggregateAsync("c", "nothing", from, from.AddDays(7)));
    }

    [Fact]
    public async Task AFlappingServiceIsReadOnlyUpToTheLimit()
    {
        var nas = await AddAsync("faketest", "NAS");
        var from = Local(9, 14, 9);
        for (var i = 0; i < 10; i++)
            await StatusAsync(nas, from.AddMinutes(i), i % 2 == 1);

        var window = await Get<HistoryStore>().StatusBetweenAsync(nas.Id, from, from.AddDays(7), 4);

        Assert.True(window.Truncated);
        Assert.Equal(4, window.Events.Count);
        Assert.Null(window.Prior);
        Assert.Equal(from, window.Events[0].At);
    }

    [Fact]
    public async Task WhatChangedIsMeasuredFromTheLastScheduledSummary()
    {
        await AddAsync("faketest", "NAS");
        var old = await AddAsync("faketest", "Old Pi");
        await AddAsync("recording", "Discord");
        var gatherer = Get<WeeklySummaryGatherer>();
        var now = Local(9, 21, 9);

        // The very first summary has nothing to compare with, so says nothing.
        var first = await gatherer.GatherAsync(now, Eastern, CancellationToken.None);
        Assert.Empty(first.Added);
        Assert.Empty(first.Removed);

        await gatherer.RememberAsync(CancellationToken.None);
        await AddAsync("faketest", "Immich");
        await Get<ConfigStore>().DeleteConnectionAsync(old.Id);

        var second = await gatherer.GatherAsync(now.AddDays(7), Eastern, CancellationToken.None);
        Assert.Equal(["Immich"], second.Added);
        Assert.Equal(["Old Pi"], second.Removed);
    }

    [Fact]
    public async Task APreviewIsNotThisWeeksSummary()
    {
        await AddAsync("recording", "Discord");
        await EnableAsync(Local(9, 16, 12));
        var job = Get<WeeklySummaryJob>();

        var preview = await job.PreviewAsync();
        await job.SendAsync(preview, [], CancellationToken.None);
        Assert.Single(_channel.Sent);

        // The scheduled one still goes out.
        Assert.True(await job.TickAsync(Local(9, 21, 9), Eastern, CancellationToken.None));
        Assert.Equal(2, _channel.Sent.Count);
    }
}
