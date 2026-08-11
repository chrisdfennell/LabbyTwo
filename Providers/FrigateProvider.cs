using System.Diagnostics;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Frigate. A camera that has stopped is the definition of a silent failure — the NVR is
/// up, the disk is fine, and one feed has simply been black since Tuesday. Frigate reports
/// a frame rate per camera, so that is the number to watch, along with how hard the
/// detector is working and whether the recordings disk still has room.
/// </summary>
public sealed class FrigateProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "frigate";
    public string DisplayName => "Frigate NVR";
    public string Icon => "📹";
    public string Category => "Home";
    public string Description => "Cameras alive, detection rate, detector speed, and how full the recordings disk is.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.63:5000", Required: true),

        new("events", "Count recent events", FieldKind.Bool, Default: "true",
            Help: "Adds how many events Frigate recorded in the last 24 hours. One more request per probe."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("cameras", "Cameras"),
        new("cameras_down", "Cameras with no frames"),
        new("detection_fps", "Detections", " fps", 1),
        new("inference_ms", "Detector speed", " ms", 1),
        new("storage_percent", "Recordings disk", "%", 1),
        new("events_24h", "Events today"),
        new("uptime_days", "Uptime", " days", 1),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("A camera has gone dark", "cameras_down", Comparison.Above, 0, ForMinutes: 10,
            Why: "Frigate keeps running with a dead feed. Ten minutes rides out a camera rebooting."),

        new("Recordings disk nearly full", "storage_percent", Comparison.Above, 90, ClearThreshold: 85, ForMinutes: 30,
            Why: "Frigate prunes as it goes, so this filling up usually means retention is set longer than the disk allows."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No base URL configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            using var response = await http.GetAsync($"{baseUrl}/api/stats", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
                return ProbeResult.Down(stopwatch.Elapsed, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            var details = new Dictionary<string, string>();

            int cameras = 0, dark = 0;
            double detections = 0;
            var darkNames = new List<string>();

            if (root.TryGetProperty("cameras", out var feeds) && feeds.ValueKind == JsonValueKind.Object)
            {
                foreach (var camera in feeds.EnumerateObject())
                {
                    cameras++;

                    // camera_fps is what the camera is actually delivering. Zero means the
                    // feed is gone, whatever the rest of Frigate thinks.
                    if ((Number(camera.Value, "camera_fps") ?? 0) <= 0)
                    {
                        dark++;
                        darkNames.Add(camera.Name);
                    }

                    detections += Number(camera.Value, "detection_fps") ?? 0;
                }
            }

            metrics["cameras"] = cameras;
            metrics["cameras_down"] = dark;
            metrics["detection_fps"] = detections;

            if (root.TryGetProperty("detectors", out var detectors) && detectors.ValueKind == JsonValueKind.Object)
            {
                var speeds = detectors.EnumerateObject()
                    .Select(d => Number(d.Value, "inference_speed") ?? 0)
                    .Where(speed => speed > 0)
                    .ToList();

                if (speeds.Count > 0)
                    metrics["inference_ms"] = speeds.Max();
            }

            if (root.TryGetProperty("service", out var service))
            {
                if (Number(service, "uptime") is { } uptime)
                    metrics["uptime_days"] = uptime / 86400;

                if (service.TryGetProperty("version", out var version) && version.GetString() is { } text)
                    details["Version"] = text;

                // Storage is keyed by mount path; recordings are the one that fills up.
                if (service.TryGetProperty("storage", out var storage) && storage.ValueKind == JsonValueKind.Object)
                {
                    foreach (var mount in storage.EnumerateObject())
                    {
                        var total = Number(mount.Value, "total") ?? 0;
                        var used = Number(mount.Value, "used") ?? 0;
                        if (total > 0 && mount.Name.Contains("record", StringComparison.OrdinalIgnoreCase))
                            metrics["storage_percent"] = used / total * 100;
                    }
                }
            }

            if (connection.Settings.GetBool("events", true))
            {
                try
                {
                    var after = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
                    using var events = await http.GetAsync($"{baseUrl}/api/events?after={after}&limit=1000", ct);
                    if (events.IsSuccessStatusCode)
                    {
                        using var list = JsonDocument.Parse(await events.Content.ReadAsStringAsync(ct));
                        if (list.RootElement.ValueKind == JsonValueKind.Array)
                            metrics["events_24h"] = list.RootElement.GetArrayLength();
                    }
                }
                catch (Exception)
                {
                    // Events are a nice-to-have; stats are the point.
                }
            }

            var message = dark > 0
                ? $"{dark} camera{(dark == 1 ? "" : "s")} with no frames: {string.Join(", ", darkNames.Take(3))}"
                : $"{cameras} camera{(cameras == 1 ? "" : "s")}, {detections:0.#} detections/s";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
