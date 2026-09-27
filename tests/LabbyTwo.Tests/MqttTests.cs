using System.Net.Sockets;
using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Server;

namespace LabbyTwo.Tests;

/// <summary>
/// MQTT against a real broker, started in this process.
///
/// Worth the trouble because this is the one provider whose shape is different: it holds a
/// subscription open instead of making a request, and nothing about that is exercised by
/// asserting on a parsed payload. A test with a fake pool would confirm the mapping and miss
/// every interesting failure — not connecting, not subscribing, not keeping what arrived.
///
/// MQTTnet ships a broker in the same package as its client, so this needs no container and
/// no network: the only outbound thing here is a loopback socket.
/// </summary>
public sealed class MqttTests : IAsyncLifetime
{
    private MqttServer? _broker;
    private int _port;

    /// <summary>
    /// One per test rather than one shared: the provider owns its broker session, so a
    /// shared instance would carry a live subscription between tests and the "editing it
    /// rebuilds the session" case would be testing the previous test's socket.
    /// </summary>
    private static MqttProvider NewProvider() => new(NullLogger<MqttPool>.Instance);

    public async Task InitializeAsync()
    {
        _port = FreePort();
        _broker = new MqttServerFactory().CreateMqttServer(
            new MqttServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointPort(_port)
                .Build());

        await _broker.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_broker is not null)
        {
            await _broker.StopAsync();
            _broker.Dispose();
        }
    }

    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private Connection Broker(string metrics = "", string topics = "#") => new()
    {
        Id = "mqtt-test",
        Provider = "mqtt",
        Name = "Test broker",
        Settings = new SettingsBag
        {
            ["host"] = "127.0.0.1",
            ["port"] = _port.ToString(),
            ["topics"] = topics,
            ["metrics"] = metrics,
        },
    };

    private async Task PublishAsync(string topic, string payload) =>
        await _broker!.InjectApplicationMessage(new InjectedMqttApplicationMessage(
            new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag()
                .Build()));

    [Fact]
    public async Task ConnectsAndReportsWhatHasArrived()
    {
        await PublishAsync("home/porch/temperature", "4.5");

        var probe = await NewProvider().ProbeAsync(Broker(), CancellationToken.None);

        Assert.True(probe.Ok, probe.Message);
        Assert.True(probe.Metrics!["mqtt_topics"] >= 1);
    }

    [Fact]
    public async Task ABrokerThatIsNotThereIsDownRatherThanAnException()
    {
        var connection = Broker();
        connection.Settings["port"] = FreePort().ToString();   // nothing listening

        var probe = await NewProvider().ProbeAsync(connection, CancellationToken.None);

        Assert.False(probe.Ok);
        Assert.False(string.IsNullOrWhiteSpace(probe.Message));
    }

    /// <summary>
    /// The three payload shapes a house actually publishes: a bare number, a JSON object the
    /// way Zigbee2MQTT does it, and a word from something like Tasmota.
    /// </summary>
    [Fact]
    public async Task MapsBarePayloadsJsonPathsAndOnOff()
    {
        await PublishAsync("home/porch/temperature", "4.5");
        await PublishAsync("zigbee2mqtt/Kitchen", """{"battery":87,"linkquality":120,"nested":{"deep":3}}""");
        await PublishAsync("tele/boiler/POWER", "ON");

        var probe = await NewProvider().ProbeAsync(
            Broker("""
                porch = home/porch/temperature
                battery = zigbee2mqtt/Kitchen:battery
                deep = zigbee2mqtt/Kitchen:nested.deep
                boiler = tele/boiler/POWER
                """),
            CancellationToken.None);

        Assert.True(probe.Ok, probe.Message);
        Assert.Equal(4.5, probe.Metrics!["porch"]);
        Assert.Equal(87, probe.Metrics["battery"]);
        Assert.Equal(3, probe.Metrics["deep"]);
        Assert.Equal(1, probe.Metrics["boiler"]);
    }

    /// <summary>
    /// A typo in a topic is the most likely mistake in that box, and the failure is silent:
    /// the probe is up, the chart is simply empty for ever. So the missing name is said out
    /// loud rather than left to be inferred.
    /// </summary>
    [Fact]
    public async Task AMetricWithNoMessageYetIsNamedRatherThanSilent()
    {
        await PublishAsync("home/porch/temperature", "4.5");

        var probe = await NewProvider().ProbeAsync(
            Broker("typo = home/prch/temperature"), CancellationToken.None);

        Assert.True(probe.Ok);
        Assert.Contains("typo", probe.Message);
        Assert.DoesNotContain("typo", probe.Metrics!.Keys);
    }

    /// <summary>
    /// Changing the broker address must not keep reporting from the old socket. The session
    /// is keyed by what would make it wrong, not by the connection id alone.
    /// </summary>
    [Fact]
    public async Task EditingTheConnectionRebuildsTheSession()
    {
        var connection = Broker();
        var provider = NewProvider();
        Assert.True((await provider.ProbeAsync(connection, CancellationToken.None)).Ok);

        // Same provider, so the same held session — which is the thing under test.
        connection.Settings["port"] = FreePort().ToString();
        var probe = await provider.ProbeAsync(connection, CancellationToken.None);

        Assert.False(probe.Ok);
    }

    // ---------- Losing the broker, and letting go of it ----------

    private static MqttPool NewPool(TimeSpan? backoff = null) =>
        new(NullLogger<MqttPool>.Instance) { ReconnectBackoff = backoff ?? TimeSpan.FromSeconds(30) };

    /// <summary>Asks until the answer is what is wanted, or gives up after a while.</summary>
    private static async Task<MqttPool.Snapshot> UntilAsync(MqttPool pool, Connection connection, bool connected)
    {
        var deadline = DateTimeOffset.Now.AddSeconds(15);
        while (true)
        {
            var snapshot = await pool.SnapshotAsync(connection, CancellationToken.None);
            if (snapshot.Connected == connected || DateTimeOffset.Now > deadline)
                return snapshot;
            await Task.Delay(100);
        }
    }

    private async Task<int> ClientsAsync(int expected)
    {
        // The broker notices a client leaving on its own schedule, so ask for a moment.
        var deadline = DateTimeOffset.Now.AddSeconds(10);
        while (true)
        {
            var count = (await _broker!.GetClientsAsync()).Count;
            if (count == expected || DateTimeOffset.Now > deadline)
                return count;
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// A broker restart used to leave the connection "not connected" until LabbyTwo itself
    /// was restarted: the dead session still matched the settings, so it was kept.
    /// </summary>
    [Fact]
    public async Task ABrokerThatComesBackIsReconnectedAndResubscribed()
    {
        await using var pool = NewPool(TimeSpan.Zero);
        var connection = Broker();
        Assert.True((await pool.SnapshotAsync(connection, CancellationToken.None)).Connected);

        await _broker!.StopAsync();
        var down = await UntilAsync(pool, connection, connected: false);
        Assert.False(down.Connected);
        Assert.False(string.IsNullOrWhiteSpace(down.Error));

        await _broker!.StartAsync();
        Assert.True((await UntilAsync(pool, connection, connected: true)).Connected);

        // Subscribed again, not merely connected: a message published now arrives.
        await PublishAsync("home/after/restart", "1");
        var deadline = DateTimeOffset.Now.AddSeconds(10);
        MqttPool.Snapshot snapshot;
        do
        {
            snapshot = await pool.SnapshotAsync(connection, CancellationToken.None);
            if (snapshot.Topics.ContainsKey("home/after/restart"))
                break;
            await Task.Delay(100);
        } while (DateTimeOffset.Now < deadline);
        Assert.Contains("home/after/restart", snapshot.Topics.Keys);
    }

    /// <summary>
    /// But not on every probe. A broker that is off for the night would otherwise be dialled
    /// every thirty seconds by every probe, each one waiting out a connect.
    /// </summary>
    [Fact]
    public async Task ReconnectingWaitsOutTheBackoff()
    {
        await using var pool = NewPool(TimeSpan.FromMinutes(10));
        var connection = Broker();
        Assert.True((await pool.SnapshotAsync(connection, CancellationToken.None)).Connected);

        await _broker!.StopAsync();
        Assert.False((await UntilAsync(pool, connection, connected: false)).Connected);
        await _broker!.StartAsync();

        Assert.False((await pool.SnapshotAsync(connection, CancellationToken.None)).Connected);
    }

    [Fact]
    public async Task PruningClosesTheSocketsOfConnectionsNoLongerWanted()
    {
        await using var pool = NewPool();
        var kept = Broker();
        var dropped = Broker() with { Id = "mqtt-gone" };
        await pool.SnapshotAsync(kept, CancellationToken.None);
        await pool.SnapshotAsync(dropped, CancellationToken.None);
        Assert.Equal(2, await ClientsAsync(2));

        var closed = await pool.PruneAsync(new HashSet<string> { kept.Id });

        Assert.Equal(1, closed);
        Assert.Equal([kept.Id], pool.Held);
        Assert.Equal(1, await ClientsAsync(1));
    }

    /// <summary>
    /// A probe given up on after the connect worked — here during the pause for retained
    /// messages — used to leave that socket open with nothing holding it.
    /// </summary>
    [Fact]
    public async Task AnOpenThatDoesNotFinishClosesWhatItOpened()
    {
        await using var pool = NewPool();
        using var impatient = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.SnapshotAsync(Broker(), impatient.Token));

        Assert.Empty(pool.Held);
        Assert.Equal(0, await ClientsAsync(0));
    }

    /// <summary>
    /// The pool cannot know a connection was deleted or switched off; the sweep is what
    /// tells it, on the change itself rather than at the next restart.
    /// </summary>
    [Fact]
    public async Task DisablingAConnectionClosesItsSession()
    {
        var directory = TestHost.TempDirectory();
        var services = TestHost.ReadyHost(directory);
        try
        {
            var config = services.GetRequiredService<ConfigStore>();
            var mqtt = Assert.IsType<MqttProvider>(services.GetRequiredService<Registry>().Provider("mqtt"));
            _ = ActivatorUtilities.CreateInstance<MqttSessionSweep>(services);

            var connection = Broker();
            await config.SaveConnectionAsync(connection);
            Assert.True((await mqtt.ProbeAsync(connection, CancellationToken.None)).Ok);
            Assert.Equal(1, await ClientsAsync(1));

            await config.SaveConnectionAsync(connection with { Enabled = false });

            Assert.Equal(0, await ClientsAsync(0));
        }
        finally
        {
            TestHost.Teardown(services, directory);
        }
    }

    [Fact]
    public async Task TheSweepClosesASessionNobodySaved()
    {
        var directory = TestHost.TempDirectory();
        var services = TestHost.ReadyHost(directory);
        try
        {
            var mqtt = Assert.IsType<MqttProvider>(services.GetRequiredService<Registry>().Provider("mqtt"));
            var sweep = ActivatorUtilities.CreateInstance<MqttSessionSweep>(services);

            // What the editor's Test button does with a connection that was never saved.
            Assert.True((await mqtt.ProbeAsync(Broker(), CancellationToken.None)).Ok);

            Assert.Equal(1, await sweep.SweepAsync());
            Assert.Equal(0, await ClientsAsync(0));
        }
        finally
        {
            TestHost.Teardown(services, directory);
        }
    }
}
