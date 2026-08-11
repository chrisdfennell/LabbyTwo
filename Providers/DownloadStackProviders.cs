using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// SABnzbd. NZBGet has been here since the start and this has not, which is odd given they
/// are the two halves of the same choice — and the queue backing up while nothing appears
/// in the library is the failure both are asked about.
/// </summary>
public sealed class SabnzbdProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "sabnzbd";
    public string DisplayName => "SABnzbd";
    public string Icon => "📥";
    public string Category => "Downloads";
    public string Description => "Queue size, speed, what is left to download, free disk, and whether it is paused.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.30:8080", Required: true),
        new("api_key", "API key", FieldKind.Password, Required: true,
            Help: "Config → General → API Key. The full key, not the NZB key."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("queue", "In the queue"),
        new("speed_mbps", "Speed", " MB/s", 2),
        new("remaining_gb", "Left to fetch", " GB", 1),
        new("disk_free_gb", "Free disk", " GB", 1),
        new("paused", "Paused"),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Downloads are paused", "paused", Comparison.Above, 0, ForMinutes: 30,
            Why: "Half an hour, because pausing for a bit is a thing people do on purpose and then forget."),

        new("Running out of disk", "disk_free_gb", Comparison.Below, 20, ClearThreshold: 40, ForMinutes: 15,
            Why: "SABnzbd stops rather than filling the disk, which looks like nothing happening at all."),
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
            var url = $"{baseUrl}/api?mode=queue&output=json&apikey={Uri.EscapeDataString(connection.Settings.Get("api_key"))}";

            using var response = await http.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();

            using var document = JsonDocument.Parse(body);

            // A wrong key is a 200 with {"status": false, "error": "API Key Incorrect"}.
            if (document.RootElement.TryGetProperty("error", out var error))
                return ProbeResult.Down(stopwatch.Elapsed, $"SABnzbd said: {error.GetString()}");

            if (!document.RootElement.TryGetProperty("queue", out var queue))
                return ProbeResult.Down(stopwatch.Elapsed, "No queue in the reply — is this SABnzbd?");

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };

            if (Number(queue, "noofslots") is { } slots)
                metrics["queue"] = slots;

            // Reported in kB/s as a string, because everything here is a string.
            if (Number(queue, "kbpersec") is { } kbps)
                metrics["speed_mbps"] = kbps / 1024;

            if (Number(queue, "mbleft") is { } left)
                metrics["remaining_gb"] = left / 1024;

            if (Number(queue, "diskspace1") is { } free)
                metrics["disk_free_gb"] = free;

            var paused = queue.TryGetProperty("paused", out var state) && state.ValueKind == JsonValueKind.True;
            metrics["paused"] = paused ? 1 : 0;

            var message = paused
                ? "Paused"
                : metrics.GetValueOrDefault("queue") is var count and > 0
                    ? $"{count:0} in the queue at {metrics.GetValueOrDefault("speed_mbps"):0.#} MB/s"
                    : "Idle";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }
}

/// <summary>
/// Transmission. Its RPC insists on a session id that you can only get by being refused
/// once — the first call comes back 409 with the id in a header, and you repeat the call
/// with it. Handled here so nobody has to know that.
/// </summary>
public sealed class TransmissionProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "transmission";
    public string DisplayName => "Transmission";
    public string Icon => "🐢";
    public string Category => "Downloads";
    public string Description => "Torrents active, transfer rates, and how much has moved this session.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.31:9091", Required: true,
            Help: "Just the host and port — /transmission/rpc is added for you."),
        new("username", "Username", FieldKind.Text, Help: "Only if the web interface asks."),
        new("password", "Password", FieldKind.Password),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("torrents", "Torrents"),
        new("active", "Active"),
        new("download_mbps", "Down", " MB/s", 2),
        new("upload_mbps", "Up", " MB/s", 2),
        new("latency_ms", "Response time", " ms"),
    ];

    // One session id per connection, refreshed when Transmission decides to rotate it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sessions = new();

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No base URL configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var document = await CallAsync(connection, baseUrl, "session-stats", ct);
            stopwatch.Stop();

            if (!document.RootElement.TryGetProperty("arguments", out var arguments))
                return ProbeResult.Down(stopwatch.Elapsed, "No stats in the reply.");

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };

            if (Number(arguments, "torrentCount") is { } total)
                metrics["torrents"] = total;
            if (Number(arguments, "activeTorrentCount") is { } active)
                metrics["active"] = active;
            if (Number(arguments, "downloadSpeed") is { } down)
                metrics["download_mbps"] = down / 1024 / 1024;
            if (Number(arguments, "uploadSpeed") is { } up)
                metrics["upload_mbps"] = up / 1024 / 1024;

            var message = metrics.GetValueOrDefault("active") is var running and > 0
                ? $"{running:0} active at {metrics.GetValueOrDefault("download_mbps"):0.#} MB/s"
                : $"{metrics.GetValueOrDefault("torrents"):0} torrents, idle";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _sessions.TryRemove(connection.Id, out _);
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private async Task<JsonDocument> CallAsync(Connection connection, string baseUrl, string method, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);

        async Task<HttpResponseMessage> SendAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/transmission/rpc")
            {
                Content = new StringContent($"{{\"method\":\"{method}\"}}", Encoding.UTF8, "application/json"),
            };

            if (_sessions.TryGetValue(connection.Id, out var session))
                request.Headers.TryAddWithoutValidation("X-Transmission-Session-Id", session);

            if (connection.Settings.Get("username") is { Length: > 0 } user)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{connection.Settings.Get("password")}")));
            }

            return await http.SendAsync(request, ct);
        }

        var response = await SendAsync();

        // The documented handshake: 409 carries the id to use, and the call is repeated.
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict
            && response.Headers.TryGetValues("X-Transmission-Session-Id", out var values))
        {
            _sessions[connection.Id] = values.First();
            response.Dispose();
            response = await SendAsync();
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("Transmission refused the username or password.");

            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
