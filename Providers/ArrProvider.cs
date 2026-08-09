using System.Diagnostics;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Sonarr and Radarr share an API shape, so they share an implementation and differ only
/// in the labels and the "what's in the queue" wording.
/// </summary>
public abstract class ArrProviderBase(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public abstract string Type { get; }
    public abstract string DisplayName { get; }
    public abstract string Icon { get; }
    public abstract string Description { get; }
    public string Category => "Media";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.50:8989", Required: true),
        new("api_key", "API key", FieldKind.Password, Required: true, Help: "Settings → General inside the app."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("queue_count", "Queued downloads"),
        new("latency_ms", "Response time", "ms"),
    ];

    public sealed record QueueItem(string Title, string Status, double PercentDone, string TimeLeft);

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var status = await GetAsync(connection, "system/status", ct);
            var version = status.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            var metrics = new Dictionary<string, double>();

            try
            {
                var queue = await QueueAsync(connection, ct);
                metrics["queue_count"] = queue.Count;
            }
            catch
            {
                // An older or busy instance can refuse the queue call; the app is still up.
            }

            stopwatch.Stop();
            metrics["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds;
            var details = version is null ? null : new Dictionary<string, string> { ["Version"] = version };
            return ProbeResult.Up(stopwatch.Elapsed, version is null ? "Connected" : $"v{version}", metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    public async Task<IReadOnlyList<QueueItem>> QueueAsync(Connection connection, CancellationToken ct)
    {
        using var doc = await GetAsync(connection, "queue?pageSize=50", ct);
        var records = doc.RootElement.TryGetProperty("records", out var wrapped) ? wrapped : doc.RootElement;
        if (records.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<QueueItem>();
        foreach (var record in records.EnumerateArray())
        {
            var title = record.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var size = record.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
            var left = record.TryGetProperty("sizeleft", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : 0;
            items.Add(new QueueItem(
                title,
                record.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                size > 0 ? (size - left) / size * 100 : 0,
                record.TryGetProperty("timeleft", out var tl) ? tl.GetString() ?? "" : ""));
        }
        return items;
    }

    private async Task<JsonDocument> GetAsync(Connection connection, string path, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            throw new InvalidOperationException("No base URL configured.");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/{path}");
        request.Headers.TryAddWithoutValidation("X-Api-Key", connection.Settings.Get("api_key"));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("The API key was rejected.");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}

public sealed class SonarrProvider(IHttpClientFactory httpFactory) : ArrProviderBase(httpFactory)
{
    public override string Type => "sonarr";
    public override string DisplayName => "Sonarr";
    public override string Icon => "📺";
    public override string Description => "TV automation — version, reachability, and how many episodes are downloading.";
}

public sealed class RadarrProvider(IHttpClientFactory httpFactory) : ArrProviderBase(httpFactory)
{
    public override string Type => "radarr";
    public override string DisplayName => "Radarr";
    public override string Icon => "🎞️";
    public override string Description => "Movie automation — version, reachability, and how many movies are downloading.";
}
