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

    /// <summary>
    /// Sonarr and Radarr are on v3; Lidarr, Readarr and Prowlarr are still on v1. Same
    /// API shape either way, so the version is the only thing that varies.
    /// </summary>
    protected virtual string ApiVersion => "v3";
    public string Category => "Media";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.50:8989", Required: true),
        new("api_key", "API key", FieldKind.Password, Required: true, Help: "Settings → General inside the app."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("queue_count", "Queued downloads"),
        new("latency_ms", "Response time", " ms"),
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

    /// <param name="Title">The series or film. Sonarr sends the show; the episode is separate.</param>
    /// <param name="Episode">"S02E05 · Title" for Sonarr, empty for Radarr.</param>
    public sealed record Upcoming(string Title, string Episode, DateTimeOffset When, bool HaveIt);

    /// <summary>
    /// What is due in the next few days. Every *arr publishes a calendar and nothing here
    /// read it, which is odd given "what's on this week" is the question a media dashboard
    /// exists to answer.
    /// </summary>
    public async Task<IReadOnlyList<Upcoming>> CalendarAsync(Connection connection, int days, CancellationToken ct)
    {
        var from = DateTimeOffset.Now.Date;
        var to = from.AddDays(Math.Clamp(days, 1, 60));

        using var doc = await GetAsync(connection,
            $"calendar?start={from:yyyy-MM-dd}&end={to:yyyy-MM-dd}&includeSeries=true&unmonitored=false", ct);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<Upcoming>();

        foreach (var record in doc.RootElement.EnumerateArray())
        {
            // Sonarr nests the show under "series" and dates it "airDateUtc"; Radarr puts
            // the film's own title at the top level and dates it by release. One shape each,
            // read tolerantly, so this method serves both.
            var series = record.TryGetProperty("series", out var show)
                && show.TryGetProperty("title", out var showTitle)
                    ? showTitle.GetString() ?? ""
                    : "";

            var own = record.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var title = series.Length > 0 ? series : own;
            if (title.Length == 0)
                continue;

            var episode = "";
            if (series.Length > 0)
            {
                var season = Number(record, "seasonNumber");
                var number = Number(record, "episodeNumber");
                episode = season is not null && number is not null
                    ? $"S{season:00}E{number:00}{(own.Length > 0 ? $" · {own}" : "")}"
                    : own;
            }

            if (When(record) is not { } when)
                continue;

            items.Add(new Upcoming(
                title,
                episode,
                when,
                record.TryGetProperty("hasFile", out var has) && has.ValueKind == JsonValueKind.True));
        }

        return [.. items.OrderBy(item => item.When)];
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    /// <summary>The date field differs per app, and some records carry more than one.</summary>
    private static DateTimeOffset? When(JsonElement record)
    {
        foreach (var name in (string[])["airDateUtc", "airDate", "digitalRelease", "physicalRelease", "inCinemas"])
        {
            if (record.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed))
                return parsed.ToLocalTime();
        }

        return null;
    }

    private async Task<JsonDocument> GetAsync(Connection connection, string path, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            throw new InvalidOperationException("No base URL configured.");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/{ApiVersion}/{path}");
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

public sealed class LidarrProvider(IHttpClientFactory httpFactory) : ArrProviderBase(httpFactory)
{
    public override string Type => "lidarr";
    public override string DisplayName => "Lidarr";
    public override string Icon => "🎵";
    public override string Description => "Version, reachability, and how many albums are downloading.";
    protected override string ApiVersion => "v1";
}

public sealed class ReadarrProvider(IHttpClientFactory httpFactory) : ArrProviderBase(httpFactory)
{
    public override string Type => "readarr";
    public override string DisplayName => "Readarr";
    public override string Icon => "📚";
    public override string Description => "Version, reachability, and how many books are downloading.";
    protected override string ApiVersion => "v1";
}

/// <summary>
/// Prowlarr manages indexers rather than downloads, so it has no queue. The base class
/// already treats a missing queue as "not news", which is exactly right here.
/// </summary>
public sealed class ProwlarrProvider(IHttpClientFactory httpFactory) : ArrProviderBase(httpFactory)
{
    public override string Type => "prowlarr";
    public override string DisplayName => "Prowlarr";
    public override string Icon => "🔎";
    public override string Description => "Version and reachability for your indexer manager.";
    protected override string ApiVersion => "v1";
}
