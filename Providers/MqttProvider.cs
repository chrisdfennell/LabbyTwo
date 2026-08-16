using System.Diagnostics;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Services;

namespace LabbyTwo.Providers;

/// <summary>
/// An MQTT broker, and with it Zigbee2MQTT, Tasmota, ESPHome, Shelly in MQTT mode and
/// anything else in the house that publishes rather than answers.
///
/// This is the second escape hatch, and the counterpart to the JSON API provider: that one
/// covers everything that answers a request, and this covers everything that does not.
/// Between them a home lab can chart something LabbyTwo has never heard of without anybody
/// writing a provider for it.
///
/// The session itself lives in <see cref="MqttPool"/> — see the note there for why a
/// subscription cannot live inside a probe. This class is only the mapping from what has
/// arrived to what the dashboard charts.
/// </summary>
public sealed class MqttProvider(ILogger<MqttPool> log) : IConnectionProvider, IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Owned rather than injected, deliberately. Nothing else in the app needs a broker
    /// session, and a provider whose constructor asks for something the container has not
    /// been given breaks the resolution of *every* provider — the registry builds them all
    /// together. Providers are singletons, so this lives exactly as long as it should, and
    /// the container disposes it on the way out.
    ///
    /// If a second thing ever wants the pool, that is the moment to promote it to a
    /// registered service.
    /// </summary>
    private readonly MqttPool _pool = new(log);

    public void Dispose() => _pool.Dispose();

    public ValueTask DisposeAsync() => _pool.DisposeAsync();

    public string Type => "mqtt";
    public string DisplayName => "MQTT broker";
    public string Icon => "📨";
    public string Category => "Home";

    public string Description =>
        "Any MQTT broker — Zigbee2MQTT, Tasmota, ESPHome. Subscribes and turns the messages into metrics.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("host", "Broker address", FieldKind.Text, "192.168.1.50", Required: true,
            Help: "Reached by LabbyTwo, so it has to resolve from inside its container."),
        new("port", "Port", FieldKind.Number, Default: "1883"),
        new("tls", "Use TLS", FieldKind.Bool, Default: "false"),
        new("username", "Username", FieldKind.Text),
        new("password", "Password", FieldKind.Password),

        new("topics", "Subscribe to", FieldKind.Text, "zigbee2mqtt/#", Default: "#",
            Help: "An MQTT filter. + matches one level, # matches the rest. Narrow it: “#” on a busy "
                + "broker means holding every topic it carries."),

        new("metrics", "Metrics", FieldKind.Textarea,
            "kitchen_battery = zigbee2mqtt/Kitchen sensor:battery\nboiler_watts = tele/boiler/SENSOR:ENERGY.Power",
            Help: "One per line as name = topic:path. The path is optional and reads into a JSON payload "
                + "with dots for objects and [n] for array elements — leave it off when the whole payload "
                + "is the number. Booleans count as 1 and 0."),

        new("stale_minutes", "Treat silence as down after (minutes)", FieldKind.Number, Default: "0",
            Help: "Optional. A sensor that has said nothing for this long makes the connection down. "
                + "Zero means never, which is right for a broker whose devices only publish on change.")
        { Advanced = true },
    ];

    /// <summary>
    /// Whatever was typed into the Metrics box, the same as the JSON API provider — the set
    /// belongs to this instance rather than to the integration.
    /// </summary>
    public IReadOnlyList<MetricSpec> MetricsFor(Connection connection) =>
    [
        new("mqtt_topics", "Topics seen"),
        new("mqtt_messages", "Messages received"),
        .. JsonApiProvider.ParseMetricMap(connection.Settings.Get("metrics"))
            .Select(m => MetricSpec.Fallback(m.Name)),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var snapshot = await _pool.SnapshotAsync(connection, ct);
            stopwatch.Stop();

            if (!snapshot.Connected)
                return ProbeResult.Down(stopwatch.Elapsed, snapshot.Error ?? "Not connected to the broker.");

            var readings = new Dictionary<string, double>
            {
                ["mqtt_topics"] = snapshot.Topics.Count,
                ["mqtt_messages"] = snapshot.Messages,
            };

            var missing = new List<string>();
            foreach (var (name, target) in JsonApiProvider.ParseMetricMap(connection.Settings.Get("metrics")))
            {
                if (Value(snapshot, target) is { } value)
                    readings[name] = value;
                else
                    missing.Add(name);
            }

            // Silence is the failure mode that matters on a broker: everything looks fine,
            // because a broker with nothing to say looks exactly like a broker nobody is
            // publishing to any more.
            var stale = Math.Max(0, connection.Settings.GetInt("stale_minutes", 0));
            if (stale > 0 && snapshot.Topics.Count > 0)
            {
                var newest = snapshot.Topics.Values.Max(reading => reading.At);
                if (DateTimeOffset.Now - newest > TimeSpan.FromMinutes(stale))
                {
                    return new ProbeResult(false,
                        $"Nothing published for {Ago.Since(newest)[..^4]} — connected, but the broker has gone quiet.",
                        stopwatch.Elapsed, readings);
                }
            }

            var summary = $"{snapshot.Topics.Count} topic{(snapshot.Topics.Count == 1 ? "" : "s")}, "
                        + $"{snapshot.Messages} message{(snapshot.Messages == 1 ? "" : "s")}";

            // Named, because "a metric is missing" is nearly always a typo in a topic and the
            // only way to find it is to know which line is wrong.
            if (missing.Count > 0)
                summary += $" · no value yet for {string.Join(", ", missing)}";

            return ProbeResult.Up(stopwatch.Elapsed, summary, readings);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Reads one mapping. "topic" on its own means the payload is the number; "topic:path"
    /// reads into a JSON payload. The topic is taken up to the *last* colon, because MQTT
    /// topics may legally contain one and a path may not.
    /// </summary>
    private static double? Value(MqttPool.Snapshot snapshot, string target)
    {
        var split = target.LastIndexOf(':');
        var topic = split > 0 ? target[..split].Trim() : target.Trim();
        var path = split > 0 ? target[(split + 1)..].Trim() : "";

        if (!snapshot.Topics.TryGetValue(topic, out var reading))
            return null;

        if (path.Length == 0)
            return Number(reading.Payload);

        try
        {
            using var doc = JsonDocument.Parse(reading.Payload);
            return JsonApiProvider.Resolve(doc.RootElement, path) is { } element
                ? Number(element)
                : null;
        }
        catch (JsonException)
        {
            // A path was given for something that is not JSON. Not an error worth failing the
            // whole probe over — the summary already names the metric with no value.
            return null;
        }
    }

    private static double? Number(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        JsonValueKind.String => Number(element.GetString() ?? ""),
        _ => null,
    };

    /// <summary>
    /// A bare payload. Tasmota publishes "ON"/"OFF" as often as it publishes a number, and a
    /// switch that charts as 1 and 0 is worth more than one that charts as nothing.
    /// </summary>
    private static double? Number(string raw)
    {
        var text = raw.Trim();

        if (double.TryParse(text, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return text.ToLowerInvariant() switch
        {
            "on" or "true" or "online" or "open" or "yes" => 1,
            "off" or "false" or "offline" or "closed" or "no" => 0,
            _ => null,
        };
    }
}
