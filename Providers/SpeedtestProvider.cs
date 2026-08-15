using System.Diagnostics;
using System.Globalization;
using System.Net;
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
            Help: "Scheme, host and port, and nothing else — /api/v1/results/latest is added for you, so a " +
                  "URL ending in /api or /api/v1 will not work. It has to be an address that answers from " +
                  "inside LabbyTwo's container: for another container on the same host that usually means " +
                  "its container name and its internal port, not the host's IP and the published one."),

        new("token", "API token", FieldKind.Password,
            Help: "Speedtest Tracker → Settings → API Tokens. Leave it blank if yours serves the API without " +
                  "one — Test connection says which of the two you are looking at rather than leaving you to " +
                  "guess."),
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

            // Two different faults answer 401, and telling somebody their token was
            // refused when they never had one sends them looking for the wrong problem.
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return ProbeResult.Down(stopwatch.Elapsed,
                    connection.Settings.Get("token").Length == 0
                        ? "Speedtest Tracker wants an API token and this connection has none. " +
                          "Make one under Settings → API Tokens and paste it into the API token box."
                        : "Speedtest Tracker refused the token. Settings → API Tokens, and paste a fresh one.");

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

/// <summary>
/// The same numbers, with nothing to install. The provider above reads a Speedtest
/// Tracker somebody is already running; this one moves the bytes itself, so "how fast is
/// the internet here" stops being a question that needs a second container, a database
/// and a scheduler behind it.
///
/// It reports the same metric names on purpose — <c>download_mbps</c>, <c>upload_mbps</c>,
/// <c>ping_ms</c>, <c>jitter_ms</c>. A chart, a tile or an alert rule written against
/// either one keeps working if you later switch to the other, which is the same bargain
/// AdGuard and Pi-hole already make with each other.
/// </summary>
public sealed class InternetSpeedTestProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "speedtest";
    public string DisplayName => "Internet speed test";
    public string Icon => "📶";
    public string Category => "Network";

    public string Description =>
        "Measures your own download, upload and latency on a schedule. Nothing to install and no API key.";

    /// <summary>
    /// Cloudflare's speed-test endpoints: no key, no account, no rate limit worth worrying
    /// about at this cadence, and a network close to almost everybody. Both are
    /// overridable, which is also the answer for anyone who would rather push the traffic
    /// at their own server than at somebody else's.
    /// </summary>
    private const string DefaultDownload = "https://speed.cloudflare.com/__down?bytes={bytes}";
    private const string DefaultUpload = "https://speed.cloudflare.com/__up";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("every_hours", "Run a test every (hours)", FieldKind.Number, Default: "6", Required: true,
            Help: "This is the setting that matters, because a speed test is not free: it really does move " +
                  "the data. At the default sizes each run costs about 35 MB, so every six hours is roughly " +
                  "140 MB a day. Turn it down to 24 on anything metered."),

        new("download_mb", "Download size (MB)", FieldKind.Number, Default: "25",
            Help: "Too small and a fast line finishes before the measurement means anything; too large and " +
                  "you are paying for precision you will not read. 25 MB takes about two seconds at 100 Mbps.")
        { Advanced = true },

        new("upload_mb", "Upload size (MB)", FieldKind.Number, Default: "10",
            Help: "Smaller than the download on purpose — home connections are usually far slower upward, " +
                  "so an equal size would take several times as long for the same confidence.")
        { Advanced = true },

        new("download_url", "Download URL", FieldKind.Url, DefaultDownload,
            Help: "Left empty this uses Cloudflare. Point it at your own server if you would rather not send " +
                  "the traffic there — {bytes} is replaced with the size in bytes.")
        { Advanced = true },

        new("upload_url", "Upload URL", FieldKind.Url, DefaultUpload,
            Help: "Anything that accepts a POST body and discards it.")
        { Advanced = true },
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("download_mbps", "Download", " Mbps", 1),
        new("upload_mbps", "Upload", " Mbps", 1),
        new("ping_ms", "Ping", " ms", 1),
        new("jitter_ms", "Jitter", " ms", 1),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Nothing like the speed you pay for", "download_mbps", Comparison.Below, 100, ForMinutes: 0,
            Why: "Set it to about half your plan. No sustain window here, unlike the tracker: these results " +
                 "arrive hours apart, so requiring one to persist for half an hour would never fire."),

        new("Upload has collapsed", "upload_mbps", Comparison.Below, 5,
            Why: "The half of the connection nobody watches, and the half that breaks video calls and " +
                 "backups first."),

        new("Latency is bad enough to notice", "ping_ms", Comparison.Above, 150,
            Why: "Past this, calls and games are visibly worse even when the speed figures look fine."),
    ];

    /// <summary>
    /// Per connection, because this is the one provider where the right cadence is a
    /// property of your line and your data plan rather than of the integration. Floored at
    /// an hour: the sweep is every thirty seconds, and a speed test on that schedule would
    /// saturate the connection it is trying to measure and bill you for the privilege.
    /// </summary>
    public TimeSpan MinimumIntervalFor(Connection connection) =>
        TimeSpan.FromHours(Math.Clamp(connection.Settings.GetInt("every_hours", 6), 1, 168));

    /// <summary>Whole test, hard stop. A stalled endpoint must not hold a probe slot for ever.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);

        try
        {
            // The transfer client, not the probe one: the ordinary client gives up after
            // thirty seconds, which is correct for a probe and would cap this at whatever
            // fits in that window — quietly reporting a slow line as slower still.
            var http = httpFactory.CreateClient(ProviderHttp.TransferClientName);

            var downloadUrl = Url(connection, "download_url", DefaultDownload);
            var uploadUrl = Url(connection, "upload_url", DefaultUpload);

            var (ping, jitter) = await LatencyAsync(http, downloadUrl, budget.Token);

            var downloadBytes = Bytes(connection, "download_mb", 25);
            var download = await DownloadAsync(http, downloadUrl, downloadBytes, budget.Token);

            var uploadBytes = Bytes(connection, "upload_mb", 10);
            var upload = await UploadAsync(http, uploadUrl, uploadBytes, budget.Token);

            stopwatch.Stop();

            var metrics = new Dictionary<string, double>
            {
                ["download_mbps"] = download,
                ["upload_mbps"] = upload,
                ["latency_ms"] = ping,
            };
            if (ping > 0)
                metrics["ping_ms"] = ping;
            if (jitter >= 0)
                metrics["jitter_ms"] = jitter;

            return ProbeResult.Up(stopwatch.Elapsed,
                $"{download:0.#} down / {upload:0.#} up Mbps, {ping:0} ms", metrics);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed,
                $"The test did not finish within {Budget.TotalMinutes:0} minutes. That is either a very slow " +
                "connection or an endpoint that stopped sending — try a smaller download size.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, Host(connection)));
        }
    }

    /// <summary>
    /// Five small round trips. The lowest is the ping — the others carry whatever queuing
    /// happened to be in the way, and the best case is the honest figure for the path.
    /// Jitter is the mean gap between consecutive samples, which is what the word means
    /// here and what a video call actually suffers from.
    /// </summary>
    private static async Task<(double Ping, double Jitter)> LatencyAsync(
        HttpClient http, string downloadUrl, CancellationToken ct)
    {
        var samples = new List<double>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Sized(downloadUrl, 0));

                // Headers only: the point is the round trip, not the body.
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                timer.Stop();
                if (response.IsSuccessStatusCode)
                    samples.Add(timer.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One lost sample is not a failed test; the download that follows is what
                // decides whether this connection works.
            }
        }

        if (samples.Count == 0)
            return (0, -1);

        var ping = samples.Min();
        if (samples.Count < 2)
            return (ping, -1);

        var gaps = samples.Zip(samples.Skip(1), (a, b) => Math.Abs(b - a)).ToList();
        return (ping, gaps.Average());
    }

    private static async Task<double> DownloadAsync(
        HttpClient http, string url, long bytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Sized(url, bytes));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[128 * 1024];
        long read = 0;

        // Started after the headers, so connection setup and the server thinking about it
        // are not counted as transfer time and reported as a slower line than you have.
        var timer = Stopwatch.StartNew();
        int got;
        while ((got = await stream.ReadAsync(buffer, ct)) > 0)
            read += got;
        timer.Stop();

        return Mbps(read, timer.Elapsed);
    }

    private static async Task<double> UploadAsync(
        HttpClient http, string url, long bytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ZeroContent(bytes) };

        var timer = Stopwatch.StartNew();
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        timer.Stop();
        response.EnsureSuccessStatusCode();

        return Mbps(bytes, timer.Elapsed);
    }

    private static double Mbps(long bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 ? 0 : bytes * 8 / elapsed.TotalSeconds / 1_000_000d;

    private static string Sized(string url, long bytes) =>
        url.Replace("{bytes}", bytes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static long Bytes(Connection connection, string key, int fallback) =>
        Math.Clamp(connection.Settings.GetInt(key, fallback), 1, 1000) * 1024L * 1024L;

    private static string Url(Connection connection, string key, string fallback) =>
        connection.Settings.Get(key) is { Length: > 0 } value ? value : fallback;

    private static string Host(Connection connection)
    {
        var url = Url(connection, "download_url", DefaultDownload);
        return Uri.TryCreate(Sized(url, 0), UriKind.Absolute, out var parsed) ? parsed.Host : url;
    }

    /// <summary>
    /// The upload body, written from one reused buffer rather than allocated whole. Ten
    /// megabytes as a byte[] per probe is a large-object-heap allocation every few hours
    /// for no reason, and the bytes themselves are meaningless — only their number counts.
    /// </summary>
    private sealed class ZeroContent(long length) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[128 * 1024];
            for (long sent = 0; sent < length;)
            {
                var chunk = (int)Math.Min(buffer.Length, length - sent);
                await stream.WriteAsync(buffer.AsMemory(0, chunk));
                sent += chunk;
            }
        }

        protected override bool TryComputeLength(out long computed)
        {
            // A known length keeps this off chunked encoding, which some endpoints refuse
            // and which would otherwise add framing to the thing being measured.
            computed = length;
            return true;
        }
    }
}
