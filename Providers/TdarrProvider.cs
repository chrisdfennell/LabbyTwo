using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Tdarr. Its API is unusual enough to explain: almost everything goes through one
/// endpoint, <c>POST /api/v2/cruddb</c>, with a body naming a collection and a mode —
/// there is no REST surface to speak of. That is probably why nothing integrates it, and
/// it is a fixed shape once written down.
///
/// The number worth watching is the transcode queue. Tdarr failing is loud; Tdarr quietly
/// falling behind, with a queue that only grows, is what actually happens.
/// </summary>
public sealed class TdarrProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "tdarr";
    public string DisplayName => "Tdarr";
    public string Icon => "🎞️";
    public string Category => "Media";
    public string Description => "Transcode and health-check queues, workers busy, and how much space it has saved.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.32:8265", Required: true,
            Help: "The Tdarr server's web interface, not a node."),

        new("api_key", "API key", FieldKind.Password,
            Help: "Only if you have turned authentication on. Blank is right for most installs."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("queue_transcode", "Waiting to transcode"),
        new("queue_health", "Waiting for a health check"),
        new("files", "Files known"),
        new("transcodes_done", "Transcoded"),
        new("saved_gb", "Space saved", " GB", 1),
        new("workers", "Workers busy"),
        new("errors", "Errored"),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("The transcode queue is running away", "queue_transcode", Comparison.Above, 500, ForMinutes: 60,
            Why: "Tdarr falling behind looks exactly like Tdarr working. Set it above your normal backlog."),

        new("Nothing is being worked on", "workers", Comparison.Below, 1, ForMinutes: 120,
            Why: "Two hours of no workers with a queue waiting means the nodes have gone away."),
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

            // The statistics document holds every counter the UI shows on its front page.
            using var stats = await CruddbAsync(connection, baseUrl,
                """{"data":{"collection":"StatisticsJSONDB","mode":"getById","docID":"statistics","obj":{}}}""", ct);

            var root = stats.RootElement;

            Copy(root, "totalFileCount", metrics, "files");
            Copy(root, "totalTranscodeCount", metrics, "transcodes_done");
            Copy(root, "table1Count", metrics, "queue_transcode");
            Copy(root, "table4Count", metrics, "queue_health");
            Copy(root, "table3Count", metrics, "errors");

            // Reported in gigabytes already, and negative when transcoding made things
            // bigger — which is a real outcome and worth showing rather than clamping.
            Copy(root, "sizeDiff", metrics, "saved_gb");

            // Workers live on the nodes rather than in the statistics document.
            try
            {
                using var nodes = await GetAsync(connection, $"{baseUrl}/api/v2/get-nodes", ct);
                double busy = 0;

                if (nodes.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var node in nodes.RootElement.EnumerateObject())
                    {
                        if (node.Value.TryGetProperty("workers", out var workers)
                            && workers.ValueKind == JsonValueKind.Object)
                            busy += workers.EnumerateObject().Count();
                    }
                }

                metrics["workers"] = busy;
            }
            catch (Exception)
            {
                // Counters are still worth having on an install where this endpoint moved.
            }

            stopwatch.Stop();
            metrics["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds;

            var queued = metrics.GetValueOrDefault("queue_transcode");
            var message = queued > 0
                ? $"{queued:N0} queued, {metrics.GetValueOrDefault("workers"):0} worker(s) busy"
                : $"{metrics.GetValueOrDefault("files"):N0} files, nothing queued";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private async Task<JsonDocument> CruddbAsync(Connection connection, string baseUrl, string body, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v2/cruddb")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        Authorise(request, connection);

        using var response = await http.SendAsync(request, ct);
        Check(response);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<JsonDocument> GetAsync(Connection connection, string url, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Authorise(request, connection);

        using var response = await http.SendAsync(request, ct);
        Check(response);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static void Authorise(HttpRequestMessage request, Connection connection)
    {
        if (connection.Settings.Get("api_key") is { Length: > 0 } key)
            request.Headers.TryAddWithoutValidation("x-api-key", key);
    }

    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(
                "Tdarr refused the request. If you have authentication switched on, put its API key in; " +
                "if you have not, leave the key blank.");

        response.EnsureSuccessStatusCode();
    }

    private static void Copy(JsonElement element, string from, Dictionary<string, double> metrics, string to)
    {
        if (element.TryGetProperty(from, out var value) && value.ValueKind == JsonValueKind.Number)
            metrics[to] = value.GetDouble();
    }
}

/// <summary>
/// Mylar3, for comics. Its API is the older Python style — one endpoint, a command in the
/// query string — and it answers with the whole library, so the counting happens here.
/// </summary>
public sealed class Mylar3Provider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "mylar3";
    public string DisplayName => "Mylar3";
    public string Icon => "📚";
    public string Category => "Media";
    public string Description => "Series tracked, issues wanted, and how many are still missing.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.33:8090", Required: true),
        new("api_key", "API key", FieldKind.Password, Required: true,
            Help: "Settings → Web Interface → API. Enable the API there first; it is off by default."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("series", "Series"),
        new("wanted", "Issues wanted"),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("The wanted list keeps growing", "wanted", Comparison.Above, 50, ForMinutes: 60,
            Why: "Comics that stay wanted are usually a search provider that has quietly stopped working."),
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
            var metrics = new Dictionary<string, double>();

            using var index = await ReadAsync(http, $"{baseUrl}/api?apikey={key}&cmd=getIndex", ct);
            metrics["series"] = Count(index.RootElement);

            try
            {
                using var wanted = await ReadAsync(http, $"{baseUrl}/api?apikey={key}&cmd=getWanted", ct);
                metrics["wanted"] = Count(wanted.RootElement);
            }
            catch (Exception)
            {
                // Older builds spell this differently; the library count still stands.
            }

            stopwatch.Stop();
            metrics["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds;

            return ProbeResult.Up(stopwatch.Elapsed,
                $"{metrics.GetValueOrDefault("series"):N0} series, {metrics.GetValueOrDefault("wanted"):N0} wanted",
                metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private static async Task<JsonDocument> ReadAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        // A wrong key answers 200 with a plain-text complaint rather than JSON.
        if (body.TrimStart().StartsWith('{') is false && body.TrimStart().StartsWith('[') is false)
            throw new InvalidOperationException($"Mylar3 said: {body[..Math.Min(body.Length, 120)].Trim()}");

        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// The reply is either a bare array or one wrapped in a "data" property, depending on
    /// the command and the version.
    /// </summary>
    private static double Count(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.GetArrayLength();

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
        {
            return data.ValueKind switch
            {
                JsonValueKind.Array => data.GetArrayLength(),
                JsonValueKind.Object => data.EnumerateObject().Count(),
                _ => 0,
            };
        }

        return 0;
    }
}
