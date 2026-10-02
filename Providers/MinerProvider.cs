using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// A small Bitcoin miner on the LAN: an NMMiner (the ESP32 "lottery" miners that run on a
/// Cheap Yellow Display) or anything running AxeOS — the Bitaxe family and its clones. Both
/// answer <c>GET /api/system/info</c> with a JSON object and nothing else is needed: no
/// account, no pool API, no key.
///
/// The two firmwares share the address but not the shape. NMMiner nests everything
/// (<c>identity</c>, <c>miner</c>, <c>stratum</c>…) and writes difficulties as padded
/// strings with SI suffixes — <c>"306.59 "</c>, <c>"132.8T"</c>. AxeOS is one flat object of
/// numbers. Which one answered is decided by the shape of the answer, never by a setting,
/// so a drawer of mixed miners needs one kind of connection.
///
/// <para><b>Why there are two hashrates.</b> The firmware's own figure is reported as
/// <c>hashrate_ghs</c>, read as GH/s (the AxeOS convention, which NMMiner's API mirrors).
/// But an NMMiner has been seen answering <c>hashRate: 0</c> while its accepted-share count
/// climbed steadily, so a card that trusted the reported figure alone would say a working
/// miner was doing nothing. <c>est_hashrate_ghs</c> is worked out independently from what
/// the pool accepted, which cannot be faked by a display bug — see <see cref="ShareTrail"/>.</para>
///
/// <para>The payout address in the stratum user is masked everywhere it is shown unless the
/// connection asks otherwise. It is not a secret in the cryptographic sense, but it is a
/// public ledger of everything that address has ever been paid, and a dashboard is a thing
/// people screenshot.</para>
/// </summary>
public sealed class MinerProvider(IHttpClientFactory httpFactory, TimeProvider? clock = null) : IConnectionProvider
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The one endpoint both firmwares answer.</summary>
    public const string InfoPath = "/api/system/info";

    public string Type => "miner";
    public string DisplayName => "Bitcoin miner (NMMiner / Bitaxe)";
    public string Icon => "⛏️";
    public string Category => "Devices";
    public string Description =>
        "Hashrate, shares, best difficulty and WiFi from an NMMiner or a Bitaxe (AxeOS) on your LAN — " +
        "with a hashrate worked out from accepted shares, for the times the miner's own figure says zero.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("address", "Address", FieldKind.Text, "192.168.86.34", Required: true,
            Help: "The miner's IP address or URL. NMMiner and AxeOS both answer on port 80, so the IP alone is enough."),
        new("show_worker", "Show the full payout address", FieldKind.Bool, Default: "false",
            Help: "Off shows it shortened — bc1q8k…dssa.worker — so a screenshot of the dashboard does not " +
                  "publish the address every reward is paid to.") { Advanced = true },
        new("timeout", "Timeout (seconds)", FieldKind.Number, Default: "5",
            Help: "ESP32 miners are slow to answer while busy hashing; a few seconds is normal.") { Advanced = true },
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("hashrate_ghs", "Hashrate (reported)", MinerUnits.GigahashesPerSecond, 2),
        new("est_hashrate_ghs", "Hashrate (from shares)", MinerUnits.GigahashesPerSecond, 2),
        new("shares_accepted", "Shares accepted"),
        new("shares_rejected", "Shares rejected"),
        new("reject_percent", "Rejected shares", "%", 1),
        new("shares_per_hour", "Shares per hour", " /h", 1),
        new("best_diff_session", "Best difficulty since boot", MinerUnits.Difficulty, 2),
        new("best_diff_ever", "Best difficulty ever", MinerUnits.Difficulty, 2),
        new("network_diff", "Network difficulty", MinerUnits.Difficulty, 2),
        new("pool_diff", "Share difficulty", MinerUnits.Difficulty, 4),
        new("block_hits", "Blocks found"),
        new("new_blocks", "Blocks found since last check"),
        new("uptime_hours", "Uptime", " h", 1),
        new("wifi_rssi", "WiFi signal", " dBm"),
        new("free_heap_kb", "Free memory", " KB"),
        new("temp_c", "Temperature", "°C", 1),
        new("vr_temp_c", "Voltage regulator", "°C", 1),
        new("power_w", "Power", " W", 1),
        new("fan_rpm", "Fan", " rpm"),
        new("latency_ms", "Response time", " ms"),
    ];

    /// <summary>
    /// "Miner offline" is not here because it does not need to be: a miner that stops
    /// answering is a connection that is down, and up/down alerting already says so for
    /// every connection with alerts on. These are the failures that leave it answering.
    /// </summary>
    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        // Shares per hour is measured over up to fifteen minutes (see ShareTrail), so it is
        // exactly zero only when a whole window went by without one; any single share in the
        // window reads at least 4/h. Half a share an hour therefore means "none", with no
        // healthy miner anywhere near it — an NMMiner at 0.002 does hundreds an hour.
        new("No shares accepted for 30 min", "shares_per_hour", Comparison.Below, 0.5, ForMinutes: 30,
            Why: "Up and answering but not mining — the pool connection dropped, or the miner is stuck. " +
                 "If your pool sets a high share difficulty and shares are normally far apart, lengthen the wait."),

        new("Rejected shares high", "reject_percent", Comparison.Above, 5, ClearThreshold: 3, ForMinutes: 30,
            Why: "A few rejects are normal (stale shares after a new block). More than one in twenty means " +
                 "a bad connection to the pool, or a Bitaxe overclocked past stable."),

        new("Weak WiFi", "wifi_rssi", Comparison.Below, -80, ClearThreshold: -75, ForMinutes: 15,
            Why: "Below -80 dBm an ESP32 drops off the network now and then, and every drop loses work in progress."),

        new("Running hot", "temp_c", Comparison.Above, 70, ClearThreshold: 62, ForMinutes: 5,
            Why: "For a Bitaxe: the ASIC is throttling or about to. Check the fan and the heatsink."),

        // new_blocks is the rise in the block counter since the last probe — one for the
        // probe that saw it, zero after — so this sends once per block rather than staying
        // lit (and announcing it again after every restart, as a rule on the counter would).
        new("Block found!", "new_blocks", Comparison.Above, 0,
            Why: "The lottery paid out. Unlikely, which is exactly why you would want to hear about it straight away."),
    ];

    // ---- memory between probes -------------------------------------------------------

    /// <summary>Share history per connection, for the rates. In memory only; a restart starts it again.</summary>
    private readonly ConcurrentDictionary<string, ShareTrail> _trails = new();

    /// <summary>
    /// Which firmware each connection last turned out to be, so the restart button is only
    /// offered where it is known to work. Learned from probes; nothing is offered before one.
    /// </summary>
    private readonly ConcurrentDictionary<string, MinerSchema> _schemas = new();

    /// <summary>
    /// Restart, for AxeOS only. NMMiner's web UI has a restart button, but its API has never
    /// been seen to offer one, and a button that might do nothing — or something else — is
    /// worse than no button.
    /// </summary>
    public static readonly ProviderAction Restart = new("restart", "Restart miner", "🔄")
    {
        Description = "Reboots the miner. It stops hashing and drops off the network for about a minute.",
        ConfirmMessage = "Restart this miner? It will stop hashing and be offline for a minute or so.",
        Disrupts = TimeSpan.FromMinutes(2),
    };

    public IReadOnlyList<ProviderAction> ActionsFor(Connection connection) =>
        _schemas.TryGetValue(connection.Id, out var schema) && schema == MinerSchema.AxeOS ? [Restart] : [];

    public async Task<ActionResult> RunActionAsync(
        Connection connection, ProviderAction action, SettingsBag input, CancellationToken ct)
    {
        if (action.Id != Restart.Id)
            return ActionResult.Failed($"No miner action called “{action.Id}”.");

        if (!_schemas.TryGetValue(connection.Id, out var schema) || schema != MinerSchema.AxeOS)
            return ActionResult.Failed("Restarting is only offered for AxeOS miners (Bitaxe and compatible).");

        var baseUrl = BaseUrl(connection.Settings.Get("address"));
        if (baseUrl.Length == 0)
            return ActionResult.Failed("No address configured.");

        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/system/restart");
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode
                ? ActionResult.Done("Restarting — it should be hashing again in a minute or so.")
                : ActionResult.Failed($"The miner answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (Exception ex)
        {
            return ActionResult.Failed(ProbeError.Describe(ex, baseUrl));
        }
    }

    // ---- the probe ---------------------------------------------------------------------

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var baseUrl = BaseUrl(connection.Settings.Get("address"));
        if (baseUrl.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No address configured.");

        var timeout = TimeSpan.FromSeconds(Math.Clamp(connection.Settings.GetInt("timeout", 5), 1, 60));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            using var response = await http.GetAsync(baseUrl + InfoPath, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                stopwatch.Stop();
                return ProbeResult.Down(stopwatch.Elapsed,
                    $"HTTP {(int)response.StatusCode} from {InfoPath} — that is not an NMMiner or AxeOS miner's API.");
            }

            var payload = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            stopwatch.Stop();

            using var document = JsonDocument.Parse(payload);
            if (MinerReading.Parse(document.RootElement) is not { } reading)
                return ProbeResult.Down(stopwatch.Elapsed,
                    $"{InfoPath} answered, but not in a shape this knows — neither NMMiner's nor AxeOS's.");

            return Read(connection, reading, stopwatch.Elapsed, _clock.GetUtcNow());
        }
        catch (JsonException)
        {
            // The message alone: JsonException quotes the offending text, and the text is the
            // miner's answer — which, on these devices, carries the payout address.
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, $"{InfoPath} did not answer with valid JSON.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed,
                $"Timed out after {timeout.TotalSeconds:0}s — nothing answered at {baseUrl}.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, baseUrl));
        }
    }

    /// <summary>
    /// The probe's result from one parsed answer, folding it into this connection's share
    /// history. Separate from the HTTP so the arithmetic is testable at whatever times a
    /// test likes.
    /// </summary>
    public ProbeResult Read(Connection connection, MinerReading reading, TimeSpan elapsed, DateTimeOffset now)
    {
        _schemas[connection.Id] = reading.Schema;

        var metrics = new Dictionary<string, double> { ["latency_ms"] = elapsed.TotalMilliseconds };
        void Put(string key, double? value)
        {
            if (value is { } number && !double.IsNaN(number) && !double.IsInfinity(number))
                metrics[key] = number;
        }

        Put("hashrate_ghs", reading.HashrateGhs);
        Put("shares_accepted", reading.Accepted);
        Put("shares_rejected", reading.Rejected);
        if (reading.Accepted is { } accepted && reading.Rejected is { } rejected && accepted + rejected > 0)
            Put("reject_percent", rejected / (accepted + rejected) * 100);
        Put("best_diff_session", reading.BestDiffSession);
        Put("best_diff_ever", reading.BestDiffEver);
        Put("network_diff", reading.NetworkDiff);
        Put("pool_diff", reading.PoolDiff);
        Put("block_hits", reading.BlockHits);
        Put("uptime_hours", reading.UptimeSeconds / 3600);
        Put("wifi_rssi", reading.Rssi);
        Put("free_heap_kb", reading.FreeHeapBytes / 1024);
        Put("temp_c", reading.TempC);
        Put("vr_temp_c", reading.VrTempC);
        Put("power_w", reading.PowerW);
        Put("fan_rpm", reading.FanRpm);

        if (reading.Accepted is { } count)
        {
            var trail = _trails.GetOrAdd(connection.Id, _ => new ShareTrail());
            var step = trail.Add(new ShareSample(now, count, reading.UptimeSeconds, reading.BlockHits), reading.PoolDiff);
            if (step.Rates is { } rates)
            {
                Put("shares_per_hour", rates.SharesPerHour);
                Put("est_hashrate_ghs", rates.EstimatedGhs);
            }
            Put("new_blocks", step.NewBlocks);
        }

        var details = new Dictionary<string, string>();
        void Detail(string label, string value)
        {
            if (value.Length > 0)
                details[label] = value;
        }

        Detail("Model", reading.Model);
        Detail("Firmware", reading.Version);
        Detail("Hostname", reading.Hostname);
        Detail("Pool", reading.Pool);
        Detail("Worker", connection.Settings.GetBool("show_worker") ? reading.Worker : MaskWorker(reading.Worker));
        Detail("Speaks", reading.Schema == MinerSchema.AxeOS ? "AxeOS API" : "NMMiner API");

        return ProbeResult.Up(elapsed, Summary(reading.Model, metrics), metrics, details);
    }

    /// <summary>
    /// One line for the tile: the best hashrate there is, and the share rate once there is one.
    /// The estimate wins over a reported zero, because a miner whose shares are being accepted
    /// is not hashing at nothing whatever its screen says.
    /// </summary>
    private static string Summary(string model, IReadOnlyDictionary<string, double> metrics)
    {
        var parts = new List<string>();
        if (model.Length > 0)
            parts.Add(model);

        if (EffectiveHashrate(metrics) is { } effective)
            parts.Add((metrics.ContainsKey("est_hashrate_ghs") && metrics.GetValueOrDefault("hashrate_ghs") <= 0 ? "≈" : "")
                      + MinerUnits.Hashrate(effective));

        if (metrics.TryGetValue("shares_per_hour", out var perHour))
            parts.Add($"{perHour:0.#} shares/h");
        else if (metrics.TryGetValue("shares_accepted", out var shares))
            parts.Add($"{shares:0} shares accepted");

        return parts.Count == 0 ? "Connected" : string.Join(" · ", parts);
    }

    /// <summary>
    /// The hashrate a card should lead with: the miner's own when it reports one above zero,
    /// otherwise the estimate from shares, otherwise nothing. Shared with the cards so the
    /// tile, the Miner card and the Miners summary agree.
    /// </summary>
    public static double? EffectiveHashrate(IReadOnlyDictionary<string, double> metrics)
    {
        if (metrics.TryGetValue("hashrate_ghs", out var reported) && reported > 0)
            return reported;
        if (metrics.TryGetValue("est_hashrate_ghs", out var estimated))
            return estimated;
        return metrics.TryGetValue("hashrate_ghs", out var zero) ? zero : null;
    }

    /// <summary>
    /// "bc1q8kuzgp…upmdssa.nmminer1" becomes "bc1q8k…dssa.nmminer1": the start and end of the
    /// address, enough to tell two apart, and the worker name after the dot intact. Anything
    /// twelve characters or shorter is a pool login rather than an address and is left alone.
    /// </summary>
    public static string MaskWorker(string user)
    {
        if (string.IsNullOrWhiteSpace(user))
            return "";

        var trimmed = user.Trim();
        var dot = trimmed.IndexOf('.');
        var address = dot < 0 ? trimmed : trimmed[..dot];
        var worker = dot < 0 ? "" : trimmed[dot..];

        return address.Length <= 12 ? trimmed : $"{address[..6]}…{address[^4..]}{worker}";
    }

    /// <summary>
    /// Whatever was typed, as a base URL: a bare IP gets http://, a trailing slash or a pasted
    /// <c>/api/system/info</c> is dropped. Empty when nothing was typed.
    /// </summary>
    public static string BaseUrl(string? written)
    {
        var text = (written ?? "").Trim();
        if (text.Length == 0)
            return "";
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;
        text = text.TrimEnd('/');
        if (text.EndsWith(InfoPath, StringComparison.OrdinalIgnoreCase))
            text = text[..^InfoPath.Length];
        return text.TrimEnd('/');
    }
}

