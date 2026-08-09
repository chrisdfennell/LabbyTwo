using System.Diagnostics;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Jellyfin. The same shape as Plex — a server version and who is watching — but a
/// different API and no plex.tv round trip, so it is its own provider rather than a flag
/// on that one.
/// </summary>
public sealed class JellyfinProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "jellyfin";
    public string DisplayName => "Jellyfin";
    public string Icon => "🎞️";
    public string Category => "Media";
    public string Description => "Server version and how many people are streaming right now.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.50:8096", Required: true),
        new("api_key", "API key", FieldKind.Password, Required: true,
            Help: "Dashboard → Advanced → API Keys."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("stream_count", "Active streams"),
        new("transcode_count", "Transcoding"),
        new("latency_ms", "Response time", " ms"),
    ];

    public sealed record Session(string User, string Item, string Device, double PercentDone, bool Transcoding);

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        if (connection.Settings.Get("url").Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No base URL configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var info = await GetAsync(connection, "System/Info", ct);
            stopwatch.Stop();

            var details = new Dictionary<string, string>();
            if (info.RootElement.TryGetProperty("Version", out var version) && version.GetString() is { } text)
                details["Version"] = text;

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            var message = details.GetValueOrDefault("Version") is { Length: > 0 } v ? $"v{v}" : "Connected";

            try
            {
                var sessions = await SessionsAsync(connection, ct);
                metrics["stream_count"] = sessions.Count;
                metrics["transcode_count"] = sessions.Count(s => s.Transcoding);
                message = sessions.Count == 0
                    ? $"{message} · nothing playing"
                    : $"{message} · {sessions.Count} streaming";
            }
            catch
            {
                // Version and reachability are still worth reporting without sessions.
            }

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    /// <summary>What is playing. Used by the now-playing widget as well as the probe.</summary>
    public async Task<IReadOnlyList<Session>> SessionsAsync(Connection connection, CancellationToken ct)
    {
        using var document = await GetAsync(connection, "Sessions", ct);
        var sessions = new List<Session>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            // Sessions with no NowPlayingItem are idle clients, not streams.
            if (!element.TryGetProperty("NowPlayingItem", out var item) || item.ValueKind != JsonValueKind.Object)
                continue;

            var title = item.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "";
            if (item.TryGetProperty("SeriesName", out var series) && series.GetString() is { Length: > 0 } show)
                title = $"{show} — {title}";

            double percent = 0;
            if (element.TryGetProperty("PlayState", out var play) &&
                play.TryGetProperty("PositionTicks", out var position) && position.ValueKind == JsonValueKind.Number &&
                item.TryGetProperty("RunTimeTicks", out var runtime) && runtime.ValueKind == JsonValueKind.Number &&
                runtime.GetDouble() > 0)
                percent = position.GetDouble() / runtime.GetDouble() * 100;

            var transcoding = element.TryGetProperty("TranscodingInfo", out var transcode)
                              && transcode.ValueKind == JsonValueKind.Object;

            sessions.Add(new Session(
                element.TryGetProperty("UserName", out var user) ? user.GetString() ?? "" : "",
                title,
                element.TryGetProperty("DeviceName", out var device) ? device.GetString() ?? "" : "",
                Math.Clamp(percent, 0, 100),
                transcoding));
        }

        return sessions;
    }

    private async Task<JsonDocument> GetAsync(Connection connection, string path, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{path}");

        // The modern header. Jellyfin also accepts ?api_key=, but that lands in logs.
        request.Headers.TryAddWithoutValidation("Authorization",
            $"MediaBrowser Token=\"{connection.Settings.Get("api_key")}\"");

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Jellyfin rejected the API key.");
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}

