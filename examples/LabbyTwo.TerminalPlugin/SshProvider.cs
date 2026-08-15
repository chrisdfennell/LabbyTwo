using System.Diagnostics;
using System.Globalization;
using LabbyTwo.Core;

namespace LabbyTwo.TerminalPlugin;

/// <summary>
/// An SSH host — the NAS, a Proxmox node, a Pi. It is a connection like any other, which
/// is the point: the credentials are encrypted with everything else's, the terminal opens
/// through it rather than holding a second copy of the password, and the box shows up on
/// the status page next to the services running on it.
///
/// It also reports what the machine is doing, because a connection that only existed to
/// hold a password would be a worse citizen than the rest of them: load, memory and
/// uptime come off <c>/proc</c> in the same round trip that proves the login works.
/// </summary>
public sealed class SshProvider : IConnectionProvider
{
    public string Type => "ssh";
    public string DisplayName => "SSH host";
    public string Icon => "⌨️";
    public string Category => "Infrastructure";

    public string Description =>
        "A machine you can log into. Reports load, memory and uptime, and is what the Terminal page opens a shell on.";

    /// <summary>
    /// Five minutes, not thirty seconds. A probe here is a login, and a login is a line in
    /// <c>auth.log</c> — 2,880 of them a day per host turns the one file you read after a
    /// break-in into noise, and fail2ban has been known to take a dim view of the rate.
    /// Load and uptime do not need per-sweep resolution to be useful.
    /// </summary>
    public TimeSpan MinimumInterval => TimeSpan.FromMinutes(5);

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("host", "Host", FieldKind.Text, "192.168.1.50", Required: true,
            Help: "Reachable from inside LabbyTwo's container, which is not the same thing as reachable " +
                  "from your laptop. A container name works if you share its network."),

        new("username", "Username", FieldKind.Text, "root", Required: true),

        new("password", "Password", FieldKind.Password,
            Help: "Encrypted at rest, like every other password here. Leave it empty if you are using a key."),

        new("key_path", "Private key file", FieldKind.Text, "/app/data/keys/id_ed25519",
            Help: "A path inside LabbyTwo's container, so the key has to be mounted into it:\n" +
                  "  services:\n    labbytwo:\n      volumes:\n" +
                  "        - ~/.ssh/id_ed25519:/app/data/keys/id_ed25519:ro\n" +
                  "Deliberately a path rather than a box to paste the key into: an encrypted field here is " +
                  "one line, and a PEM is not — and a key that stays a file on your disk is a key that is " +
                  "not in the database at all."),

        new("key_passphrase", "Key passphrase", FieldKind.Password) { Advanced = true },

        new("host_fingerprint", "Host key fingerprint", FieldKind.Text, "SHA256:…",
            Help: "Pin the host key. Leave it empty and the first key offered is accepted and printed by " +
                  "Test connection — paste that back in here and a key that changes afterwards stops the " +
                  "connection instead of quietly being trusted.")
        { Advanced = true },

        new("port", "Port", FieldKind.Number, Default: "22") { Advanced = true },
        new("timeout", "Timeout (seconds)", FieldKind.Number, Default: "15") { Advanced = true },
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("load1", "Load average", "", 2),

        // The one that means the same thing on every machine. A load of 8 is idle on a
        // 32-core server and on fire on a Pi, so an alert rule written against load1
        // cannot be shared between hosts and this one can.
        new("load_per_core", "Load per core", "", 2),

        new("memory_percent", "Memory used", "%", 0),
        new("uptime_days", "Uptime", " days", 1),
        new("cpu_count", "Cores"),
        new("latency_ms", "Login time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Overloaded", "load_per_core", Comparison.Above, 2, ForMinutes: 15,
            Why: "Twice as much work queued as there are cores to do it, for a quarter of an hour. " +
                 "Per core rather than raw load, so the same rule fits the NAS and the Pi."),

        new("Memory nearly gone", "memory_percent", Comparison.Above, 92, ForMinutes: 15,
            Why: "What the out-of-memory killer looks like just before it picks something to stop."),

