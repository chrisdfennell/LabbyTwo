using System.Diagnostics;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Immich — the self-hosted photo library. Two things worth a tile: how much of the disk
/// the library has taken, which only ever goes one way, and how many photos are in it,
/// which is the number that tells you a phone has stopped backing up long before anyone
/// opens the app to check.
///
/// Immich moved its endpoints from <c>/api/server-info/…</c> to <c>/api/server/…</c>
/// around v1.118, so both are tried. A photo library is exactly the sort of thing people
/// run for years without updating.
/// </summary>
public sealed class ImmichProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "immich";
    public string DisplayName => "Immich";
    public string Icon => "🖼️";
    public string Category => "Media";
    public string Description => "Photos and videos in the library, what it is using on disk, and how full that disk is.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.40:2283", Required: true,
            Help: "The server address, without /api."),

        new("api_key", "API key", FieldKind.Password, Required: true,
            Help: "Immich → Account Settings → API Keys. Statistics need an admin account's key; " +
                  "a normal user's key can only see its own."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("photos", "Photos"),
        new("videos", "Videos"),
        new("library_gb", "Library size", " GB", 1),
        new("disk_percent", "Disk used", "%", 1),
        new("disk_free_gb", "Disk free", " GB", 1),
        new("users", "Users"),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Photo disk nearly full", "disk_percent", Comparison.Above, 90, ClearThreshold: 85, ForMinutes: 30,
            Why: "A library that cannot write is a phone quietly failing to back up."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No base URL configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var metrics = new Dictionary<string, double>();
            var details = new Dictionary<string, string>();

            using var statistics = await GetAsync(connection, baseUrl,
                ["/api/server/statistics", "/api/server-info/statistics"], ct);

            if (statistics is null)
            {
                stopwatch.Stop();
                return ProbeResult.Down(stopwatch.Elapsed,
                    "Immich answered, but not with statistics. That endpoint needs an admin API key.");
            }

            var stats = statistics.RootElement;
            if (Number(stats, "photos") is { } photos)
                metrics["photos"] = photos;
            if (Number(stats, "videos") is { } videos)
                metrics["videos"] = videos;
            if (Number(stats, "usage") is { } usage)
                metrics["library_gb"] = usage / 1024d / 1024 / 1024;
            if (stats.TryGetProperty("usageByUser", out var users) && users.ValueKind == JsonValueKind.Array)
                metrics["users"] = users.GetArrayLength();

            // Storage is a separate call and a nice-to-have: a library count is still worth
            // showing on an install where the disk endpoint has moved again.
            try
            {
                using var storage = await GetAsync(connection, baseUrl,
                    ["/api/server/storage", "/api/server-info/storage"], ct);

                if (storage is not null)
                {
                    var disk = storage.RootElement;
                    if (Number(disk, "diskUsagePercentage") is { } percent)
                        metrics["disk_percent"] = percent;
                    if (Number(disk, "diskAvailableRaw") is { } free)
                        metrics["disk_free_gb"] = free / 1024d / 1024 / 1024;
                }
            }
            catch (Exception)
            {
                // Deliberately swallowed — see above.
            }

            try
            {
                using var version = await GetAsync(connection, baseUrl,
                    ["/api/server/version", "/api/server-info/version"], ct);

                if (version is not null
                    && Number(version.RootElement, "major") is { } major
                    && Number(version.RootElement, "minor") is { } minor)
                {
                    details["Version"] = $"v{major:0}.{minor:0}.{Number(version.RootElement, "patch") ?? 0:0}";
                }
            }
            catch (Exception)
            {
                // Same again: a version string is decoration.
            }

            stopwatch.Stop();
            metrics["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds;

            var message = metrics.TryGetValue("photos", out var count)
                ? $"{count:N0} photos, {metrics.GetValueOrDefault("videos"):N0} videos"
                : "Connected";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    /// <summary>
    /// Tries each path in turn and returns the first that answers. A 404 means "this
    /// version calls it something else"; anything else is a real failure and is thrown.
    /// </summary>
    private async Task<JsonDocument?> GetAsync(
        Connection connection, string baseUrl, string[] paths, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);

        foreach (var path in paths)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
            request.Headers.TryAddWithoutValidation("x-api-key", connection.Settings.Get("api_key"));
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await http.SendAsync(request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                continue;

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new InvalidOperationException(
                    "Immich refused the API key. Statistics are admin-only — make the key on an admin account.");

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }

        return null;
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