/// <summary>
/// qBittorrent's Web UI API. Logs in with the same credentials the web interface uses and
/// keeps the session cookie for the life of one probe.
/// </summary>
public sealed class QBittorrentProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "qbittorrent";
    public string DisplayName => "qBittorrent";
    public string Icon => "🌀";
    public string Category => "Downloads";
    public string Description => "Download and upload rate, and how many torrents are active.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.50:8080", Required: true),
        new("username", "Username", FieldKind.Text, "admin",
            Help: "Leave blank if the Web UI is set to bypass authentication for your subnet."),
        new("password", "Password", FieldKind.Password),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("download_mbps", "Download", " Mbps", 2),
        new("upload_mbps", "Upload", " Mbps", 2),
        new("torrents_downloading", "Downloading"),
        new("torrents_seeding", "Seeding"),
        new("latency_ms", "Response time", " ms"),
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
            var cookie = await SignInAsync(http, connection, baseUrl, ct);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v2/transfer/info");
            if (cookie is { Length: > 0 })
                request.Headers.TryAddWithoutValidation("Cookie", cookie);

            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                throw new InvalidOperationException(
                    "qBittorrent refused the request. Either the credentials are wrong, or its Host header check " +
                    "is rejecting this address — Options → Web UI → \"Enable Host header validation\".");
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            stopwatch.Stop();

            var root = document.RootElement;
            const double toMbps = 8d / 1_000_000;
            var down = Number(root, "dl_info_speed") ?? 0;
            var up = Number(root, "up_info_speed") ?? 0;

            var metrics = new Dictionary<string, double>
            {
                ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
                ["download_mbps"] = down * toMbps,
                ["upload_mbps"] = up * toMbps,
            };

            await TryCountTorrentsAsync(http, baseUrl, cookie, metrics, ct);

            return ProbeResult.Up(stopwatch.Elapsed,
                $"↓ {down * toMbps:0.##} Mbps · ↑ {up * toMbps:0.##} Mbps",
                metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    /// <summary>
    /// Returns the session cookie, or null when the Web UI is configured to skip
    /// authentication for this subnet — a common home lab setup, and not an error.
    /// </summary>
    private static async Task<string?> SignInAsync(HttpClient http, Connection connection, string baseUrl, CancellationToken ct)
    {
        var username = connection.Settings.Get("username");
        if (username.Length == 0)
            return null;

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = connection.Settings.Get("password"),
        });

        using var response = await http.PostAsync($"{baseUrl}/api/v2/auth/login", form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        // qBittorrent answers 200 with the body "Fails." on bad credentials — and gives the
        // same answer when its Host header check refuses the request, which is what happens
        // the first time anyone addresses it by container name. Same symptom, different fix,
        // so name both.
        if (body.Contains("Fail", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "qBittorrent refused the login. If the username and password are right, it is probably the " +
                "Host header check: Options → Web UI → turn off \"Enable Host header validation\", or add " +
                "this address to the whitelist. It rejects requests addressed by container name until you do.");

        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var sid = cookies.FirstOrDefault(c => c.StartsWith("SID=", StringComparison.OrdinalIgnoreCase));
            if (sid is not null)
                return sid.Split(';')[0];
        }

        return null;
    }

    private static async Task TryCountTorrentsAsync(
        HttpClient http, string baseUrl, string? cookie, Dictionary<string, double> metrics, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v2/torrents/info");
            if (cookie is { Length: > 0 })
                request.Headers.TryAddWithoutValidation("Cookie", cookie);

            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var downloading = 0;
            var seeding = 0;

            foreach (var torrent in document.RootElement.EnumerateArray())
            {
                var state = torrent.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
                if (state.Contains("download", StringComparison.OrdinalIgnoreCase) && !state.StartsWith("paused"))
                    downloading++;
                else if (state.Contains("up", StringComparison.OrdinalIgnoreCase) && !state.StartsWith("paused"))
                    seeding++;
            }

            metrics["torrents_downloading"] = downloading;
            metrics["torrents_seeding"] = seeding;
        }
        catch
        {
            // Transfer rates alone are still a working check.
        }
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
