using System.Diagnostics;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Bazarr. The interesting number is not "is it up" but how many subtitles it still wants,
/// which is exactly what its badges endpoint counts.
/// </summary>
public sealed class BazarrProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "bazarr";
    public string DisplayName => "Bazarr";
    public string Icon => "💬";
    public string Category => "Media";
    public string Description => "Version, and how many episodes and movies are still missing subtitles.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://bazarr:6767", Required: true),
        new("api_key", "API key", FieldKind.Password, Required: true, Help: "Settings → General → Security."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("subtitles_wanted_episodes", "Episodes wanting subtitles"),
        new("subtitles_wanted_movies", "Movies wanting subtitles"),
        new("providers_failing", "Subtitle providers failing"),
        new("latency_ms", "Response time", "ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Subtitle providers failing", "providers_failing", Comparison.Above, 0, ForMinutes: 30,
            Why: "A provider that stops working quietly stops fetching subtitles."),
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
            var key = connection.Settings.Get("api_key");

            using var status = await GetAsync(http, $"{baseUrl}/api/system/status", key, ct);
            stopwatch.Stop();

            var details = new Dictionary<string, string>();
            if (status.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("bazarr_version", out var version) && version.GetString() is { } text)
                details["Version"] = text;

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            var message = details.GetValueOrDefault("Version") is { Length: > 0 } v ? $"v{v}" : "Connected";

            try
            {
                using var badges = await GetAsync(http, $"{baseUrl}/api/badges", key, ct);
                var root = badges.RootElement;
                var episodes = Number(root, "episodes") ?? 0;
                var movies = Number(root, "movies") ?? 0;
                metrics["subtitles_wanted_episodes"] = episodes;
                metrics["subtitles_wanted_movies"] = movies;
                if (Number(root, "providers") is { } providers)
                    metrics["providers_failing"] = providers;

                message = $"{message} · {episodes + movies:0} wanting subtitles";
            }
            catch
            {
                // Reachability and version still stand without the counts.
            }

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private static async Task<JsonDocument> GetAsync(HttpClient http, string url, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-API-KEY", key);

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Bazarr rejected the API key.");
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}

/// <summary>
/// Tautulli. Plex already reports a stream count; Tautulli adds what those streams are
/// costing — bandwidth, and how many are being transcoded, which is what actually heats
/// a server up.
/// </summary>
public sealed class TautulliProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "tautulli";
    public string DisplayName => "Tautulli";
    public string Icon => "📊";
    public string Category => "Media";
    public string Description => "Active Plex streams, how many are transcoding, and total bandwidth.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://tautulli:8181", Required: true),
        new("api_key", "API key", FieldKind.Password, Required: true, Help: "Settings → Web Interface → API key."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("stream_count", "Active streams"),
        new("transcode_count", "Transcoding"),
        new("direct_play_count", "Direct play"),
        new("bandwidth_mbps", "Total bandwidth", " Mbps", 1),
        new("latency_ms", "Response time", "ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Too many transcodes", "transcode_count", Comparison.Above, 2, ClearThreshold: 1, ForMinutes: 5,
            Why: "Transcoding is what pins the CPU; a couple at once is usually the limit."),
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
            var key = Uri.EscapeDataString(connection.Settings.Get("api_key"));

            // Tautulli authenticates by query string; there is no header form.
            using var response = await http.GetAsync($"{baseUrl}/api/v2?apikey={key}&cmd=get_activity", ct);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            stopwatch.Stop();

            // Tautulli answers 200 with result="error" for a bad key rather than a 401.
            if (!document.RootElement.TryGetProperty("response", out var wrapper))
                return ProbeResult.Down(stopwatch.Elapsed, "Tautulli returned an unexpected response.");

            if (wrapper.TryGetProperty("result", out var result) &&
                !string.Equals(result.GetString(), "success", StringComparison.OrdinalIgnoreCase))
            {
                var why = wrapper.TryGetProperty("message", out var m) ? m.GetString() : null;
                return ProbeResult.Down(stopwatch.Elapsed, why is { Length: > 0 }
                    ? $"Tautulli said: {why}"
                    : "Tautulli rejected the request — check the API key.");
            }

            var data = wrapper.TryGetProperty("data", out var d) ? d : default;
            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };

            // Every count in this payload is a string, including the numbers.
            var streams = AsNumber(data, "stream_count") ?? 0;
            metrics["stream_count"] = streams;
            metrics["transcode_count"] = AsNumber(data, "stream_count_transcode") ?? 0;
            metrics["direct_play_count"] = AsNumber(data, "stream_count_direct_play") ?? 0;
            if (AsNumber(data, "total_bandwidth") is { } kbps)
                metrics["bandwidth_mbps"] = kbps / 1000;

            var message = streams == 0
                ? "Nothing playing"
                : $"{streams:0} stream(s), {metrics["transcode_count"]:0} transcoding";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    /// <summary>Tautulli returns its numbers as JSON strings, so parse either shape.</summary>
    private static double? AsNumber(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => double.TryParse(value.GetString(), out var parsed) ? parsed : null,
            _ => null,
        };
    }
}

