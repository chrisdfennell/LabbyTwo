using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// The in-memory latest readings the cards draw from. What matters is that a card asking
/// never waits on the database, and that what it is given is still the answer the database
/// would have given: the same window, nothing for a connection that has gone.
/// </summary>
public sealed class LatestReadingsTests : IDisposable
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(10);

    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly LatestReadings _latest;

    public LatestReadingsTests()
    {
        _services = TestHost.ReadyHost(_directory);
        _latest = new LatestReadings(
            _services.GetRequiredService<HistoryStore>(),
            _services.GetRequiredService<ConfigStore>(),
            NullLogger<LatestReadings>.Instance);
    }

    public void Dispose()
    {
        _latest.Dispose();
        TestHost.Teardown(_services, _directory);
    }

    private HistoryStore History => _services.GetRequiredService<HistoryStore>();
    private ConfigStore Config => _services.GetRequiredService<ConfigStore>();

    private async Task<Connection> NewConnectionAsync()
    {
        var connection = new Connection { Provider = "http", Name = "Station" };
        await Config.SaveConnectionAsync(connection);
        return connection;
    }

    private async Task InsertAsync(string connectionId, string metric, double value, DateTimeOffset at)
    {
        await using var db = await _services.GetRequiredService<Db>().OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO samples (connection_id, metric, ts, value) VALUES ($id, $m, $ts, $v)";
        cmd.Parameters.AddWithValue("$id", connectionId);
        cmd.Parameters.AddWithValue("$m", metric);
        cmd.Parameters.AddWithValue("$ts", at.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$v", value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task DeleteSamplesAsync(string connectionId)
    {
        await using var db = await _services.GetRequiredService<Db>().OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM samples WHERE connection_id = $id";
        cmd.Parameters.AddWithValue("$id", connectionId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Completes the first time a load for <paramref name="connectionId"/> lands.</summary>
    private Task LoadedAsync(string connectionId)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _latest.Changed += id =>
        {
            if (id == connectionId)
                loaded.TrySetResult();
        };
        return loaded.Task.WaitAsync(LoadTimeout);
    }

    [Fact]
    public async Task ARecordedReadingIsServedStraightFromMemory()
    {
        var connection = await NewConnectionAsync();

        await History.RecordAsync(connection.Id,
            new Dictionary<string, double> { ["temp_outdoor_c"] = 9, ["humidity"] = 70 }, default);

        // Gone from the database; if Get went there for it, it would come back empty.
        await DeleteSamplesAsync(connection.Id);

        var latest = _latest.Get(connection.Id, TimeSpan.FromHours(6));
        Assert.Equal(9, latest["temp_outdoor_c"]);
        Assert.Equal(70, latest["humidity"]);
        Assert.True(latest.ContainsKey("TEMP_OUTDOOR_C"));
    }

    [Fact]
    public async Task ANewerRecordingReplacesTheOneLoadedFromTheDatabase()
    {
        var connection = await NewConnectionAsync();
        await InsertAsync(connection.Id, "temp_outdoor_c", 5, DateTimeOffset.UtcNow.AddMinutes(-10));

        var loaded = LoadedAsync(connection.Id);
        _latest.Get(connection.Id, TimeSpan.FromHours(6));
        await loaded;
        Assert.Equal(5, _latest.Get(connection.Id, TimeSpan.FromHours(6))["temp_outdoor_c"]);

        await History.RecordAsync(connection.Id, new Dictionary<string, double> { ["temp_outdoor_c"] = 11 }, default);
        await DeleteSamplesAsync(connection.Id);

        Assert.Equal(11, _latest.Get(connection.Id, TimeSpan.FromHours(6))["temp_outdoor_c"]);
    }

    [Fact]
    public async Task AReadingOlderThanTheWindowIsNotServed()
    {
        var connection = await NewConnectionAsync();
        await InsertAsync(connection.Id, "download_mbps", 500, DateTimeOffset.UtcNow.AddHours(-9));

        var loaded = LoadedAsync(connection.Id);
        _latest.Get(connection.Id, TimeSpan.FromHours(6));
        await loaded;

        // One load answers every window: it holds the newest value whatever its age, and
        // the window is applied on the way out — as the query applies it.
        Assert.Empty(_latest.Get(connection.Id, TimeSpan.FromHours(6)));
        Assert.Equal(500, _latest.Get(connection.Id, TimeSpan.FromHours(12))["download_mbps"]);
        Assert.Equal(500, _latest.Get(connection.Id, TimeSpan.FromDays(30))["download_mbps"]);
    }

    [Fact]
    public async Task AMissReturnsAtOnceAndLoadsOnceInTheBackground()
    {
        var connection = await NewConnectionAsync();
        await InsertAsync(connection.Id, "disk_percent", 42, DateTimeOffset.UtcNow.AddMinutes(-1));

        var raised = 0;
        _latest.Changed += id =>
        {
            if (id == connection.Id)
                Interlocked.Increment(ref raised);
        };
        var loaded = LoadedAsync(connection.Id);

        // Nothing in memory yet, so nothing is returned — the card shows its empty state
        // rather than waiting — and the value arrives with Changed.
        Assert.Empty(_latest.Get(connection.Id, TimeSpan.FromHours(6)));
        await loaded;
        Assert.Equal(42, _latest.Get(connection.Id, TimeSpan.FromHours(6))["disk_percent"]);

        // Loaded is loaded: asking again is answered from memory, with no second load.
        _latest.Get(connection.Id, TimeSpan.FromHours(6));
        await Task.Delay(200);
        Assert.Equal(1, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task ManyCardsMissingAtOnceShareOneLoad()
    {
        var connection = await NewConnectionAsync();
        await InsertAsync(connection.Id, "latency_ms", 12, DateTimeOffset.UtcNow.AddMinutes(-1));

        var raised = 0;
        _latest.Changed += id =>
        {
            if (id == connection.Id)
                Interlocked.Increment(ref raised);
        };
        var loaded = LoadedAsync(connection.Id);

        // A dashboard of cards for one connection opening together after a restart.
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            Task.Run(() => _latest.Get(connection.Id, TimeSpan.FromHours(6)))));
        await loaded;
        await Task.Delay(200);

        Assert.Equal(1, Volatile.Read(ref raised));
        Assert.Equal(12, _latest.Get(connection.Id, TimeSpan.FromHours(6))["latency_ms"]);
    }

    [Fact]
    public async Task ADeletedConnectionIsNotServed()
    {
        var connection = await NewConnectionAsync();
        await History.RecordAsync(connection.Id, new Dictionary<string, double> { ["disk_percent"] = 80 }, default);
        Assert.NotEmpty(_latest.Get(connection.Id, TimeSpan.FromHours(6)));

        await Config.DeleteConnectionAsync(connection.Id);
        // The store does this itself on the config change; awaited here so the test does
        // not race it.
        await _latest.ForgetDeletedAsync();

        Assert.Empty(_latest.Get(connection.Id, TimeSpan.FromHours(6)));
    }
}
