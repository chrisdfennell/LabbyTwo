using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Audiobookshelf. Books and audiobooks, and — the part worth having on a dashboard — how
/// many are part-listened, because that is the number that tells you the library is being
/// used rather than merely large.
/// </summary>
public sealed class AudiobookshelfProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "audiobookshelf";
    public string DisplayName => "Audiobookshelf";
    public string Icon => "🎧";
    public string Category => "Media";
    public string Description => "Libraries, items and how many are in progress on an Audiobookshelf server.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.80:13378", Required: true),
        new("token", "API token", FieldKind.Password, Required: true,
            Help: "Settings → Users → your user → the API token at the bottom of the page."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("libraries", "Libraries"),
        new("items", "Items"),
        new("in_progress", "Part listened"),
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
            var metrics = new Dictionary<string, double>();

            using var libraries = await GetAsync(http, connection, $"{baseUrl}/api/libraries", ct);
            var list = libraries.RootElement.TryGetProperty("libraries", out var wrapped)
                ? wrapped
                : libraries.RootElement;

            var count = list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 0;
            metrics["libraries"] = count;

            // Item counts are per library, so one call each. A home library is two or three
            // of them, not fifty, and doing it here keeps the card free of its own polling.
            double items = 0;
            if (list.ValueKind == JsonValueKind.Array)
            {
                foreach (var library in list.EnumerateArray())
                {
                    if (library.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } key)
                    {
                        try
                        {
                            using var stats = await GetAsync(http, connection,
                                $"{baseUrl}/api/libraries/{Uri.EscapeDataString(key)}/stats", ct);

                            if (stats.RootElement.TryGetProperty("totalItems", out var total)
                                && total.ValueKind == JsonValueKind.Number)
                                items += total.GetDouble();
                        }
                        catch (Exception)
                        {
                            // One library that will not answer should not lose the others.
                        }
                    }
                }
            }

            metrics["items"] = items;

            try
            {
                using var progress = await GetAsync(http, connection, $"{baseUrl}/api/me/items-in-progress", ct);
                if (progress.RootElement.TryGetProperty("libraryItems", out var inProgress)
                    && inProgress.ValueKind == JsonValueKind.Array)
                    metrics["in_progress"] = inProgress.GetArrayLength();
            }
            catch (Exception)
            {
                // Optional.
            }

            stopwatch.Stop();
            metrics["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds;

            return ProbeResult.Up(stopwatch.Elapsed,
                $"{items:N0} item{(items == 1 ? "" : "s")} in {count} librar{(count == 1 ? "y" : "ies")}", metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private static async Task<JsonDocument> GetAsync(
        HttpClient http, Connection connection, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Settings.Get("token"));

        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(
                "Audiobookshelf rejected the token. It is on your user's page under Settings → Users, " +
                "not in the server settings.");

        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}

/// <summary>
/// Komga, for comics and manga. Plain basic auth and a REST API that answers the only
/// question worth putting on a wall: how much is in there, and how much is unread.
/// </summary>
public sealed class KomgaProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "komga";
    public string DisplayName => "Komga";
    public string Icon => "📖";
    public string Category => "Media";
    public string Description => "Series, books and how many are still unread.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.82:25600", Required: true),
        new("username", "Username", FieldKind.Text, Required: true, Help: "The email address you log in with."),
        new("password", "Password", FieldKind.Password, Required: true,
            Help: "An API key works here too, in place of the password."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("series", "Series"),
        new("books", "Books"),
        new("unread", "Unread"),
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
            var metrics = new Dictionary<string, double>();

            // size=1 because only the total is wanted; Komga returns it beside the page.
            metrics["series"] = await CountAsync(http, connection, $"{baseUrl}/api/v1/series?size=1", ct);
            metrics["books"] = await CountAsync(http, connection, $"{baseUrl}/api/v1/books?size=1", ct);

            try
            {
                metrics["unread"] = await CountAsync(http, connection,
                    $"{baseUrl}/api/v1/books?size=1&read_status=UNREAD", ct);
            }
            catch (Exception)
            {
                // Optional: older versions spell the filter differently.
            }

            stopwatch.Stop();
            metrics["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds;

            return ProbeResult.Up(stopwatch.Elapsed,
                $"{metrics.GetValueOrDefault("books"):N0} books in {metrics.GetValueOrDefault("series"):N0} series",
                metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private static async Task<double> CountAsync(
        HttpClient http, Connection connection, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                $"{connection.Settings.Get("username")}:{connection.Settings.Get("password")}")));

        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Komga refused the login. The username is the email address you sign in with.");

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.TryGetProperty("totalElements", out var total)
            && total.ValueKind == JsonValueKind.Number
                ? total.GetDouble()
                : 0;
    }
}

/// <summary>
/// Navidrome. Subsonic's API underneath, which means the interesting thing on a dashboard
/// is who is listening right now — a music server is either playing or it is furniture.
/// </summary>
public sealed class NavidromeProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "navidrome";
    public string DisplayName => "Navidrome";
    public string Icon => "🎵";
    public string Category => "Media";
    public string Description => "Albums, artists and who is playing something right now.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.81:4533", Required: true),
        new("username", "Username", FieldKind.Text, Required: true),
        new("password", "Password", FieldKind.Password, Required: true,
            Help: "Sent as the Subsonic API's plain-text parameter over your LAN. Use a dedicated account."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("albums", "Albums"),
        new("artists", "Artists"),
        new("songs", "Songs"),
        new("now_playing", "Playing now"),
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
            var metrics = new Dictionary<string, double>();

            using var scan = await GetAsync(http, connection, baseUrl, "getScanStatus", ct);
            var status = Subsonic(scan.RootElement);

            if (status.TryGetProperty("scanStatus", out var counts) && Number(counts, "count") is { } songs)
                metrics["songs"] = songs;

            using var playing = await GetAsync(http, connection, baseUrl, "getNowPlaying", ct);
            var now = Subsonic(playing.RootElement);
            var listeners = 0;

            if (now.TryGetProperty("nowPlaying", out var entries)
                && entries.TryGetProperty("entry", out var list)
                && list.ValueKind == JsonValueKind.Array)
                listeners = list.GetArrayLength();

            metrics["now_playing"] = listeners;

            stopwatch.Stop();
            metrics["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds;

            var message = listeners > 0
                ? $"{listeners} listening now"
                : metrics.TryGetValue("songs", out var count) ? $"{count:N0} songs" : "Connected";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private static async Task<JsonDocument> GetAsync(
        HttpClient http, Connection connection, string baseUrl, string method, CancellationToken ct)
    {
        // Subsonic wants the client name and protocol version on every call, and answers
        // XML unless asked for JSON.
        var url = $"{baseUrl}/rest/{method}" +
                  $"?u={Uri.EscapeDataString(connection.Settings.Get("username"))}" +
                  $"&p={Uri.EscapeDataString(connection.Settings.Get("password"))}" +
                  "&v=1.16.1&c=LabbyTwo&f=json";

        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        // Subsonic reports failures as a 200 with status="failed" and an error object.
        var root = Subsonic(document.RootElement);
        if (root.TryGetProperty("status", out var status) && status.GetString() == "failed")
        {
            var message = root.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var text)
                ? text.GetString() ?? "rejected the login"
                : "rejected the login";

            document.Dispose();
            throw new InvalidOperationException($"Navidrome {message}.");
        }

        return document;
    }

    private static JsonElement Subsonic(JsonElement root) =>
        root.TryGetProperty("subsonic-response", out var wrapped) ? wrapped : root;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
