using System.Collections.Concurrent;
using System.Net.Sockets;
using LabbyTwo.Core;
using LabbyTwo.Services;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using static LabbyTwo.Tests.HomeAssistantPlanTests;

namespace LabbyTwo.Tests;

/// <summary>
/// The publisher against a real broker, started in this process — the same arrangement as
/// <see cref="MqttTests"/>. What matters about the Home Assistant link is what lands on the
/// broker and when: retained configs, deltas only, the will and the birth, the republish
/// when HA restarts, the leftovers cleared, the buttons pressed. None of that shows up in
/// a test of the plan alone.
/// </summary>
public sealed class HomeAssistantBrokerTests : IAsyncLifetime
{
    private MqttServer? _broker;
    private int _port;
    private readonly List<IMqttClient> _observers = [];

    /// <summary>The app's side, made up: settings, a lab, and a record of the buttons pressed.</summary>
    private sealed class FakeSource : IHomeAssistantSource
    {
        public HaSettings Settings { get; set; } = HaSettings.Off;
        public HaLab Lab { get; set; } = HaLab.Empty;
        public int LabCalls;
        public ConcurrentQueue<(HaCommand Command, bool Retained)> Commands { get; } = new();

        public Task<HaSettings> SettingsAsync(CancellationToken ct) => Task.FromResult(Settings);

