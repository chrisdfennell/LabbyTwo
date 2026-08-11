using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Synology DSM. Shaped like the QNAP provider on purpose — a session per connection,
/// every field read tolerantly — because DSM's JSON moves between versions in the same way
/// QTS's XML does, and because these two are what most people mean by "my NAS".
/// </summary>
public sealed class SynologyProvider(IHttpClientFactory httpFactory, ILogger<SynologyProvider> log) : IConnectionProvider
{
    public string Type => "synology";
    public string DisplayName => "Synology NAS";
    public string Icon => "🖴";
    public string Category => "Storage";
    public string Description =>
        "DSM system info, temperature, load and volume usage. Make a read-only account — accounts with 2FA cannot use this API.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("host", "Host", FieldKind.Text, "192.168.1.55", Required: true),
        new("port", "Port", FieldKind.Number, Default: "5000", Help: "5000 for http, 5001 for https by default."),
        new("https", "Use HTTPS", FieldKind.Bool, Default: "false"),
        new("username", "Username", FieldKind.Text, Required: true,
            Help: "A dedicated account in the users group is enough, and safer than an administrator."),
        new("password", "Password", FieldKind.Password, Required: true),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("cpu_percent", "CPU", "%", 1),
        new("ram_percent", "Memory", "%", 1),
        new("temp_c", "Temperature", "°C", 1),
        new("disk_percent", "Fullest volume", "%", 1),
        new("uptime_days", "Uptime", " days", 1),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Volume nearly full", "disk_percent", Comparison.Above, 90, ClearThreshold: 85, ForMinutes: 10,
            Why: "The one that actually loses data if ignored."),

        new("Running hot", "temp_c", Comparison.Above, 60, ClearThreshold: 55, ForMinutes: 15,
            Why: "Usually a failed fan or a blocked vent."),
    ];

    private readonly ConcurrentDictionary<string, string> _sessions = new();

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        if (connection.Settings.Get("host").Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No host configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var metrics = new Dictionary<string, double>();
            var details = new Dictionary<string, string>();

            var info = await GetAsync(connection,
                "entry.cgi?api=SYNO.Core.System&version=1&method=info", ct);

            if (info.TryGetProperty("model", out var model) && model.GetString() is { Length: > 0 } name)
                details["Model"] = name;
            if (info.TryGetProperty("firmware_ver", out var firmware) && firmware.GetString() is { Length: > 0 } version)
                details["DSM"] = version;

            if (Number(info, "temperature") is { } temperature)
                metrics["temp_c"] = temperature;
            if (Number(info, "up_time") is { } seconds)
                metrics["uptime_days"] = seconds / 86400;
            else if (info.TryGetProperty("up_time", out var text) && Uptime(text.GetString()) is { } parsed)
                metrics["uptime_days"] = parsed;

            // Load and volumes are separate APIs, and either can be missing on an older DSM
            // or an account without permission. Neither is worth failing the whole probe.
            try
            {
                var usage = await GetAsync(connection,
                    "entry.cgi?api=SYNO.Core.System.Utilization&version=1&method=get", ct);

                if (usage.TryGetProperty("cpu", out var cpu))
                {
                    var user = Number(cpu, "user_load") ?? 0;
                    var system = Number(cpu, "system_load") ?? 0;
                    metrics["cpu_percent"] = user + system;
                }

                if (usage.TryGetProperty("memory", out var memory) && Number(memory, "real_usage") is { } used)
                    metrics["ram_percent"] = used;
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Utilisation unavailable for {Connection}", connection.Name);
            }

            try
            {
                var storage = await GetAsync(connection,
                    "entry.cgi?api=SYNO.Storage.CGI.Storage&version=1&method=load_info", ct);

                if (storage.TryGetProperty("volumes", out var volumes) && volumes.ValueKind == JsonValueKind.Array)
                {
                    double worst = 0;
                    foreach (var volume in volumes.EnumerateArray())
                    {
                        if (!volume.TryGetProperty("size", out var size))
                            continue;

                        // DSM returns these as strings of bytes, which is why they are parsed
                        // rather than read as numbers.
                        var total = Bytes(size, "total");
                        var used = Bytes(size, "used");
                        if (total > 0)
                            worst = Math.Max(worst, used / total * 100);
                    }

                    if (worst > 0)
                        metrics["disk_percent"] = worst;
                }
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Volume usage unavailable for {Connection}", connection.Name);
            }

            stopwatch.Stop();
            metrics["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds;

            var message = details.TryGetValue("Model", out var reported) ? reported : "Connected";
            return ProbeResult.Up(stopwatch.Elapsed, message, metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _sessions.TryRemove(connection.Id, out _);
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("host")));
        }
    }

    private string BaseUrl(Connection connection)
    {
        var https = connection.Settings.GetBool("https");
        var port = connection.Settings.GetInt("port", https ? 5001 : 5000);
        return $"{(https ? "https" : "http")}://{connection.Settings.Get("host")}:{port}/webapi/";
    }

    private async Task<string> SessionIdAsync(Connection connection, CancellationToken ct)
    {
        if (_sessions.TryGetValue(connection.Id, out var cached))
            return cached;

        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        var url = $"{BaseUrl(connection)}auth.cgi?api=SYNO.API.Auth&version=3&method=login&session=Core&format=sid" +
                  $"&account={Uri.EscapeDataString(connection.Settings.Get("username"))}" +
                  $"&passwd={Uri.EscapeDataString(connection.Settings.Get("password"))}";

        using var document = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        var root = document.RootElement;

        if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException(Explain(root));

        var sid = root.TryGetProperty("data", out var data) && data.TryGetProperty("sid", out var value)
            ? value.GetString() ?? ""
            : "";

        if (sid.Length == 0)
            throw new InvalidOperationException("DSM accepted the login but returned no session.");

        _sessions[connection.Id] = sid;
        return sid;
    }

    private async Task<JsonElement> GetAsync(Connection connection, string path, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);

        async Task<JsonDocument> CallAsync()
        {
            var sid = await SessionIdAsync(connection, ct);
            return JsonDocument.Parse(await http.GetStringAsync($"{BaseUrl(connection)}{path}&_sid={sid}", ct));
        }

        var document = await CallAsync();
        var root = document.RootElement;

        // 105/106/107 are DSM's "your session is no longer valid" family, and they arrive
        // as a 200 with success=false rather than a 401.
        if (!Succeeded(root) && Code(root) is 105 or 106 or 107)
        {
            document.Dispose();
            _sessions.TryRemove(connection.Id, out _);
            document = await CallAsync();
            root = document.RootElement;
        }

        if (!Succeeded(root))
            throw new InvalidOperationException(Explain(root));

        // Cloned because the document is disposed when this returns, and callers keep
        // reading the element afterwards.
        return root.TryGetProperty("data", out var data) ? data.Clone() : default;
    }

    private static bool Succeeded(JsonElement root) =>
        root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;

    private static int Code(JsonElement root) =>
        root.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code)
        && code.ValueKind == JsonValueKind.Number
            ? code.GetInt32()
            : 0;

    /// <summary>DSM reports failures as numbers. These are the ones a person can act on.</summary>
    private static string Explain(JsonElement root) => Code(root) switch
    {
        400 or 401 => "DSM rejected the username or password.",
        403 or 404 => "That account has two-factor authentication on, which this API cannot pass. " +
                      "Make a separate account for monitoring without it.",
        407 => "DSM has blocked this address after too many failed logins. Clear it in Control Panel → Security.",
        105 => "That account is not allowed to use the API. Give it access, or use one that has it.",
        var other => $"DSM returned error {other}.",
    };

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static double Bytes(JsonElement size, string name) =>
        size.TryGetProperty(name, out var value)
        && double.TryParse(
            value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0;

    /// <summary>Some DSM builds report uptime as "12:34:56" rather than seconds.</summary>
    private static double? Uptime(string? text)
    {
        if (text is null || !TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var span))
            return null;
        return span.TotalDays;
    }
}