/// <summary>
/// NZBGet, over its JSON-RPC endpoint. Old and new versions both answer /jsonrpc/status,
/// which keeps this to one call.
/// </summary>
public sealed class NzbGetProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "nzbget";
    public string DisplayName => "NZBGet";
    public string Icon => "📥";
    public string Category => "Downloads";
    public string Description => "Download rate, queue size, free disk space, and whether downloading is paused.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://nzbget:6789", Required: true),
        new("username", "Username", FieldKind.Text, "nzbget"),
        new("password", "Password", FieldKind.Password),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("download_mbps", "Download", " Mbps", 2),
        new("remaining_mb", "Queue remaining", " MB"),
        new("free_disk_gb", "Free disk", " GB", 1),
        new("download_paused", "Paused"),
        new("latency_ms", "Response time", "ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Downloads paused", "download_paused", Comparison.Above, 0, ForMinutes: 60,
            Why: "An hour, because pausing on purpose is normal and forgetting to unpause is the problem."),
        new("Download disk filling", "free_disk_gb", Comparison.Below, 20, ClearThreshold: 40,
            Why: "A full download disk fails everything in the queue at once."),
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
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/jsonrpc/status");

            if (connection.Settings.Get("username") is { Length: > 0 } username)
            {
                var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                    $"{username}:{connection.Settings.Get("password")}"));
                request.Headers.TryAddWithoutValidation("Authorization", $"Basic {credentials}");
            }

            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return ProbeResult.Down(stopwatch.Elapsed, "NZBGet rejected the username or password.");
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            stopwatch.Stop();

            if (!document.RootElement.TryGetProperty("result", out var result))
                return ProbeResult.Down(stopwatch.Elapsed, "NZBGet returned no result — is that the JSON-RPC port?");

            const double bytesToMbps = 8d / 1_000_000;
            var rate = Number(result, "DownloadRate") ?? 0;
            var remaining = Number(result, "RemainingSizeMB") ?? 0;
            var paused = result.TryGetProperty("DownloadPaused", out var p) && p.ValueKind == JsonValueKind.True;

            var metrics = new Dictionary<string, double>
            {
                ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
                ["download_mbps"] = rate * bytesToMbps,
                ["remaining_mb"] = remaining,
                ["download_paused"] = paused ? 1 : 0,
            };

            if (Number(result, "FreeDiskSpaceMB") is { } freeMb)
                metrics["free_disk_gb"] = freeMb / 1024;

            var message = paused
                ? $"Paused — {remaining:0} MB queued"
                : remaining > 0
                    ? $"↓ {rate * bytesToMbps:0.#} Mbps, {remaining:0} MB left"
                    : "Idle";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
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