/// <summary>Which of the two API shapes a miner answered in.</summary>
public enum MinerSchema
{
    /// <summary>NMMiner: nested objects, difficulties as padded strings with SI suffixes.</summary>
    NMMiner,

    /// <summary>AxeOS (ESP-Miner): the Bitaxe family and compatibles; one flat object of numbers.</summary>
    AxeOS,
}

/// <summary>
/// One answer from <c>/api/system/info</c>, in either shape, as the numbers and words the
/// provider wants. Every number is optional: firmware versions differ in what they report,
/// and a field that is missing is better left out than recorded as zero.
/// </summary>
public sealed record MinerReading
{
    public MinerSchema Schema { get; init; }
    public string Model { get; init; } = "";
    public string Version { get; init; } = "";
    public string Hostname { get; init; } = "";

    /// <summary>host:port, with no stratum+tcp:// in front.</summary>
    public string Pool { get; init; } = "";

    /// <summary>The full stratum user — the payout address and worker. Mask before showing.</summary>
    public string Worker { get; init; } = "";

    public double? HashrateGhs { get; init; }
    public double? Accepted { get; init; }
    public double? Rejected { get; init; }
    public double? UptimeSeconds { get; init; }
    public double? BestDiffSession { get; init; }
    public double? BestDiffEver { get; init; }
    public double? NetworkDiff { get; init; }
    public double? PoolDiff { get; init; }
    public double? BlockHits { get; init; }
    public double? Rssi { get; init; }
    public double? FreeHeapBytes { get; init; }
    public double? TempC { get; init; }
    public double? VrTempC { get; init; }
    public double? PowerW { get; init; }
    public double? FanRpm { get; init; }