        public Task<HaLab> LabAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref LabCalls);
            return Task.FromResult(Lab);
        }

        public Task<string> CommandAsync(HaCommand command, HaSettings settings, bool retained, CancellationToken ct)
        {
            Commands.Enqueue((command, retained));
            return Task.FromResult($"pressed {command.Key}");
        }
    }

    /// <summary>Every formatted log line and exception, for the test that a password never reaches one.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(formatter(state, exception) + " " + exception);
    }

    private sealed record Seen(string Topic, string Payload, bool Retain);

    public async Task InitializeAsync()
    {
        _port = FreePort();
        _broker = new MqttServerFactory().CreateMqttServer(
            new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(_port).Build());
        await _broker.StartAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var observer in _observers)
            observer.Dispose();
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

    private FakeSource Source(HaLab? lab = null) => new()
    {
        Settings = On(_port),
        Lab = lab ?? Lab(Conn("nas1", "NAS"), Conn("plex1", "Plex")),
    };

    private static HomeAssistantPublisher Publisher(IHomeAssistantSource source, ILogger? log = null) => new(source, log ?? new CapturingLogger())
    {
        Debounce = TimeSpan.FromMilliseconds(100),
        MinBackoff = TimeSpan.FromMilliseconds(200),
        MaxBackoff = TimeSpan.FromSeconds(1),
        RetainedWait = TimeSpan.FromMilliseconds(300),
        RepeatGuard = TimeSpan.FromSeconds(1),
    };

    /// <summary>A second client watching the broker, the way Home Assistant would.</summary>
    private async Task<ConcurrentQueue<Seen>> WatchAsync(string filter)
    {
        var seen = new ConcurrentQueue<Seen>();
        var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += args =>
        {
            seen.Enqueue(new Seen(args.ApplicationMessage.Topic, args.ApplicationMessage.ConvertPayloadToString() ?? "", args.ApplicationMessage.Retain));
            return Task.CompletedTask;
        };
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", _port).WithClientId("observer-" + Guid.NewGuid().ToString("n")[..8]).Build());
        await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(f => f.WithTopic(filter)).Build());
        _observers.Add(client);
        return seen;
    }

    private async Task SendAsync(string topic, string payload, bool retain = false)
    {
        var client = _observers.FirstOrDefault() ?? throw new InvalidOperationException("Watch something first.");
        await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }

    /// <summary>Runs the publisher for the length of a test, and stops it cleanly at the end.</summary>
    private sealed class Running(HomeAssistantPublisher publisher) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Task? _loop;
        public HomeAssistantPublisher Publisher => publisher;

        public Running Start()
        {
            _loop = publisher.RunAsync(_stop.Token);
            return this;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            if (_loop is not null)
                await _loop;
            await publisher.DisposeAsync();
        }
    }

    private static async Task<Running> StartedAsync(HomeAssistantPublisher publisher)
    {
        var running = new Running(publisher).Start();
        Assert.True(await EventuallyAsync(() => publisher.Status.LastPublishAt is not null), publisher.Status.LastError);
        return running;
    }

    [Fact]
    public async Task DiscoveryConfigsAreRetainedOnTheBroker()
    {
        var source = Source();
        await using var running = await StartedAsync(Publisher(source));

        // Let the first publish land before watching. A client subscribed while it is still
        // being sent receives the live copy, which MQTT delivers without the retain flag —
        // the very thing this test checks — so on a busy machine it failed now and then.
        Assert.True(await EventuallyAsync(() => running.Publisher.Status.LastPublishAt is not null));

        // A client that arrives later — Home Assistant starting after LabbyTwo — still gets them.
        var seen = await WatchAsync("homeassistant/#");
        Assert.True(await EventuallyAsync(() => seen.Any(m => m.Topic == "homeassistant/binary_sensor/labbytwo/c_nas1_status/config")));
        var config = seen.First(m => m.Topic == "homeassistant/binary_sensor/labbytwo/c_nas1_status/config");
        Assert.True(config.Retain);
        Assert.Contains("\"unique_id\":\"labbytwo_c_nas1_status\"", config.Payload);
        Assert.True(await EventuallyAsync(() => seen.Any(m => m.Topic == "homeassistant/sensor/labbytwo/hub_summary/config")));

        var states = await WatchAsync("labbytwo/#");
        Assert.True(await EventuallyAsync(() => states.Any(m => m.Topic == "labbytwo/conn/nas1/status" && m.Payload == "ON" && m.Retain)));
        Assert.True(await EventuallyAsync(() => states.Any(m => m.Topic == "labbytwo/hub/summary" && m.Payload == "2 up, 0 down")));
    }

    [Fact]
    public async Task OnlyWhatChangedIsSentAgain()
    {
        var source = Source();
        var seen = await WatchAsync("#");
        await using var running = await StartedAsync(Publisher(source));
        Assert.True(await EventuallyAsync(() => seen.Any(m => m.Topic == "labbytwo/conn/plex1/status")));
        var publishes = running.Publisher.Status.LastPublishAt;

        // Nothing changed: a publish happens and sends nothing.
        running.Publisher.Kick();
        Assert.True(await EventuallyAsync(() => running.Publisher.Status.LastPublishAt != publishes));
        Assert.Equal(0, running.Publisher.Status.LastPublishSent);

        // Plex goes down: its status, its attributes, and the hub's summary and "anything down".
        seen.Clear();
        source.Lab = Lab(Conn("nas1", "NAS"), Conn("plex1", "Plex", up: false));
        running.Publisher.Kick();
        Assert.True(await EventuallyAsync(() => seen.Any(m => m.Topic == "labbytwo/conn/plex1/status" && m.Payload == "OFF")));
        await Task.Delay(300);
        var topics = seen.Select(m => m.Topic).ToHashSet();
        Assert.Contains("labbytwo/hub/any_down", topics);
        Assert.Contains("labbytwo/hub/summary", topics);
        Assert.Contains("labbytwo/conn/plex1/attributes", topics);
        Assert.DoesNotContain("labbytwo/conn/nas1/status", topics);
        Assert.DoesNotContain(topics, t => t.EndsWith("/config", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABurstOfEventsIsOnePublish()
    {
        var source = Source();
        await using var running = await StartedAsync(new HomeAssistantPublisher(source, new CapturingLogger())
        {
            Debounce = TimeSpan.FromMilliseconds(600),
            RetainedWait = TimeSpan.Zero,
        });
        await Task.Delay(800);
        var before = Volatile.Read(ref source.LabCalls);

        // A sweep's worth of events, spread over a few hundred milliseconds.
        for (var i = 0; i < 10; i++)
        {
            running.Publisher.Kick();
            await Task.Delay(20);
        }

        await Task.Delay(1500);
        var publishes = Volatile.Read(ref source.LabCalls) - before;
        Assert.InRange(publishes, 1, 2);
    }

    [Fact]
    public async Task BirthOnConnectWillOnDropAndOfflineOnStop()
    {
        var availability = await WatchAsync("labbytwo/status");
        var source = Source();
        var running = await StartedAsync(Publisher(source));
        Assert.True(await EventuallyAsync(() => availability.Any(m => m.Payload == "online")));

        // The broker drops LabbyTwo without a goodbye: the will says offline, and the
        // publisher reconnects and says online again by itself.
        availability.Clear();
        await _broker!.DisconnectClientAsync("labbytwo-ha-labbytwo", new MqttServerClientDisconnectOptions
        {
            ReasonCode = MqttDisconnectReasonCode.UnspecifiedError,
        });
        Assert.True(await EventuallyAsync(() => availability.Any(m => m.Payload == "offline")), "no will");
        Assert.True(await EventuallyAsync(() => availability.Any(m => m.Payload == "online") && running.Publisher.Status.Connected), "no reconnect");

        availability.Clear();
        await running.DisposeAsync();
        Assert.True(await EventuallyAsync(() => availability.Any(m => m.Payload == "offline")), "no goodbye");
    }

    [Fact]
    public async Task AHomeAssistantRestartGetsEveryConfigAgain()
    {
        var source = Source();
        var seen = await WatchAsync("homeassistant/#");
        await using var running = await StartedAsync(Publisher(source));
        Assert.True(await EventuallyAsync(() => seen.Any(m => m.Topic.EndsWith("c_nas1_status/config", StringComparison.Ordinal))));

        seen.Clear();
        await SendAsync("homeassistant/status", "online");
        Assert.True(await EventuallyAsync(() => seen.Any(m => m.Topic.EndsWith("c_nas1_status/config", StringComparison.Ordinal) && m.Payload.Length > 0)));
    }

    [Fact]
    public async Task ARemovedConnectionIsClearedAndSoAreLeftoversFromBefore()
    {
        var seen = await WatchAsync("homeassistant/#");

        // A config from a connection deleted while LabbyTwo was not running.
        const string leftover = "homeassistant/binary_sensor/labbytwo/c_gone_status/config";
        await SendAsync(leftover, """{"name":"Connectivity"}""", retain: true);

        var source = Source();
        await using var running = await StartedAsync(Publisher(source));
        Assert.True(await EventuallyAsync(() => seen.Any(m => m.Topic == leftover && m.Payload.Length == 0)), "leftover not cleared");

        // Plex is deleted: its configs go, NAS's stay.
        seen.Clear();
        source.Lab = Lab(Conn("nas1", "NAS"));
        running.Publisher.Kick();
        Assert.True(await EventuallyAsync(() => seen.Any(m => m.Topic == "homeassistant/binary_sensor/labbytwo/c_plex1_status/config" && m.Payload.Length == 0)));
        Assert.DoesNotContain(seen, m => m.Topic.Contains("c_nas1_", StringComparison.Ordinal) && m.Payload.Length == 0);

        // And nothing of Plex's is left retained for a newcomer.
        var later = await WatchAsync("homeassistant/#");
        await Task.Delay(400);
        Assert.DoesNotContain(later, m => m.Topic.Contains("c_plex1_", StringComparison.Ordinal) || m.Topic == leftover);
        Assert.Contains(later, m => m.Topic.Contains("c_nas1_status", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UntickingAConnectionClearsItToo()
    {
        var seen = await WatchAsync("homeassistant/#");
        var source = Source();
        await using var running = await StartedAsync(Publisher(source));

        source.Settings = source.Settings with { AllConnections = false, Connections = new HashSet<string> { "nas1" } };
        running.Publisher.Kick();
        Assert.True(await EventuallyAsync(() => seen.Any(m => m.Topic == "homeassistant/binary_sensor/labbytwo/c_plex1_status/config" && m.Payload.Length == 0)));
    }

    [Fact]
    public async Task SwitchingOffRemovesEverythingAndDisconnects()
    {
        var seen = await WatchAsync("homeassistant/#");
        var source = Source();
        await using var running = await StartedAsync(Publisher(source));

        seen.Clear();
        source.Settings = source.Settings with { Enabled = false };
        running.Publisher.Kick();
        Assert.True(await EventuallyAsync(() => seen.Any(m => m.Topic.EndsWith("c_nas1_status/config", StringComparison.Ordinal) && m.Payload.Length == 0)));
        Assert.True(await EventuallyAsync(() => !running.Publisher.Status.Connected));
    }

    [Fact]
    public async Task NothingIsSentWhileSwitchedOff()
    {
        var source = Source();
        source.Settings = source.Settings with { Enabled = false };
        var connected = 0;
        _broker!.ClientConnectedAsync += _ =>
        {
            Interlocked.Increment(ref connected);
            return Task.CompletedTask;
        };

        await using var running = new Running(Publisher(source)).Start();
        running.Publisher.Kick();
        await Task.Delay(500);
        Assert.Equal(0, Volatile.Read(ref connected));
        Assert.Equal(0, Volatile.Read(ref source.LabCalls));
    }

    [Fact]
    public async Task ButtonPressesReachTheAppAndRepeatsAndReplaysAreHandled()
    {
        await WatchAsync("labbytwo/#");
        var source = Source();
        source.Settings = source.Settings with { ActionButtons = true, MaintenanceButtons = true };
        await using var running = await StartedAsync(Publisher(source));

        await SendAsync("labbytwo/conn/nas1/action/wake/set", "PRESS");
        Assert.True(await EventuallyAsync(() => source.Commands.Any(c => c.Command.ActionId == "wake")));
        var (command, retained) = source.Commands.Single();
        Assert.Equal((HaCommandKind.Action, "nas1", false), (command.Kind, command.ConnectionId, retained));

        // A second press straight away is the same press.
        await SendAsync("labbytwo/conn/nas1/action/wake/set", "PRESS");
        await Task.Delay(300);
        Assert.Single(source.Commands);

        Assert.True(await EventuallyAsync(() => running.Publisher.Status.LastCommand is not null));
    }

    /// <summary>
    /// A press left retained on the broker — by a misconfigured automation, say — would
    /// otherwise run again on every reconnect. It arrives marked as retained, for the app to
    /// refuse and record rather than run.
    /// </summary>
    [Fact]
    public async Task ARetainedPressIsHandedOverAsAReplay()
    {
        await WatchAsync("labbytwo/#");
        await SendAsync("labbytwo/hub/maintenance_start/set", "PRESS", retain: true);

        var source = Source();
        source.Settings = source.Settings with { MaintenanceButtons = true };
        await using var running = await StartedAsync(Publisher(source));

        Assert.True(await EventuallyAsync(() => source.Commands.Any(c => c.Command.Kind == HaCommandKind.MaintenanceStart)));
        Assert.True(source.Commands.Single().Retained);

        await SendAsync("labbytwo/hub/maintenance_start/set", "", retain: true);
    }

    [Fact]
    public async Task CommandTopicsAreNotListenedToUnlessAButtonIsTicked()
    {
        await WatchAsync("labbytwo/#");
        var source = Source();
        await using var running = await StartedAsync(Publisher(source));

        await SendAsync("labbytwo/conn/nas1/action/wake/set", "PRESS");
        await Task.Delay(400);
        Assert.Empty(source.Commands);
    }

    [Fact]
    public async Task APasswordNeverReachesTheLogOrThePage()
    {
        const string password = "hunter2-very-secret";
        _broker!.ValidatingConnectionAsync += args =>
        {
            if (args.ClientId.StartsWith("labbytwo-ha", StringComparison.Ordinal))
                args.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            return Task.CompletedTask;
        };

        var log = new CapturingLogger();
        var source = Source();
        source.Settings = source.Settings with { Username = "labby", Password = password };
        await using var running = new Running(Publisher(source, log)).Start();

        Assert.True(await EventuallyAsync(() => running.Publisher.Status.LastError is not null));
        await Task.Delay(500);   // a retry or two

        Assert.NotEmpty(log.Lines);
        Assert.DoesNotContain(log.Lines, line => line.Contains(password, StringComparison.Ordinal));
        Assert.DoesNotContain(password, running.Publisher.Status.LastError);
        Assert.DoesNotContain(password, source.Settings.Fingerprint);
    }
}
