using System.Collections.Concurrent;
using LabbyTwo.Core;
using MQTTnet;

namespace LabbyTwo.Services;

/// <summary>
/// The long-lived half of MQTT, which is the whole difficulty with it.
///
/// Every other provider in LabbyTwo is a request: the monitor asks, something answers, and
/// nothing is held between sweeps. MQTT is the opposite shape — you connect once, subscribe,
/// and the broker tells *you*, whenever it feels like it. A provider that connected inside
/// <c>ProbeAsync</c> would open and tear down a session every thirty seconds, receive only
/// retained messages, and miss every live one, which is most of what a broker carries.
///
/// So the session lives here, one per connection, and the probe reads a snapshot of what has
/// arrived. That inverts the usual relationship: the probe is no longer what fetches the
/// data, it is what reports on a conversation already happening.
///
/// Owned by <see cref="Providers.MqttProvider"/> rather than registered in the container.
/// Providers are singletons, so it lives exactly as long as it should — and a provider whose
/// constructor asks the container for something it has not been given breaks the resolution
/// of *every* provider, because the registry builds them all together. Nothing else needs a
/// broker session; if something ever does, that is the moment to promote this to a service.
/// </summary>
public sealed class MqttPool(ILogger<MqttPool> log) : IDisposable, IAsyncDisposable
{
    /// <param name="Payload">The last message on the topic, as text. MQTT payloads are bytes; everything a home lab publishes is UTF-8.</param>
    public sealed record Reading(string Payload, DateTimeOffset At);

    /// <param name="Error">Why there is no connection, when there is not. Null while connected.</param>
    public sealed record Snapshot(
        bool Connected,
        string? Error,
        DateTimeOffset? Since,
        long Messages,
        IReadOnlyDictionary<string, Reading> Topics);

    /// <summary>
    /// A broker with a chatty tree can carry thousands of topics, and this holds the latest
    /// payload of each for ever. Bounded so a mistyped filter of "#" on a busy broker costs a
    /// few megabytes rather than the process — past the cap new topics are dropped and the
    /// ones already known keep updating, which keeps a configured mapping working.
    /// </summary>
    private const int MaxTopics = 2000;

