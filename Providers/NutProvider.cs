using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// A UPS through Network UPS Tools. Not HTTP — NUT speaks a small line protocol on TCP
/// 3493 — which is exactly why the provider interface is about "one round trip" rather
/// than "one HTTP request".
/// </summary>
public sealed class NutProvider : IConnectionProvider
{
    public string Type => "nut";
    public string DisplayName => "UPS (Network UPS Tools)";
    public string Icon => "🔋";
    public string Category => "Power";
    public string Description => "Battery charge, load, input voltage and whether it is running on battery.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("host", "Host", FieldKind.Text, "192.168.1.20", Required: true),
        new("port", "Port", FieldKind.Number, Default: "3493"),
        new("ups", "UPS name", FieldKind.Text, "ups", Required: true,
            Help: "As named in ups.conf — the bit in [brackets]. Usually just \"ups\"."),
        new("username", "Username", FieldKind.Text, Help: "Only if upsd.users requires one."),
        new("password", "Password", FieldKind.Password),
        new("rated_watts", "Rated output (watts)", FieldKind.Number,
            Help: "Only for a UPS that reports its load as a percentage and nothing else — the watts on its label, " +
                  "so the Power page can turn the percentage into watts. Leave empty if it reports its own wattage."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("battery_percent", "Battery charge", "%"),
        new("battery_runtime_minutes", "Runtime left", " min"),
        new("load_percent", "Load", "%"),
        new("load_watts", "Power drawn", " W"),
        new("input_volts", "Input voltage", " V", 1),
        new("on_battery", "Running on battery"),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Running on battery", "on_battery", Comparison.Above, 0, ForMinutes: 1,
            Why: "A minute's grace, so a brief flicker does not wake you."),
        new("Battery low", "battery_percent", Comparison.Below, 30, ClearThreshold: 50,
            Why: "Time to shut things down cleanly."),
        new("Little runtime left", "battery_runtime_minutes", Comparison.Below, 5, ClearThreshold: 10,
            Why: "About to lose power, whatever the charge reads."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var host = connection.Settings.Get("host");
        var ups = connection.Settings.Get("ups", "ups");
        if (host.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No host configured.");

        var port = connection.Settings.GetInt("port", 3493);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            await client.ConnectAsync(host, port, timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

            if (connection.Settings.Get("username") is { Length: > 0 } username)
            {
                await writer.WriteLineAsync($"USERNAME {username}");
                await reader.ReadLineAsync(timeout.Token);
                await writer.WriteLineAsync($"PASSWORD {connection.Settings.Get("password")}");
                await reader.ReadLineAsync(timeout.Token);
            }

            await writer.WriteLineAsync($"LIST VAR {ups}");

            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while ((line = await reader.ReadLineAsync(timeout.Token)) is not null)
            {
                if (line.StartsWith("END LIST", StringComparison.Ordinal))
                    break;
                if (line.StartsWith("ERR ", StringComparison.Ordinal))
                    throw new InvalidOperationException(Explain(line[4..].Trim(), ups));

                // VAR ups battery.charge "100"
                if (!line.StartsWith("VAR ", StringComparison.Ordinal))
                    continue;
                var quote = line.IndexOf('"');
                if (quote < 0)
                    continue;
                var parts = line[..quote].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                    continue;
                variables[parts[2]] = line[(quote + 1)..].TrimEnd('"');
            }

            // Politeness: upsd logs a warning for a client that just drops the socket.
            await writer.WriteLineAsync("LOGOUT");
            stopwatch.Stop();

            if (variables.Count == 0)
                return ProbeResult.Down(stopwatch.Elapsed, $"upsd answered but reported no variables for \"{ups}\".");

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            Copy(variables, "battery.charge", metrics, "battery_percent");
            Copy(variables, "battery.runtime", metrics, "battery_runtime_minutes", seconds => seconds / 60);
            Copy(variables, "ups.load", metrics, "load_percent");
            Copy(variables, "input.voltage", metrics, "input_volts");
            if (LoadWatts(variables, connection.Settings.Get("rated_watts")) is { } watts)
                metrics["load_watts"] = watts;

            var status = variables.GetValueOrDefault("ups.status", "");
            var onBattery = status.Contains("OB", StringComparison.Ordinal);
            var lowBattery = status.Contains("LB", StringComparison.Ordinal);
            metrics["on_battery"] = onBattery ? 1 : 0;

            var details = new Dictionary<string, string> { ["Status"] = Describe(status) };
            if (variables.GetValueOrDefault("ups.model") is { Length: > 0 } model)
                details["Model"] = model;

            var charge = metrics.GetValueOrDefault("battery_percent");
            var summary = onBattery
                ? $"On battery — {charge:0}% charge, {metrics.GetValueOrDefault("battery_runtime_minutes"):0} min left"
                : $"On mains — {charge:0}% charge, {metrics.GetValueOrDefault("load_percent"):0}% load";

            // Mains lost or the battery critically low is the whole reason to watch a UPS.
            return onBattery || lowBattery
                ? new ProbeResult(false, summary, stopwatch.Elapsed, metrics, details)
                : ProbeResult.Up(stopwatch.Elapsed, summary, metrics, details);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, "Timed out talking to upsd.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// What the UPS is delivering, in watts, for the Power page. Most UPSes report it as
    /// <c>ups.realpower</c>; many only report <c>ups.load</c>, a percentage of their rated
    /// output, and then that rating is needed — from <c>ups.realpower.nominal</c> when the
    /// driver knows it, else from the "Rated output" setting. Null when neither is there:
    /// a load percentage alone is not watts, and guessing the rating would be making a
    /// number up.
    /// </summary>
    public static double? LoadWatts(IReadOnlyDictionary<string, string> variables, string ratedSetting)
    {
        static double? Read(IReadOnlyDictionary<string, string> v, string key) =>
            v.TryGetValue(key, out var raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

        if (Read(variables, "ups.realpower") is { } real)
            return real;

        if (Read(variables, "ups.load") is not { } load)
            return null;

        var rated = Read(variables, "ups.realpower.nominal");
        if (rated is null && double.TryParse(ratedSetting, NumberStyles.Float, CultureInfo.InvariantCulture, out var setting) && setting > 0)
            rated = setting;

        return rated is { } nominal ? load / 100 * nominal : null;
    }

    /// <summary>NUT's error codes are terse; say what to do about the common ones.</summary>
    private static string Explain(string code, string ups) => code switch
    {
        "UNKNOWN-UPS" => $"upsd has no UPS called \"{ups}\". Check the name in [brackets] in ups.conf.",
        "ACCESS-DENIED" => "upsd denied access — the host may not be listed in upsd.conf, or credentials are needed.",
        "USERNAME-REQUIRED" or "PASSWORD-REQUIRED" => "upsd wants a username and password.",
        _ => $"upsd said: {code}",
    };

    /// <summary>Expands the status flags into something readable on a tile.</summary>
    private static string Describe(string status) => string.Join(", ", status
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(flag => flag switch
        {
            "OL" => "online",
            "OB" => "on battery",
            "LB" => "low battery",
            "HB" => "high battery",
            "RB" => "replace battery",
            "CHRG" => "charging",
            "DISCHRG" => "discharging",
            "BYPASS" => "bypass",
            "CAL" => "calibrating",
            "OFF" => "off",
            "OVER" => "overloaded",
            "TRIM" => "trimming voltage",
            "BOOST" => "boosting voltage",
            _ => flag,
        }));

    private static void Copy(
        Dictionary<string, string> variables, string key,
        Dictionary<string, double> metrics, string metric,
        Func<double, double>? convert = null)
    {
        if (variables.TryGetValue(key, out var raw) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            metrics[metric] = convert is null ? value : convert(value);
    }
}
