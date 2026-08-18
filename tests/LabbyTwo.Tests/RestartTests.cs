using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// What survives the process being restarted, which on a box that updates itself is
/// something that happens weekly and unattended.
///
/// The monitor keeps live status in memory, so a restart used to be indistinguishable
/// from a service being seen for the first time: every connection got a status event
/// saying what was already true, and the status page — whose whole promise is "here is
/// every time something changed" — filled up with changes that never happened. These pin
/// the two halves of the fix: the store refuses to record a repeat, and the monitor loads
/// what it knew before deciding anything.
/// </summary>
public sealed class RestartTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly FakeProvider _provider = new();

    /// <summary>A service whose reachability the test decides.</summary>
    private sealed class FakeProvider : IConnectionProvider
    {
        public string Type => "faketest";
        public string DisplayName => "Fake";
        public string Icon => "🧪";
        public string Description => "Test double.";
        public IReadOnlyList<FieldSpec> Fields => [];

        public bool Reachable { get; set; } = true;

        /// <summary>Non-zero for the metered-provider case, where a restart must not re-probe.</summary>
        public TimeSpan MinimumInterval { get; set; } = TimeSpan.Zero;

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(Reachable
                ? ProbeResult.Up(TimeSpan.FromMilliseconds(1), "OK",
                    new Dictionary<string, double> { ["latency_ms"] = 1 })
                : ProbeResult.Down(TimeSpan.FromMilliseconds(1), "unreachable"));
    }

    public RestartTests()
    {
        Directory.CreateDirectory(_directory);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));

        // One failure is enough to be DOWN here. The debounce is worth having and is
        // tested elsewhere; in these it would only mean an extra probe per assertion.
        services.AddTestStorage(_directory, options => options.FailuresBeforeDown = 1);

        services.AddSingleton<IConnectionProvider>(_provider);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        _services = services.BuildServiceProvider();
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public void Dispose() => TestHost.Teardown(_services, _directory);

    /// <summary>
    /// A monitor with nothing in memory, over the database the last one left behind. The
    /// real thing is a new process; the part that matters is the empty dictionary.
    /// </summary>
    private HealthMonitor Restarted() => new(
        Get<ConfigStore>(), Get<Registry>(), Get<HistoryStore>(),
        Microsoft.Extensions.Options.Options.Create(new LabbyOptions
        {
            DatabasePath = Path.Combine(_directory, "test.db"),
            FailuresBeforeDown = 1,
        }),
        Get<ILogger<HealthMonitor>>());

    private async Task<IReadOnlyList<HistoryStore.StatusEvent>> EventsAsync() =>
        await Get<HistoryStore>().RecentEventsAsync(null, 100);

    private async Task<Connection> WatchedAsync()
    {
        var connection = new Connection { Provider = "faketest", Name = "NAS" };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        return connection;
    }

    [Fact]
    public async Task RestartingDoesNotRecordAChangeThatNeverHappened()
    {
        var connection = await WatchedAsync();
        await Get<HealthMonitor>().RefreshAsync(connection);
        Assert.Single(await EventsAsync());

        // Three restarts, each probing a service that has been up the whole time. This is
        // the case that used to write a row every time, so a host that updates itself grew
        // a timeline of state changes out of nothing.
        for (var i = 0; i < 3; i++)
        {
            var restarted = Restarted();
            await restarted.RestoreAsync();
            await restarted.RefreshAsync(connection);
        }

        var events = await EventsAsync();
        Assert.Single(events);
        Assert.True(events[0].IsUp);
    }

    [Fact]
    public async Task ARestartRestoresWhatWasKnownRatherThanCheckingAgain()
    {
        var connection = await WatchedAsync();
        await Get<HealthMonitor>().RefreshAsync(connection);

        var restarted = Restarted();
        Assert.Null(restarted.State(connection.Id));

        await restarted.RestoreAsync();

        var state = restarted.State(connection.Id);
        Assert.NotNull(state);
        Assert.True(state.IsUp);
        // The dashboard shows "checking" for a null, which is what a restart used to mean
        // for everything on it.
        Assert.NotNull(state.ChangedAt);
    }

    [Fact]
    public async Task SomethingThatFailedWhileTheAppWasOffIsReportedAsAChange()
    {
        var connection = await WatchedAsync();
        await Get<HealthMonitor>().RefreshAsync(connection);

        _provider.Reachable = false;

        var restarted = Restarted();
        await restarted.RestoreAsync();

        var changes = new List<HealthMonitor.StatusChange>();
        restarted.StatusChanged += change =>
        {
            changes.Add(change);
            return Task.CompletedTask;
        };

        await restarted.RefreshAsync(connection);

        // Not a first observation — a service that went down. Restoring is what makes the
        // difference between an alert and a shrug.
        var change = Assert.Single(changes);
        Assert.False(change.IsUp);
        Assert.NotNull(change.PreviousDuration);

        var events = await EventsAsync();
        Assert.Equal(2, events.Count);
        Assert.False(events[0].IsUp);
    }

    [Fact]
    public async Task ARestartDoesNotMakeAMeteredConnectionDueAgain()
    {
        // The forecast case: the upstream recomputes hourly and counts requests, so being
        // asked again on every restart is how a restart loop becomes a quota error.
        _provider.MinimumInterval = TimeSpan.FromHours(1);
        var connection = await WatchedAsync();
        await Get<HealthMonitor>().RefreshAsync(connection);

        var restarted = Restarted();
        await restarted.RestoreAsync();

        Assert.False(restarted.IsDue(connection, DateTimeOffset.Now));
        Assert.True(restarted.IsDue(connection, DateTimeOffset.Now.AddHours(2)));
    }

    [Fact]
    public async Task AConnectionWithNoHistoryIsStillRecordedTheFirstTimeItIsSeen()
    {
        // Restoring must not swallow the first observation, which is what every uptime
        // percentage is measured from.
        var connection = await WatchedAsync();

        var monitor = Restarted();
        await monitor.RestoreAsync();
        await monitor.RefreshAsync(connection);

        Assert.Single(await EventsAsync());
    }

    [Fact]
    public async Task TheStoreRefusesToRecordAStateThatIsAlreadyTrue()
    {
        // The safety net under the monitor: whatever a caller believes, the table holds
        // transitions and only transitions.
        var history = Get<HistoryStore>();

        Assert.True(await history.RecordStatusAsync("c1", true, "OK", default));
        Assert.False(await history.RecordStatusAsync("c1", true, "still OK", default));
        Assert.True(await history.RecordStatusAsync("c1", false, "gone", default));
        Assert.False(await history.RecordStatusAsync("c1", false, "still gone", default));
        Assert.True(await history.RecordStatusAsync("c1", true, "back", default));

        // A different connection is a different sequence, not a repeat of this one.
        Assert.True(await history.RecordStatusAsync("c2", true, "OK", default));

        // Three writes out of five attempts, and they read newest first.
        var events = await EventsAsync();
        Assert.Equal(
            [(true, "back"), (false, "gone"), (true, "OK")],
            events.Where(e => e.ConnectionId == "c1").Select(e => (e.IsUp, e.Message)));
        Assert.Single(events, e => e.ConnectionId == "c2");
    }

    [Fact]
    public async Task TheMigrationClearsOutRepeatsAnExistingInstallAlreadyRecorded()
    {
        var db = Get<Db>();
        await db.EnsureSchemaAsync();

        await using (var connection = await db.OpenAsync())
        {
            var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO status_events (connection_id, ts, is_up, message) VALUES ($c, $t, $u, '')";
            var id = insert.Parameters.Add("$c", SqliteType.Text);
            var ts = insert.Parameters.Add("$t", SqliteType.Integer);
            var up = insert.Parameters.Add("$u", SqliteType.Integer);

            // What four restarts on either side of one real outage used to leave behind.
            foreach (var (connectionId, at, isUp) in new[]
                     {
                         ("nas", 100, 1), ("nas", 200, 1), ("nas", 300, 1),
                         ("nas", 400, 0), ("nas", 500, 0),
                         ("nas", 600, 1),
                         ("pi", 150, 1), ("pi", 250, 1),
                     })
            {
                id.Value = connectionId;
                ts.Value = at;
                up.Value = isUp;
                await insert.ExecuteNonQueryAsync();
            }

            // Rewind the stamp so the collapse runs against rows that predate it.
            var rewind = connection.CreateCommand();
            rewind.CommandText = "PRAGMA user_version = 7";
            await rewind.ExecuteNonQueryAsync();
        }

        // A fresh Db over the same file: migrations run once per instance, on first open.
        var reopened = new Db(
            Microsoft.Extensions.Options.Options.Create(new LabbyOptions
            {
                DatabasePath = Path.Combine(_directory, "test.db"),
            }),
            Get<Microsoft.Extensions.Hosting.IHostEnvironment>());
        await reopened.EnsureSchemaAsync();

        var events = await EventsAsync();

        // The transitions survive, in order, with the first of each run kept: the first is
        // where monitoring began and is what the uptime maths measures from.
        Assert.Equal(
            [("nas", 100L, true), ("nas", 400L, false), ("nas", 600L, true), ("pi", 150L, true)],
            events
                .OrderBy(e => e.ConnectionId).ThenBy(e => e.At)
                .Select(e => (e.ConnectionId, e.At.ToUnixTimeSeconds(), e.IsUp)));
    }
}
