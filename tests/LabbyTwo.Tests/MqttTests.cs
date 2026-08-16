using System.Net.Sockets;
using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Services;
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
}
