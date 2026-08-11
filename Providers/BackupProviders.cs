using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Proxmox Backup Server. The number that matters is not the datastore's size but the age
/// of the newest snapshot in it: a backup job that stopped a fortnight ago leaves a
/// perfectly healthy-looking datastore behind, and nothing else on a dashboard notices.
/// </summary>
public sealed class ProxmoxBackupProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "pbs";
    public string DisplayName => "Proxmox Backup Server";
    public string Icon => "🗄️";
    public string Category => "Storage";
    public string Description => "Datastore usage, how many snapshots there are, and how long since the newest one.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "https://192.168.1.60:8007", Required: true),

        new("token_id", "API token ID", FieldKind.Text, "monitor@pbs!labbytwo", Required: true,
            Help: "Configuration → Access Control → API Tokens. The whole thing, including the ! part. " +
                  "DatastoreAudit on / is enough — do not give it more."),

        new("token_secret", "API token secret", FieldKind.Password, Required: true),

        new("datastore", "Datastore", FieldKind.Text,
            Help: "Optional. Blank watches every datastore and reports the worst of them."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("disk_percent", "Fullest datastore", "%", 1),
        new("free_gb", "Free space", " GB", 1),
        new("snapshots", "Snapshots"),
        new("hours_since_backup", "Since the last backup", " h", 1),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Backups have stopped", "hours_since_backup", Comparison.Above, 36, ForMinutes: 30,
            Why: "The whole point of a backup server. 36 hours suits a nightly job without firing on a late one."),

        new("Datastore nearly full", "disk_percent", Comparison.Above, 90, ClearThreshold: 85, ForMinutes: 30,
            Why: "A full datastore fails tonight's backup, quietly, at three in the morning."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No base URL configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var document = await GetAsync(connection, $"{baseUrl}/api2/json/status/datastore-usage", ct);
            stopwatch.Stop();

            if (!document.RootElement.TryGetProperty("data", out var stores) || stores.ValueKind != JsonValueKind.Array)
                return ProbeResult.Down(stopwatch.Elapsed, "No datastore data in the reply.");

            var wanted = connection.Settings.Get("datastore");
            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };

            double worstPercent = 0, leastFree = double.MaxValue;
            var names = new List<string>();

            foreach (var store in stores.EnumerateArray())
            {
                var name = store.TryGetProperty("store", out var label) ? label.GetString() ?? "" : "";
                if (wanted.Length > 0 && !string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                    continue;

                names.Add(name);

                var total = Number(store, "total") ?? 0;
                var used = Number(store, "used") ?? 0;
                var available = Number(store, "avail") ?? (total - used);

                if (total > 0)
                    worstPercent = Math.Max(worstPercent, used / total * 100);
                leastFree = Math.Min(leastFree, available);
            }

            if (names.Count == 0)
                return ProbeResult.Down(stopwatch.Elapsed,
                    wanted.Length > 0 ? $"No datastore called \"{wanted}\"." : "This server has no datastores.");

            metrics["disk_percent"] = worstPercent;
            if (leastFree < double.MaxValue)
                metrics["free_gb"] = leastFree / 1024 / 1024 / 1024;

            // Snapshot ages come from a second call per datastore, so only the named one —
            // or the first — is asked. Every datastore on a big server would be a lot of
            // requests every thirty seconds for a number that moves once a day.
            try
            {
                var store = wanted.Length > 0 ? wanted : names[0];
                using var snapshots = await GetAsync(connection,
                    $"{baseUrl}/api2/json/admin/datastore/{Uri.EscapeDataString(store)}/snapshots", ct);

                if (snapshots.RootElement.TryGetProperty("data", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    metrics["snapshots"] = list.GetArrayLength();

                    var newest = list.EnumerateArray()
                        .Select(item => Number(item, "backup-time") ?? 0)
                        .DefaultIfEmpty(0)
                        .Max();

                    if (newest > 0)
                    {
                        var at = DateTimeOffset.FromUnixTimeSeconds((long)newest);
                        metrics["hours_since_backup"] = Math.Max(0, (DateTimeOffset.Now - at).TotalHours);
                    }
                }
            }
            catch (Exception)
            {
                // Usage is still worth reporting when the token cannot list snapshots.
            }

            var message = metrics.TryGetValue("hours_since_backup", out var age)
                ? $"Newest backup {age:0.#} h ago, {worstPercent:0.#}% used"
                : $"{worstPercent:0.#}% used";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private async Task<JsonDocument> GetAsync(Connection connection, string url, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // PBS wants its own scheme rather than Bearer: PBSAPIToken=<id>:<secret>.
        request.Headers.TryAddWithoutValidation("Authorization",
            $"PBSAPIToken={connection.Settings.Get("token_id")}:{connection.Settings.Get("token_secret")}");

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(
                "PBS refused the token. The ID has to include the !name part, and the token needs " +
                "DatastoreAudit on / to see anything.");

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}

/// <summary>
/// Duplicati. Same idea as PBS and the same headline number — when did a backup last
/// finish, and did it finish well — for the people whose backups run from a NAS rather
/// than a hypervisor.
/// </summary>
public sealed class DuplicatiProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "duplicati";
    public string DisplayName => "Duplicati";
    public string Icon => "🧷";
    public string Category => "Storage";
    public string Description => "Backup jobs, when each last ran, and whether the last run actually succeeded.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.61:8200", Required: true),

        new("password", "UI password", FieldKind.Password,
            Help: "Only if the web interface asks for one. Duplicati's API accepts it as an " +
                  "X-XSRF-Token-less basic login on recent builds; leave blank for an open instance."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("jobs", "Backup jobs"),
        new("jobs_failed", "Jobs whose last run failed"),
        new("hours_since_backup", "Since the last backup", " h", 1),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("A backup failed", "jobs_failed", Comparison.Above, 0, ForMinutes: 10,
            Why: "Duplicati emails on failure only if you set that up, and one failed run is how a chain of them starts."),

        new("Backups have stopped", "hours_since_backup", Comparison.Above, 36, ForMinutes: 30,
            Why: "Catches the case a failure alert cannot: a job that is not running at all any more."),
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
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/backups");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (connection.Settings.Get("password") is { Length: > 0 } password)
                request.Headers.TryAddWithoutValidation("X-XSRF-Token", password);

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized)
                return ProbeResult.Down(stopwatch.Elapsed,
                    "Duplicati wants a password. Settings → Access to the user interface, and paste it here.");

            if (!response.IsSuccessStatusCode)
                return ProbeResult.Down(stopwatch.Elapsed, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return ProbeResult.Down(stopwatch.Elapsed, "Duplicati answered, but with no backup list.");

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            var jobs = 0;
            var failed = 0;
            DateTimeOffset? newest = null;

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                // Each entry is {"Backup": {...}, "Schedule": {...}}.
                if (!entry.TryGetProperty("Backup", out var backup))
                    continue;

                jobs++;

                if (!backup.TryGetProperty("Metadata", out var metadata))
                    continue;

                if (When(metadata, "LastBackupFinished") is { } finished && (newest is null || finished > newest))
                    newest = finished;

                // Duplicati records the last result as a word: Success, Warning, Error, Fatal.
                if (metadata.TryGetProperty("LastBackupStarted", out _)
                    && metadata.TryGetProperty("LastErrorMessage", out var error)
                    && error.GetString() is { Length: > 0 })
                    failed++;
            }

            metrics["jobs"] = jobs;
            metrics["jobs_failed"] = failed;
            if (newest is { } last)
                metrics["hours_since_backup"] = Math.Max(0, (DateTimeOffset.Now - last).TotalHours);

            var message = jobs == 0
                ? "No backup jobs configured"
                : failed > 0
                    ? $"{failed} of {jobs} job{(jobs == 1 ? "" : "s")} last failed"
                    : metrics.TryGetValue("hours_since_backup", out var age)
                        ? $"{jobs} job{(jobs == 1 ? "" : "s")}, newest run {age:0.#} h ago"
                        : $"{jobs} job{(jobs == 1 ? "" : "s")}";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private static DateTimeOffset? When(JsonElement metadata, string name) =>
        metadata.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
