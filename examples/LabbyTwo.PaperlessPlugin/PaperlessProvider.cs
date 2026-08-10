using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Providers;

namespace LabbyTwo.PaperlessPlugin;

/// <summary>
/// Paperless-ngx — how many documents are filed and how many are still sitting in the
/// inbox waiting to be tagged.
///
/// The point of this example is the pair: a provider that monitors, plus a widget that
/// shows something a number cannot. The widget calls <see cref="RecentAsync"/> directly,
/// which is why that method is public — the host registers every provider as a singleton
/// under its own concrete type, so a component can just inject it.
/// </summary>
public sealed class PaperlessProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "paperless";
    public string DisplayName => "Paperless-ngx";
    public string Icon => "📄";
    public string Category => "Documents";
    public string Description => "Document count, inbox backlog, and the documents added most recently.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.20:8000", Required: true),
        new("token", "API token", FieldKind.Password, Required: true,
            Help: "Settings → My Profile → API Auth Token inside Paperless."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("documents_total", "Documents"),
        new("documents_inbox", "In the inbox"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        // Twelve hours, not five minutes: an inbox is meant to have things in it. This
        // fires when a backlog is being ignored, not when the scanner runs.
        new("Inbox is piling up", "documents_inbox", Comparison.Above, 20, ClearThreshold: 5,
            ForMinutes: 720, Why: "Documents have been waiting to be tagged for half a day."),
    ];

    public sealed record Document(int Id, string Title, DateTimeOffset Added);

    /// <summary>Where a browser should go — the same host the API is on.</summary>
    public static string LinkBase(Connection connection) => connection.Settings.Get("url").TrimEnd('/');

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var stats = await GetAsync(connection, "/api/statistics/", ct);
            stopwatch.Stop();

            var metrics = new Dictionary<string, double>
            {
                ["documents_total"] = Number(stats.RootElement, "documents_total"),
                ["documents_inbox"] = Number(stats.RootElement, "documents_inbox"),
                ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
            };

            var inbox = metrics["documents_inbox"];
            return ProbeResult.Up(
                stopwatch.Elapsed,
                $"{metrics["documents_total"]:0} documents, {inbox:0} in the inbox",
                metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, Explain(ex, connection));
        }
    }

    /// <summary>The most recently added documents, for the widget.</summary>
    public async Task<IReadOnlyList<Document>> RecentAsync(Connection connection, int count, CancellationToken ct)
    {
        // Ordered by "added" rather than "created": created is the date on the paper,
        // which for a bank statement filed today may be years ago.
        using var doc = await GetAsync(connection, $"/api/documents/?ordering=-added&page_size={count}", ct);

        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
            return [];

        return
        [
            .. results.EnumerateArray().Select(item => new Document(
                (int)Number(item, "id"),
                Text(item, "title"),
                Date(item, "added") ?? DateTimeOffset.MinValue))
        ];
    }

    private static string Explain(Exception ex, Connection connection) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            "Paperless rejected the token. Check it under Settings → My Profile → API Auth Token.",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            "That URL answered, but not with the Paperless API. Point this at the root of the site, " +
            "not at a page inside it.",
        _ => ProbeError.Describe(ex, connection.Settings.Get("url")),
    };

    private async Task<JsonDocument> GetAsync(Connection connection, string path, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            throw new InvalidOperationException("No base URL configured.");

        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);

        // Paperless wants the literal scheme "Token", not "Bearer".
        request.Headers.TryAddWithoutValidation("Authorization", $"Token {connection.Settings.Get("token")}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static DateTimeOffset? Date(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed.ToLocalTime()
            : null;
}
