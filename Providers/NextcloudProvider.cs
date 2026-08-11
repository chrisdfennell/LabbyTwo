using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Nextcloud, through the Server info app's OCS endpoint — the one page it publishes for
/// exactly this purpose, so no scraping and no admin session.
///
/// It needs the <b>Server info</b> app enabled, and either its monitoring token or an
/// admin account. The token is the better half of that choice: it is read-only, it is one
/// line in <c>config.php</c>, and it is not a login.
/// </summary>
public sealed class NextcloudProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "nextcloud";
    public string DisplayName => "Nextcloud";
    public string Icon => "☁️";
    public string Category => "Storage";
    public string Description => "Users, files, free space and load from the Server info app.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "https://cloud.example.com", Required: true,
            Help: "The address you log in at. The OCS path is added for you."),

        new("token", "Monitoring token", FieldKind.Password,
            Help: "Preferred. Settings → Administration → Monitoring shows it, or set " +
                  "'updatechecker' style config: occ config:app:set serverinfo token --value <something>."),

        new("username", "Admin username", FieldKind.Text,
            Help: "Only needed if you would rather not use a token. An app password works here."),

        new("password", "Admin password", FieldKind.Password),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("users_total", "Users"),
        new("users_active_24h", "Active today"),
        new("files", "Files"),
        new("free_gb", "Free space", " GB", 1),
        new("mem_percent", "Memory used", "%", 1),
        new("cpu_load", "Load average", "", 2),
        new("shares", "Shares"),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Nextcloud running out of space", "free_gb", Comparison.Below, 20, ClearThreshold: 30, ForMinutes: 30,
            Why: "Below this, syncing clients start failing uploads — usually the first anyone hears of it."),
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
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{baseUrl}/ocs/v2.php/apps/serverinfo/api/v1/info?format=json");

            // OCS refuses anything without this header, with a redirect to the login page
            // rather than an error — which presents as HTML where JSON was expected.
            request.Headers.TryAddWithoutValidation("OCS-APIRequest", "true");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var token = connection.Settings.Get("token");
            if (token.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("NC-Token", token);
            }
            else if (connection.Settings.Get("username") is { Length: > 0 } user)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(
                        $"{user}:{connection.Settings.Get("password")}")));
            }

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return ProbeResult.Down(stopwatch.Elapsed,
                    "Nextcloud refused those credentials. A monitoring token or an admin account is needed, " +
                    "and if two-factor is on, use an app password rather than the login one.");

            if (!response.IsSuccessStatusCode)
                return ProbeResult.Down(stopwatch.Elapsed, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            if (body.TrimStart().StartsWith('<'))
                return ProbeResult.Down(stopwatch.Elapsed,
                    "That answered with a web page rather than data — the Server info app is probably not enabled.");

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("ocs", out var ocs)
                || !ocs.TryGetProperty("data", out var data))
                return ProbeResult.Down(stopwatch.Elapsed, "No serverinfo data in the reply.");

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            var details = new Dictionary<string, string>();

            if (data.TryGetProperty("nextcloud", out var nextcloud))
            {
                if (nextcloud.TryGetProperty("system", out var system))
                {
                    if (Number(system, "freespace") is { } free)
                        metrics["free_gb"] = free / 1024d / 1024 / 1024;

                    // Reported in kB on some builds and bytes on others. A percentage of one
                    // by the other is right either way, which is why this is a percentage.
                    if (Number(system, "mem_total") is { } memory and > 0 && Number(system, "mem_free") is { } spare)
                        metrics["mem_percent"] = (memory - spare) / memory * 100;

                    if (system.TryGetProperty("cpuload", out var load)
                        && load.ValueKind == JsonValueKind.Array && load.GetArrayLength() > 0
                        && load[0].ValueKind == JsonValueKind.Number)
                        metrics["cpu_load"] = load[0].GetDouble();

                    if (system.TryGetProperty("version", out var version) && version.GetString() is { } text)
                        details["Version"] = text;
                }

                if (nextcloud.TryGetProperty("storage", out var storage))
                {
                    if (Number(storage, "num_users") is { } users)
                        metrics["users_total"] = users;
                    if (Number(storage, "num_files") is { } files)
                        metrics["files"] = files;
                }

                if (nextcloud.TryGetProperty("shares", out var shares)
                    && Number(shares, "num_shares") is { } shareCount)
                    metrics["shares"] = shareCount;
            }

            if (data.TryGetProperty("activeUsers", out var active) && Number(active, "last24hours") is { } day)
                metrics["users_active_24h"] = day;

            var message = metrics.TryGetValue("users_total", out var total)
                ? $"{total:0} users, {metrics.GetValueOrDefault("free_gb"):0.#} GB free"
                : "Connected";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    /// <summary>serverinfo returns some numbers as strings, depending on the value and the version.</summary>
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
}