        new("Rebooted", "uptime_days", Comparison.Below, 0.02, ForMinutes: 0,
            Why: "Up for under half an hour. A machine that reboots on its own is worth knowing about, " +
                 "and this is the only thing here that can see it — a service comes back before anything " +
                 "notices it went."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var watch = new SshHost.HostKeyWatch();

        try
        {
            using var client = SshHost.Client(connection, watch);
            await client.ConnectAsync(ct);
            stopwatch.Stop();

            var metrics = new Dictionary<string, double>
            {
                ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
            };

            var readings = await ReadStatsAsync(client, ct);
            foreach (var (key, value) in readings)
                metrics[key] = value;

            return ProbeResult.Up(stopwatch.Elapsed, Summarise(connection, watch, readings), metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            return ProbeResult.Down(stopwatch.Elapsed,
                watch.Rejected ? SshHost.KeyChanged(watch) : Explain(connection, ex));
        }
    }

    private static string Summarise(
        Connection connection, SshHost.HostKeyWatch watch, IReadOnlyDictionary<string, double> readings)
    {
        var user = connection.Settings.Get("username");
        var host = connection.Settings.Get("host");
        var summary = $"Logged in as {user}@{host}";

        if (readings.TryGetValue("load1", out var load) && readings.TryGetValue("uptime_days", out var days))
            summary += $" — load {load:0.00}, up {days:0.#} days";

        // Only when there is nothing pinned. Once the field is filled in, repeating the
        // fingerprint on every successful probe is noise on the one line the tile shows.
        if (connection.Settings.Get("host_fingerprint").Length == 0 && watch.Seen.Length > 0)
            summary += $". Host key {SshHost.Fingerprint.Display(watch.Seen)} — paste it into the connection to pin it.";

        return summary;
    }

    /// <summary>
    /// One command, four files, and no failure if the far end has none of them. Plenty of
    /// things answer SSH without being Linux — a switch, a BSD box, a NAS with a cut-down
    /// firmware — and "I could log in but there is no /proc" is a machine that is up.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, double>> ReadStatsAsync(
        Renci.SshNet.SshClient client, CancellationToken ct)
    {
        var readings = new Dictionary<string, double>();

        try
        {
            using var command = client.CreateCommand(
                "cat /proc/loadavg 2>/dev/null; echo ---; " +
                "cat /proc/uptime 2>/dev/null; echo ---; " +
                "grep -E '^(MemTotal|MemAvailable):' /proc/meminfo 2>/dev/null; echo ---; " +
                "nproc 2>/dev/null");

            command.CommandTimeout = TimeSpan.FromSeconds(10);

            // ExecuteAsync returns the lifetime of the command, not its output — that is
            // on Result once it has finished.
            await command.ExecuteAsync(ct);

            var sections = (command.Result ?? "").Split("---", StringSplitOptions.TrimEntries);
            if (sections.Length < 4)
                return readings;

            if (Number(sections[0].Split(' ').FirstOrDefault()) is { } load)
                readings["load1"] = load;

            if (Number(sections[1].Split(' ').FirstOrDefault()) is { } seconds)
                readings["uptime_days"] = seconds / 86400d;

            double total = 0, available = 0;
            foreach (var line in sections[2].Split('\n', StringSplitOptions.TrimEntries))
            {
                var value = Number(line.Split(':', 2).ElementAtOrDefault(1)?.Replace("kB", "").Trim());
                if (value is null)
                    continue;
                if (line.StartsWith("MemTotal", StringComparison.Ordinal))
                    total = value.Value;
                else
                    available = value.Value;
            }

            // MemAvailable rather than MemFree on purpose: on a machine that has been up
            // a while, free memory is near zero and reclaimable cache is most of it, so
            // MemFree would report every healthy Linux box as out of memory.
            if (total > 0 && available > 0)
                readings["memory_percent"] = (total - available) / total * 100d;

            if (Number(sections[3]) is { } cores && cores >= 1)
            {
                readings["cpu_count"] = cores;
                if (readings.TryGetValue("load1", out var load1))
                    readings["load_per_core"] = load1 / cores;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A restricted or sftp-only account cannot run a command at all. The login
            // still proved the connection works, which is what the probe is for.
        }

        return readings;
    }

    private static double? Number(string? raw) =>
        double.TryParse(raw?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static string Explain(Connection connection, Exception ex)
    {
        var host = connection.Settings.Get("host");
        var message = ex.GetBaseException().Message;

        if (ex is Renci.SshNet.Common.SshAuthenticationException)
        {
            return connection.Settings.Get("key_path").Length > 0
                ? $"{host} refused the login: {message}. Check the key is the one whose public half is in " +
                  $"authorized_keys for {connection.Settings.Get("username")}, and that it is readable inside " +
                  "LabbyTwo's container."
                : $"{host} refused the login: {message}.";
        }

        // The file-not-found from SshHost carries its own compose snippet; do not bury it.
        if (ex is FileNotFoundException or InvalidOperationException)
            return message;

        return ProbeError.Describe(ex, $"{host}:{connection.Settings.GetInt("port", 22)}");
    }
}