    /// <summary>
    /// Reads either shape, or null for JSON that is neither. NMMiner is recognised by its
    /// <c>miner</c> object; AxeOS by top-level fields only it has. Checked in that order
    /// because NMMiner's answer has no top-level numbers to be confused by.
    /// </summary>
    public static MinerReading? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (root.TryGetProperty("miner", out var miner) && miner.ValueKind == JsonValueKind.Object)
            return ParseNmMiner(root, miner);

        if (Has(root, "hashRate") || Has(root, "ASICModel") || Has(root, "sharesAccepted"))
            return ParseAxeOs(root);

        return null;
    }

    private static MinerReading ParseNmMiner(JsonElement root, JsonElement miner)
    {
        var identity = Child(root, "identity");
        var stratum = Child(root, "stratum");
        var temps = Child(root, "temps");

        return new MinerReading
        {
            Schema = MinerSchema.NMMiner,
            Model = Text(identity, "hwModel") is { Length: > 0 } model ? model : "NMMiner",
            Version = Text(identity, "fwVersion"),
            Hostname = Text(identity, "hostName"),
            Pool = PoolAddress(Text(stratum, "url"), null),
            Worker = Text(stratum, "user"),
            HashrateGhs = Number(miner, "hashRate"),
            Accepted = Number(miner, "sAccepted"),
            Rejected = Number(miner, "sRejected"),
            UptimeSeconds = Number(miner, "uptimeSeconds"),
            BestDiffSession = Number(miner, "bestDiffSession"),
            BestDiffEver = Number(miner, "bestDiffEver"),
            NetworkDiff = Number(miner, "networkDiff"),
            PoolDiff = Number(miner, "poolDiff"),
            BlockHits = Number(miner, "blkhits"),
            FreeHeapBytes = Number(miner, "freeHeap"),
            Rssi = Number(identity, "rssi"),
            TempC = Number(temps, "asic"),
        };
    }

    private static MinerReading ParseAxeOs(JsonElement root)
    {
        var asic = Text(root, "ASICModel");
        var device = Text(root, "deviceModel");
        var model = device.Length > 0
            ? asic.Length > 0 ? $"{device} · {asic}" : device
            : asic.Length > 0 ? $"Bitaxe · {asic}" : "AxeOS miner";

        return new MinerReading
        {
            Schema = MinerSchema.AxeOS,
            Model = model,
            Version = Text(root, "version"),
            Hostname = Text(root, "hostname"),
            Pool = PoolAddress(Text(root, "stratumURL"), Number(root, "stratumPort")),
            Worker = Text(root, "stratumUser"),
            HashrateGhs = Number(root, "hashRate"),
            Accepted = Number(root, "sharesAccepted"),
            Rejected = Number(root, "sharesRejected"),
            UptimeSeconds = Number(root, "uptimeSeconds"),
            BestDiffSession = Number(root, "bestSessionDiff"),
            BestDiffEver = Number(root, "bestDiff"),
            NetworkDiff = Number(root, "networkDifficulty"),
            PoolDiff = Number(root, "poolDifficulty") ?? Number(root, "stratumDiff"),
            BlockHits = Number(root, "blockFound"),
            FreeHeapBytes = Number(root, "freeHeap"),
            Rssi = Number(root, "wifiRSSI"),
            TempC = Number(root, "temp"),
            VrTempC = Number(root, "vrTemp"),
            PowerW = Number(root, "power"),
            FanRpm = Number(root, "fanrpm"),
        };
    }

    /// <summary>"stratum+tcp://pool:3333" or "pool" and a port, as "pool:3333".</summary>
    private static string PoolAddress(string url, double? port)
    {
        var host = url.Trim();
        var scheme = host.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            host = host[(scheme + 3)..];
        host = host.TrimEnd('/');
        if (host.Length == 0)
            return "";
        return port is { } p and > 0 && !host.Contains(':') ? $"{host}:{p:0}" : host;
    }

    private static bool Has(JsonElement element, string name) => element.TryGetProperty(name, out _);

    private static JsonElement Child(JsonElement element, string name) =>
        element.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object ? child : default;

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()?.Trim() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                _ => "",
            }
            : "";

    /// <summary>
    /// A number however it was written: a JSON number, a string with or without an SI
    /// suffix, or a boolean (AxeOS's <c>blockFound</c>). Null and missing are both null.
    /// </summary>
    private static double? Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => MinerUnits.ParseDifficulty(value.GetString()),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null,
        };
    }
}

