using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// QNAP QTS. Handles the authLogin.cgi session per connection, so more than one NAS can
/// be added. QTS XML shifts between firmware versions, so every field is read by
/// descendant name and tolerated missing.
/// </summary>
public sealed class QnapProvider(IHttpClientFactory httpFactory, ILogger<QnapProvider> log) : IConnectionProvider
{
    public string Type => "qnap";
    public string DisplayName => "QNAP NAS";
    public string Icon => "💾";
    public string Category => "Storage";
    public string Description => "QTS system stats, temperatures, and volume usage. Create a dedicated account — accounts with 2FA cannot use this API.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("host", "Host", FieldKind.Text, "192.168.1.50", Required: true),
        new("port", "Port", FieldKind.Number, Default: "8080", Help: "QTS web port — 8080 for http, 443 for https by default."),
        new("https", "Use HTTPS", FieldKind.Bool, Default: "false"),
        new("username", "Username", FieldKind.Text, Required: true),
        new("password", "Password", FieldKind.Password, Required: true),
    ];

    private readonly ConcurrentDictionary<string, string> _sessions = new();

    public sealed record SystemInfo(
        string? Model, string? Firmware, string? HostName, TimeSpan? Uptime,
        double? CpuPercent, double? TotalMemoryMb, double? FreeMemoryMb,
        double? CpuTempC, double? SystemTempC);

    public sealed record VolumeInfo(string Label, long TotalBytes, long FreeBytes)
    {
        public long UsedBytes => TotalBytes - FreeBytes;
        public double UsedPercent => TotalBytes > 0 ? UsedBytes * 100d / TotalBytes : 0;
    }

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("cpu_percent", "CPU", "%", 1),
        new("ram_percent", "Memory", "%", 1),
        new("temp_c", "CPU temperature", "°C", 1),
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

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var info = await SystemInfoAsync(connection, ct);
            stopwatch.Stop();

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            if (info.CpuPercent is { } cpu)
                metrics["cpu_percent"] = cpu;
            if (info is { TotalMemoryMb: > 0 } and { FreeMemoryMb: not null })
                metrics["ram_percent"] = (info.TotalMemoryMb.Value - info.FreeMemoryMb.Value) / info.TotalMemoryMb.Value * 100;
            if (info.CpuTempC is { } cpuTemp)
                metrics["temp_c"] = cpuTemp;
            if (info.Uptime is { } uptime)
                metrics["uptime_days"] = uptime.TotalDays;

            // Volume fullness is the number people actually want alerts on, so fold the
            // busiest volume into the same probe rather than making it a second poll.
            try
            {
                var volumes = await VolumesAsync(connection, ct);
                if (volumes.Count > 0)
                    metrics["disk_percent"] = volumes.Max(v => v.UsedPercent);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Volume usage unavailable for {Connection}", connection.Name);
            }

            var details = new Dictionary<string, string>();
            if (info.Model is { Length: > 0 } model)
                details["Model"] = model;
            if (info.Firmware is { Length: > 0 } firmware)
                details["Firmware"] = firmware;

            return ProbeResult.Up(stopwatch.Elapsed, info.Model is { Length: > 0 } m ? m : "Connected", metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            InvalidateSession(connection);
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("host")));
        }
    }

    public async Task<SystemInfo> SystemInfoAsync(Connection connection, CancellationToken ct)
    {
        var doc = await GetXmlAsync(connection, sid => $"cgi-bin/management/manaRequest.cgi?subfunc=sysinfo&sid={sid}", ct);

        TimeSpan? uptime = null;
        var days = Num(doc, "uptime_day");
        var hours = Num(doc, "uptime_hour");
        var minutes = Num(doc, "uptime_min");
        if (days is not null || hours is not null || minutes is not null)
            uptime = new TimeSpan((int)(days ?? 0), (int)(hours ?? 0), (int)(minutes ?? 0), 0);

        return new SystemInfo(
            Str(doc, "displayModelName") ?? Str(doc, "modelName"),
            Str(doc, "version"),
            Str(doc, "hostname") ?? Str(doc, "server_name"),
            uptime,
            Num(doc, "cpu_usage"),
            Num(doc, "total_memory"),
            Num(doc, "free_memory"),
            Num(doc, "cpu_tempc"),
            Num(doc, "sys_tempc"));
    }

    public async Task<IReadOnlyList<VolumeInfo>> VolumesAsync(Connection connection, CancellationToken ct)
    {
        var doc = await GetXmlAsync(connection,
            sid => $"cgi-bin/management/chartReq.cgi?chart_func=disk_usage&disk_select=all&include=all&sid={sid}", ct);

        var volumes = new List<VolumeInfo>();
        foreach (var volume in doc.Descendants("volume"))
        {
            var label = Str(volume, "volumeLabel") ?? Str(volume, "volumeValue") ?? $"Volume {volumes.Count + 1}";
            var total = (long)(Num(volume, "total_size") ?? 0);
            var free = (long)(Num(volume, "free_size") ?? 0);
            if (total > 0)
                volumes.Add(new VolumeInfo(label.Trim(), total, free));
        }
        return volumes;
    }

    /// <summary>
    /// Where this NAS lives, ending in a slash. Public because a plugin building a File
    /// Station URL should not have to reassemble the host, port and scheme itself and get
    /// the https default wrong.
    /// </summary>
    public string BaseUrl(Connection connection)
    {
        var https = connection.Settings.GetBool("https");
        var port = connection.Settings.GetInt("port", https ? 443 : 8080);
        return $"{(https ? "https" : "http")}://{connection.Settings.Get("host")}:{port}/";
    }

    /// <summary>
    /// The QTS session id, logging in if there isn't one. Shared deliberately: File
    /// Station and the management CGI take the same sid, so a plugin that browses files
    /// borrows this session rather than opening a second one against the same account —
    /// QTS counts those, and the second login is what expires the first.
    /// </summary>
    public async Task<string> SessionIdAsync(Connection connection, CancellationToken ct)
    {
        if (_sessions.TryGetValue(connection.Id, out var cached))
            return cached;

        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        var password = Convert.ToBase64String(Encoding.UTF8.GetBytes(connection.Settings.Get("password")));
        var url = $"{BaseUrl(connection)}cgi-bin/authLogin.cgi" +
                  $"?user={Uri.EscapeDataString(connection.Settings.Get("username"))}&pwd={Uri.EscapeDataString(password)}";
        var doc = XDocument.Parse(await http.GetStringAsync(url, ct));

        var passed = doc.Descendants("authPassed").FirstOrDefault()?.Value.Trim();
        var sid = doc.Descendants("authSid").FirstOrDefault()?.Value.Trim();
        if (passed != "1" || string.IsNullOrEmpty(sid))
            throw new InvalidOperationException("Login rejected — check the username and password. Accounts with 2FA enabled cannot use this API.");

        _sessions[connection.Id] = sid;
        return sid;
    }

    /// <summary>
    /// Forgets the cached session so the next call logs in again. Call it when QTS
    /// answers a request of your own with an auth failure — an sid can expire between
    /// two calls that both looked fine a second ago.
    /// </summary>
    public void InvalidateSession(Connection connection) => _sessions.TryRemove(connection.Id, out _);

    private async Task<XDocument> GetXmlAsync(Connection connection, Func<string, string> buildUrl, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        var doc = XDocument.Parse(await http.GetStringAsync(BaseUrl(connection) + buildUrl(await SessionIdAsync(connection, ct)), ct));

        // An expired session answers 200 with authPassed=0; log back in once and retry.
        if (doc.Descendants("authPassed").FirstOrDefault()?.Value.Trim() == "0")
        {
            InvalidateSession(connection);
            doc = XDocument.Parse(await http.GetStringAsync(BaseUrl(connection) + buildUrl(await SessionIdAsync(connection, ct)), ct));
        }
        return doc;
    }

    private static string? Str(XContainer doc, string name)
    {
        var value = doc.Descendants(name).FirstOrDefault()?.Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static double? Num(XContainer doc, string name)
    {
        var raw = doc.Descendants(name).FirstOrDefault()?.Value.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        // QTS decorates numbers with units ("42 %", "55 C") depending on the endpoint.
        var digits = new string([.. raw.TakeWhile(c => char.IsDigit(c) || c is '.' or '-')]);
        return double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}