/// <summary>
/// Overseerr and Jellyseerr are the same application, so one provider covers both. The
/// number worth watching is how many requests are sitting unapproved.
/// </summary>
public sealed class OverseerrProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "overseerr";
    public string DisplayName => "Overseerr / Jellyseerr";
    public string Icon => "🎟️";
    public string Category => "Media";
    public string Description => "Version, and how many requests are pending, approved or available.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://seerr:5055", Required: true),
        new("api_key", "API key", FieldKind.Password,
            Help: "Settings → General → API Key. Optional: without it this still reports up or down, just no counts."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("requests_pending", "Requests pending"),
        new("requests_total", "Requests total"),
        new("requests_available", "Requests available"),
        new("latency_ms", "Response time", "ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Requests waiting", "requests_pending", Comparison.Above, 0, ForMinutes: 720,
            Why: "Twelve hours, so it nudges you about a forgotten queue rather than every new request."),
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
            var key = connection.Settings.Get("api_key");

            // /status needs no key, so an install with none still gets up/down.
            using var status = await GetAsync(http, $"{baseUrl}/api/v1/status", key, ct);
            stopwatch.Stop();

            var details = new Dictionary<string, string>();
            if (status.RootElement.TryGetProperty("version", out var version) && version.GetString() is { } text)
                details["Version"] = text;
            if (status.RootElement.TryGetProperty("updateAvailable", out var update) && update.ValueKind == JsonValueKind.True)
                details["Update"] = "available";

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            var message = details.GetValueOrDefault("Version") is { Length: > 0 } v ? $"v{v}" : "Connected";

            if (key.Length > 0)
            {
                try
                {
                    using var counts = await GetAsync(http, $"{baseUrl}/api/v1/request/count", key, ct);
                    var root = counts.RootElement;
                    var pending = Number(root, "pending") ?? 0;
                    metrics["requests_pending"] = pending;
                    if (Number(root, "total") is { } total)
                        metrics["requests_total"] = total;
                    if (Number(root, "available") is { } available)
                        metrics["requests_available"] = available;

                    message = pending > 0 ? $"{message} · {pending:0} pending" : $"{message} · nothing pending";
                }
                catch
                {
                    // The status call already proved it is alive.
                }
            }

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private static async Task<JsonDocument> GetAsync(HttpClient http, string url, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (key.Length > 0)
            request.Headers.TryAddWithoutValidation("X-Api-Key", key);

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Overseerr rejected the API key.");
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}

/// <summary>
/// ErsatzTV. Its management API moves between releases, so this checks the thing it exists
/// to produce: the IPTV channel playlist. That endpoint is the product, needs no key, and
/// counting the channels in it proves the service is genuinely working rather than merely
/// listening.
/// </summary>
public sealed class ErsatzTvProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "ersatztv";
    public string DisplayName => "ErsatzTV";
    public string Icon => "📺";
    public string Category => "Media";
    public string Description => "Reachability and how many channels it is publishing.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://ersatztv:8409", Required: true),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("channel_count", "Channels"),
        new("latency_ms", "Response time", "ms"),
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
            using var response = await http.GetAsync($"{baseUrl}/iptv/channels.m3u", ct);
            response.EnsureSuccessStatusCode();
            var playlist = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();

            if (!playlist.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                return ProbeResult.Down(stopwatch.Elapsed,
                    "That answered, but not with an M3U playlist. Check the URL points at ErsatzTV.");

            var channels = CountChannels(playlist);
            return ProbeResult.Up(stopwatch.Elapsed,
                channels == 1 ? "1 channel" : $"{channels} channels",
                new Dictionary<string, double>
                {
                    ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
                    ["channel_count"] = channels,
                });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    /// <summary>One #EXTINF line per channel — the format's only reliable marker.</summary>
    public static int CountChannels(string playlist) =>
        playlist.Split('\n').Count(line => line.TrimStart().StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Unmanic, over its v2 API. The number that matters is the backlog: a library optimiser
/// with a thousand pending tasks is either wedged or was pointed at too much.
/// </summary>
public sealed class UnmanicProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "unmanic";
    public string DisplayName => "Unmanic";
    public string Icon => "🎚️";
    public string Category => "Media";
    public string Description => "Version, pending tasks, and how many workers are busy.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://unmanic:8888", Required: true,
            Help: "Unmanic's own port inside the container is 8888 even when it is published as something else."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("pending_tasks", "Pending tasks"),
        new("workers_active", "Workers busy"),
        new("latency_ms", "Response time", "ms"),
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

            using var versionResponse = await http.GetAsync($"{baseUrl}/unmanic/api/v2/version/read", ct);
            versionResponse.EnsureSuccessStatusCode();
            using var versionDocument = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync(ct));
            stopwatch.Stop();

            var details = new Dictionary<string, string>();
            if (versionDocument.RootElement.TryGetProperty("version", out var version) && version.GetString() is { } text)
                details["Version"] = text;

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            var message = details.GetValueOrDefault("Version") is { Length: > 0 } v ? $"v{v}" : "Connected";

            // The pending list is a POST with a paging body even when you only want the count.
            try
            {
                using var body = new StringContent("""{"start":0,"length":1}""",
                    System.Text.Encoding.UTF8, "application/json");
                using var pending = await http.PostAsync($"{baseUrl}/unmanic/api/v2/pending/tasks", body, ct);
                pending.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await pending.Content.ReadAsStringAsync(ct));

                if (document.RootElement.TryGetProperty("recordsTotal", out var total) &&
                    total.ValueKind == JsonValueKind.Number)
                {
                    metrics["pending_tasks"] = total.GetDouble();
                    message = $"{message} · {total.GetDouble():0} pending";
                }
            }
            catch
            {
                // Version and reachability are still worth having.
            }

            try
            {
                using var workers = await http.GetAsync($"{baseUrl}/unmanic/api/v2/workers/status", ct);
                workers.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await workers.Content.ReadAsStringAsync(ct));

                if (document.RootElement.TryGetProperty("workers_status", out var list) &&
                    list.ValueKind == JsonValueKind.Array)
                {
                    metrics["workers_active"] = list.EnumerateArray()
                        .Count(w => w.TryGetProperty("idle", out var idle) && idle.ValueKind == JsonValueKind.False);
                }
            }
            catch
            {
                // Optional.
            }

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }
}