/// <summary>One reading of the counters a rate is worked out from.</summary>
public readonly record struct ShareSample(DateTimeOffset At, double Accepted, double? UptimeSeconds, double? BlockHits);

/// <summary>What the share history says at one probe.</summary>
public sealed record ShareRates(double SharesPerHour, double? EstimatedGhs);

/// <summary>
/// Accepted shares over time, for one miner, and the rates that come out of them.
///
/// <para><b>Shares per hour</b> is the rise in the accepted counter over the window, scaled to
/// an hour. The window is the last fifteen minutes or so of probes rather than the gap since
/// the last one, because shares arrive at random: two probes thirty seconds apart catch zero
/// or three by luck, and a chart of that is noise. The first probe has nothing to compare
/// with and reports no rate at all, rather than a made-up zero.</para>
///
/// <para><b>Estimated hashrate.</b> A share at difficulty <i>d</i> is a hash below a target
/// that, on average, takes <i>d</i> × 2³² hashes to find — that is the definition of
/// difficulty 1 in Bitcoin. So shares accepted per second × pool difficulty × 2³² is the
/// hashes per second it took to find them, divided by 10⁹ for GH/s. It is a statistical
/// estimate: over fifteen minutes at a few hundred shares an hour it is within a few per
/// cent; with shares minutes apart it swings, and with none in the window it says zero.
/// The current pool difficulty is used for the whole window — vardiff changes it rarely
/// enough that the error is small. Rejected shares are left out: the pool did not count
/// them, so neither does this.</para>
///
/// <para><b>Restarts.</b> A counter that went backwards, or an uptime that did, means the
/// miner rebooted and started counting from zero. The history is dropped there and starts
/// again, so a reboot never reads as a negative rate or a huge one.</para>
/// </summary>
public sealed class ShareTrail
{
    /// <summary>How far back the rate looks once it has that much history.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>2³², the expected hashes per share at difficulty 1.</summary>
    public const double HashesPerDifficulty = 4294967296d;

