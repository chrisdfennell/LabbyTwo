using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Speedtest Tracker — the thing that runs a speed test on a schedule and keeps the
/// history. LabbyTwo already knows how to chart any number a provider reports, so pulling
/// the latest result in gives you the graph you actually want out of an ISP argument:
/// throughput over weeks, next to everything else that was happening at the time.
///
/// The most useful number here turns out not to be the speed at all. It is
/// <c>result_age_hours</c>: a tracker whose scheduler has quietly stopped shows a fine
/// download figure forever, and only the age of it says the truth.
/// </summary>
public sealed class SpeedtestTrackerProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "speedtest-tracker";
    public string DisplayName => "Speedtest Tracker";
    public string Icon => "🚀";
    public string Category => "Network";
    public string Description => "Latest download, upload and ping from Speedtest Tracker, and how old that result is.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.30:8080", Required: true,
            Help: "Just the host and port — the API path is added for you."),

        new("token", "API token", FieldKind.Password,
            Help: "Speedtest Tracker → Settings → API Tokens. Older installs served the API without one; " +
                  "leave this blank if yours does."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("download_mbps", "Download", " Mbps", 1),
        new("upload_mbps", "Upload", " Mbps", 1),
        new("ping_ms", "Ping", " ms", 1),
        new("jitter_ms", "Jitter", " ms", 1),
        new("result_age_hours", "Last test", " h", 1),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Nothing like the speed you pay for", "download_mbps", Comparison.Below, 100, ForMinutes: 30,
            Why: "Set it to about half your plan. Half an hour avoids alerting on one bad test."),

        new("Tests have stopped running", "result_age_hours", Comparison.Above, 26, ForMinutes: 60,
            Why: "A stalled scheduler leaves yesterday's good result on screen indefinitely. " +
                 "26 hours suits a daily test without firing on a late one."),
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
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/results/latest");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (connection.Settings.Get("token") is { Length: > 0 } token)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return ProbeResult.Down(stopwatch.Elapsed,
                    "Speedtest Tracker refused the token. Settings → API Tokens, and paste a fresh one.");

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return ProbeResult.Down(stopwatch.Elapsed,
                    "No /api/v1/results/latest there. That path arrived in Speedtest Tracker 0.20 — " +
                    "an older install, or a different tool such as MySpeed, is better served by the " +
                    "JSON API provider, which reads numbers out of any endpoint you point it at.");

            if (!response.IsSuccessStatusCode)
                return ProbeResult.Down(stopwatch.Elapsed, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            using var document = JsonDocument.Parse(body);
            // Laravel wraps a single resource in "data"; take either shape.
            var result = document.RootElement.TryGetProperty("data", out var data) ? data : document.RootElement;

            if (result.ValueKind != JsonValueKind.Object)
                return ProbeResult.Down(stopwatch.Elapsed, "No results recorded yet — run a test first.");

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };

            if (Throughput(result, "download") is { } download)
                metrics["download_mbps"] = download;
            if (Throughput(result, "upload") is { } upload)
                metrics["upload_mbps"] = upload;
            if (Number(result, "ping") is { } ping)
                metrics["ping_ms"] = ping;
            if (Number(result, "jitter") is { } jitter)
                metrics["jitter_ms"] = jitter;

            if (When(result) is { } taken)
                metrics["result_age_hours"] = Math.Max(0, (DateTimeOffset.Now - taken).TotalHours);

            var message = metrics.TryGetValue("download_mbps", out var down)
                ? $"{down:0.#} down / {metrics.GetValueOrDefault("upload_mbps"):0.#} up Mbps"
                : "Connected";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    /// <summary>
    /// Speed in Mbps, whatever unit this version stored. Recent releases carry
    /// <c>download_bits</c>; older ones put bytes per second in <c>download</c>; and a
    /// value already small enough to be Mbps is taken at face value, because guessing
    /// wrong here shows up as a home connection running at 900 gigabits.
    /// </summary>
    private static double? Throughput(JsonElement result, string name)
    {
        if (Number(result, $"{name}_bits") is { } bits and > 0)
            return bits / 1_000_000d;

        if (Number(result, name) is not { } value || value <= 0)
            return null;

        return value < 1000 ? value : value * 8 / 1_000_000d;
    }

    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static DateTimeOffset? When(JsonElement result)
    {
        foreach (var name in (string[])["created_at", "updated_at", "timestamp"])
        {
            if (result.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return parsed;
        }
        return null;
    }
}