    private sealed class Session : IAsyncDisposable
    {
        public required string Fingerprint { get; init; }
        public required IMqttClient Client { get; init; }
        public ConcurrentDictionary<string, Reading> Topics { get; } = new();
        public long Messages;
        public DateTimeOffset? Since;
        public string? Error;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Client.DisconnectAsync();
            }
            catch
            {
                // Going away regardless; a broker that will not say goodbye is not a problem
                // worth surfacing at shutdown.
            }
            Client.Dispose();
        }
    }

    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// What has arrived on this connection, connecting first if nothing is connected yet.
    ///
    /// The first call pays for the connection and subscription; every later one is a
    /// dictionary read. A connection whose settings have changed is torn down and rebuilt,
    /// because the alternative is an edited broker address that quietly keeps reporting from
    /// the old one.
    /// </summary>
    public async Task<Snapshot> SnapshotAsync(Connection connection, CancellationToken ct)
    {
        var fingerprint = Fingerprint(connection);

        if (_sessions.TryGetValue(connection.Id, out var existing) && existing.Fingerprint == fingerprint)
            return Read(existing);

        await _lock.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(connection.Id, out existing) && existing.Fingerprint == fingerprint)
                return Read(existing);

            if (existing is not null)
            {
                _sessions.TryRemove(connection.Id, out _);
                await existing.DisposeAsync();
            }

            var session = await OpenAsync(connection, fingerprint, ct);
            _sessions[connection.Id] = session;
            return Read(session);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Drops a connection's session, so deleting or disabling one closes its socket.</summary>
    public async Task ForgetAsync(string connectionId)
    {
        if (_sessions.TryRemove(connectionId, out var session))
            await session.DisposeAsync();
    }

    private static Snapshot Read(Session session) => new(
        session.Client.IsConnected,
        session.Client.IsConnected ? null : session.Error ?? "Not connected.",
        session.Since,
        Interlocked.Read(ref session.Messages),
        session.Topics);

    private async Task<Session> OpenAsync(Connection connection, string fingerprint, CancellationToken ct)
    {
        var host = connection.Settings.Get("host").Trim();
        if (host.Length == 0)
            throw new InvalidOperationException("No broker address configured.");

        var port = Math.Clamp(connection.Settings.GetInt("port", 1883), 1, 65535);
        var filter = connection.Settings.Get("topics").Trim() is { Length: > 0 } t ? t : "#";

        var client = new MqttClientFactory().CreateMqttClient();

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            // Identifying, and unique per connection: two LabbyTwo connections to one broker
            // sharing a client id would each keep kicking the other off, which presents as a
            // connection that flaps for no visible reason.
            .WithClientId($"labbytwo-{connection.Id}")
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));

        if (connection.Settings.Get("username") is { Length: > 0 } user)
            builder = builder.WithCredentials(user, connection.Settings.Get("password"));

        if (connection.Settings.GetBool("tls"))
        {
            // Self-signed is the norm on a LAN broker, the same as everywhere else here —
            // see ProviderHttp. Refusing one would make this unusable on the machines it is
            // most for.
            builder = builder.WithTlsOptions(options => options
                .UseTls()
                .WithCertificateValidationHandler(_ => true));
        }

        var session = new Session { Fingerprint = fingerprint, Client = client };

        client.ApplicationMessageReceivedAsync += message =>
        {
            var topic = message.ApplicationMessage.Topic;

            // Past the cap, keep updating what is already known and ignore new topics.
            if (session.Topics.Count < MaxTopics || session.Topics.ContainsKey(topic))
            {
                session.Topics[topic] = new Reading(
                    message.ApplicationMessage.ConvertPayloadToString() ?? "",
                    DateTimeOffset.Now);
            }

            Interlocked.Increment(ref session.Messages);
            return Task.CompletedTask;
        };

        client.DisconnectedAsync += args =>
        {
            session.Error = args.Exception?.Message ?? args.Reason.ToString();
            log.LogInformation("MQTT {Connection} disconnected: {Reason}", connection.Name, session.Error);
            return Task.CompletedTask;
        };

        await client.ConnectAsync(builder.Build(), ct);

        await client.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(filter))
                .Build(),
            ct);

        session.Since = DateTimeOffset.Now;

        // Retained messages arrive immediately after subscribing, but "immediately" is a
        // round trip. Without this pause the very first probe reports zero topics on a broker
        // that is about to hand over two hundred, which reads as a broken connection.
        await Task.Delay(TimeSpan.FromMilliseconds(750), ct);

        return session;
    }

    /// <summary>
    /// Everything that would make the existing socket the wrong one. The password is included
    /// by length rather than by value, so a rotated password reconnects without this holding
    /// a credential in a dictionary key.
    /// </summary>
    private static string Fingerprint(Connection connection) =>
        string.Join('|',
            connection.Settings.Get("host"),
            connection.Settings.Get("port"),
            connection.Settings.Get("username"),
            connection.Settings.Get("password").Length,
            connection.Settings.Get("tls"),
            connection.Settings.Get("topics"));

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
            await session.DisposeAsync();

        _sessions.Clear();
        _lock.Dispose();
    }

    /// <summary>
    /// Both, and on purpose. A type that implements only IAsyncDisposable forces every
    /// container that ever holds one to be disposed asynchronously, which is a requirement
    /// this has no business imposing on the rest of the app — it broke every test that
    /// disposes a service provider the ordinary way.
    ///
    /// The synchronous path drops the sockets without the courtesy of a DISCONNECT packet.
    /// That is fine for MQTT: the broker notices on the next keep-alive, which is what it
    /// does for every client that loses power.
    /// </summary>
    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Client.Dispose();

        _sessions.Clear();
        _lock.Dispose();
    }
}