    private readonly List<ShareSample> _samples = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Adds a reading and says what it means: the rates over the window (null with no
    /// history to compare against) and how many blocks were found since the last reading
    /// (null on the first).
    /// </summary>
    public (ShareRates? Rates, double? NewBlocks) Add(ShareSample sample, double? poolDiff)
    {
        lock (_gate)
        {
            double? newBlocks = null;
            if (_samples.Count > 0)
            {
                var last = _samples[^1];
                if (sample.BlockHits is { } hits && last.BlockHits is { } before)
                    newBlocks = Math.Max(0, hits - before);

                // A reading from before the last one — a Test press racing the sweep — says
                // nothing new; a counter that went back says the miner restarted.
                if (sample.At <= last.At)
                    return (null, newBlocks);
                if (IsRestart(last, sample))
                    _samples.Clear();
            }

            _samples.Add(sample);

            // Keep the newest reading that is at least a window old as the baseline, and
            // nothing older than it.
            while (_samples.Count > 1 && _samples[1].At <= sample.At - Window)
                _samples.RemoveAt(0);

            return (_samples.Count > 1 ? Rate(_samples[0], sample, poolDiff) : null, newBlocks);
        }
    }

    /// <summary>The counter or the uptime went backwards: the miner restarted in between.</summary>
    public static bool IsRestart(ShareSample previous, ShareSample current) =>
        current.Accepted < previous.Accepted
        || (current.UptimeSeconds is { } now && previous.UptimeSeconds is { } then && now < then);

    /// <summary>
    /// The rates between two readings, or null when they are too close together to be one.
    /// See the class summary for the arithmetic.
    /// </summary>
    public static ShareRates? Rate(ShareSample baseline, ShareSample current, double? poolDiff)
    {
        var seconds = (current.At - baseline.At).TotalSeconds;
        if (seconds < 1 || current.Accepted < baseline.Accepted)
            return null;

        var perSecond = (current.Accepted - baseline.Accepted) / seconds;
        double? estimate = poolDiff is { } diff and > 0 ? perSecond * diff * HashesPerDifficulty / 1e9 : null;
        return new ShareRates(perSecond * 3600, estimate);
    }
}

/// <summary>A miner the network scan found, with what the Add button pre-fills.</summary>
public sealed record FoundMiner(string Address, MinerSchema Schema, string Model, string Hostname);

/// <summary>
/// "Find miners on my network": asks every address in a small range for
/// <c>/api/system/info</c> and keeps the ones that answer like a miner. Only on demand —
/// nothing here runs in the background — and deliberately gentle: sixteen requests at a time,
/// one second each, so a /24 takes about sixteen seconds at worst and no device on the LAN
/// sees more than one request.
///
/// NMMiner does not announce itself — no mDNS, and nothing seen on its UDP port 12345 — so
/// asking is the only way to find one.
/// </summary>
public static class MinerScan
{
    public const int DefaultConcurrency = 16;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The addresses to ask, from what somebody typed: "192.168.86.0/24", their own address
    /// with "/24", or just "192.168.86" or "192.168.86.12" (both meaning that /24). Nothing
    /// wider than a /24 — a /16 is sixty-five thousand requests, which is not a button press.
    /// The network and broadcast addresses are left out.
    /// </summary>
    /// <exception cref="FormatException">Not an IPv4 range this will scan, with the reason.</exception>
    public static IReadOnlyList<string> Hosts(string written)
    {
        var text = (written ?? "").Trim();
        var prefix = 24;
        var slash = text.IndexOf('/');
        if (slash >= 0)
        {
            if (!int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix)
                || prefix is < 0 or > 32)
                throw new FormatException("The part after the slash should be a prefix length, like /24.");
            text = text[..slash];
        }

        if (prefix < 24)
            throw new FormatException("Only a /24 or smaller — that is up to 254 addresses, about sixteen seconds.");

        if (text.Count(c => c == '.') == 2)
            text += ".0";

        if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new FormatException("That is not an IPv4 network. Try something like 192.168.1.0/24.");

        var bytes = address.GetAddressBytes();
        var value = (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var network = value & mask;
        var size = 1u << (32 - prefix);

        // A /31 and /32 have no network or broadcast address to skip.
        var (first, last) = prefix >= 31 ? (network, network + size - 1) : (network + 1, network + size - 2);

        var hosts = new List<string>((int)(last - first + 1));
        for (var host = first; host <= last; host++)
            hosts.Add($"{host >> 24}.{(host >> 16) & 255}.{(host >> 8) & 255}.{host & 255}");
        return hosts;
    }

    /// <summary>
    /// Asks each address (an IP, or host:port) and returns the miners, in the order given.
    /// Anything that does not answer, answers with something else, or is slower than
    /// <paramref name="timeout"/> is simply not a miner as far as this is concerned.
    /// </summary>
    /// <param name="progress">Told how many addresses have been tried so far.</param>
    public static async Task<IReadOnlyList<FoundMiner>> ScanAsync(
        HttpClient http, IReadOnlyList<string> addresses, int concurrency = DefaultConcurrency,
        TimeSpan? timeout = null, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var limit = TimeSpan.FromTicks(Math.Max(1, (timeout ?? DefaultTimeout).Ticks));
        using var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 64));
        var found = new FoundMiner?[addresses.Count];
        var tried = 0;

        var tasks = addresses.Select(async (address, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                found[index] = await AskAsync(http, address, limit, ct);
            }
            finally
            {
                gate.Release();
                progress?.Report(Interlocked.Increment(ref tried));
            }
        });

        await Task.WhenAll(tasks);
        return [.. found.OfType<FoundMiner>()];
    }

    private static async Task<FoundMiner?> AskAsync(HttpClient http, string address, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await http.GetAsync($"http://{address}{MinerProvider.InfoPath}", cts.Token);
            if (!response.IsSuccessStatusCode)
                return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            return MinerReading.Parse(document.RootElement) is { } reading
                ? new FoundMiner(address, reading.Schema, reading.Model, reading.Hostname)
                : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
