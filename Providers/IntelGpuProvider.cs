using System.Collections.Concurrent;
using System.Diagnostics;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;

namespace LabbyTwo.Providers;

/// <summary>
/// An Intel GPU shared by several containers — Plex, Tdarr and Tunarr all handed the same
/// <c>/dev/dri</c> — and the two questions worth asking of it: who is using Quick Sync, and
/// how busy is it.
///
/// The first is answered from the apps. Plex (or Tautulli), Tdarr and Tunarr each say what
/// they have on the GPU in their own probe; this reads the monitor's last answer from each
/// rather than asking again, so it costs nothing on the network. The second is answered from
/// sysfs where the kernel publishes it (<see cref="GpuSysfs"/>) — no privileged container, no
/// perf syscalls — and where it does not, the probe says so plainly and the app-level numbers
/// stand on their own.
///
/// It is a connection rather than only a card so that its numbers are recorded like any
/// other: a chart of GPU busy beside Plex's transcodes, and an alert rule on
/// <c>overlap</c> — Tdarr on the GPU while Plex is transcoding on it — which no single app's
/// connection could express. Those cross-app numbers lag the apps' own by up to one sweep,
/// which is nothing against a rule that waits ten minutes.
/// </summary>
public sealed class IntelGpuProvider(IServiceProvider services) : IConnectionProvider
{
    public const string ProviderType = "intel-gpu";

    public string Type => ProviderType;
    public string DisplayName => "Intel GPU (Quick Sync)";
    public string Icon => "🖥️";
    public string Category => "Devices";
    public string Description =>
        "Who is using the shared Intel GPU — Plex, Tdarr, Tunarr — and, where the kernel lets a container see it, how busy it is.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("path", "sysfs drm folder", FieldKind.Text, "/sys/class/drm",
            Help: "Blank tries /sys/class/drm and then /host/sys/class/drm. Docker usually shows the host's own " +
                  "here already; if not, mount the host's read-only — see the README's GPU section."),
        new("card", "Card", FieldKind.Text, "card0",
            Help: "Only for a box with more than one GPU. Blank uses the first Intel one found."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("gpu_busy_percent", "GPU busy", "%"),
        new("freq_act_mhz", "GPU clock", " MHz"),
        new("freq_max_mhz", "GPU clock ceiling", " MHz"),
        new("plex_hw_transcodes", "Plex hardware transcodes"),
        new("tdarr_gpu_workers", "Tdarr GPU workers"),
        new("tunarr_streams", "Tunarr channels on the GPU"),
        new("apps_on_gpu", "Apps on the GPU"),
        new("overlap", "Tdarr and Plex both on the GPU"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Tdarr is competing with Plex for the GPU", "overlap", Comparison.Above, 0.5, ForMinutes: 10,
            Why: "Ten minutes of Tdarr transcoding on the GPU while somebody is watching a hardware transcode on Plex. " +
                 "Worth pausing Tdarr's GPU workers, or scheduling them for the night."),

        new("The GPU is flat out", "gpu_busy_percent", Comparison.Above, GpuUsage.FlatOutPercent, ClearThreshold: 75, ForMinutes: 15,
            Why: "Only works where the GPU's load is visible. Fifteen minutes without sleeping means the next transcode will not fit."),
    ];

    /// <summary>The previous RC6 total per connection, which a busy percentage is the difference from.</summary>
    private readonly ConcurrentDictionary<string, (string Card, double SleepMs, DateTimeOffset At)> _previous = new();

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var path = connection.Settings.Get("path").Trim();
            var roots = path.Length > 0 ? [path] : GpuSysfs.DefaultRoots;
            var look = GpuSysfs.Look(roots, connection.Settings.Get("card"));

            var sources = await SourcesAsync(ct);
            var result = Read(connection, look, sources, DateTimeOffset.UtcNow);
            stopwatch.Stop();
            return result with { Duration = stopwatch.Elapsed };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, $"Could not look at the GPU: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// The probe's arithmetic, apart from the file reads and the monitor, so a test can hand
    /// it a sample and the apps' numbers. Always up: the GPU being invisible is a fact about
    /// the container, not an outage, and the app-level numbers are still worth recording.
    /// </summary>
    public ProbeResult Read(Connection connection, GpuLook look, IEnumerable<GpuSource> sources, DateTimeOffset at)
    {
        var metrics = new Dictionary<string, double>();
        var details = new Dictionary<string, string>();
        double? busy = null;

        if (look.Sample is { } sample)
        {
            if (sample.ActualMhz is { } actual)
                metrics["freq_act_mhz"] = actual;
            if (sample.MaxMhz is { } max)
                metrics["freq_max_mhz"] = max;

            if (sample.SleepMs is { } sleep)
            {
                if (_previous.TryGetValue(connection.Id, out var before) && before.Card == sample.Card)
                    busy = GpuSysfs.BusyPercent(before.SleepMs, before.At, sleep, at);
                _previous[connection.Id] = (sample.Card, sleep, at);
            }
            if (busy is { } b)
                metrics["gpu_busy_percent"] = b;
            else if (sample.SleepMs is not null)
                details["load"] = "measuring";

            details["card"] = sample.Card;
            if (sample.Driver.Length > 0)
                details["driver"] = sample.Driver;
        }
        else
        {
            details["host"] = look.Why;
        }

        var picture = GpuUsage.From(sources, busy);
        metrics["plex_hw_transcodes"] = picture.PlexHardware;
        metrics["tdarr_gpu_workers"] = picture.TdarrGpu;
        metrics["tunarr_streams"] = picture.TunarrStreams;
        metrics["apps_on_gpu"] = picture.AppsOnGpu;
        metrics["overlap"] = picture.PlexAndTdarrOverlap ? 1 : 0;

        var who = picture.Users.Count == 0
            ? "no Plex, Tdarr or Tunarr connected"
            : picture.AppsOnGpu switch
            {
                0 => "nothing on Quick Sync",
                1 => "1 app on Quick Sync",
                var n => $"{n} apps on Quick Sync",
            };

        string message;
        if (look.Sample is { } seen)
        {
            var load = busy is { } percent ? $"{percent:0}% busy"
                : seen.SleepMs is not null ? "measuring load…"
                : null;
            var clock = seen.ActualMhz is { } act
                ? seen.MaxMhz is { } ceiling ? $"{act:0}/{ceiling:0} MHz" : $"{act:0} MHz"
                : null;
            message = string.Join(" · ", new[] { load, clock, who }.Where(p => p is not null));
        }
        else
        {
            message = $"Load not visible from this container · {who}";
        }

        return ProbeResult.Up(TimeSpan.Zero, message, metrics, details);
    }

    /// <summary>
    /// The monitor's last word on every Plex, Tautulli, Tdarr and Tunarr connection. Resolved
    /// when used rather than injected: the monitor is built from the registry, which is built
    /// from every provider, this one included.
    /// </summary>
    private async Task<IReadOnlyList<GpuSource>> SourcesAsync(CancellationToken ct)
    {
        var config = services.GetService<ConfigStore>();
        var health = services.GetService<HealthMonitor>();
        if (config is null || health is null)
            return [];

        var connections = await config.ConnectionsAsync(ct);
        return GpuSources(connections, health);
    }

    /// <summary>The connections the GPU picture is drawn from, with their last probes. Shared with the GPU card.</summary>
    public static IReadOnlyList<GpuSource> GpuSources(IEnumerable<Connection> connections, HealthMonitor health) =>
    [
        .. connections
            .Where(c => c.Enabled && GpuUsage.Providers.Contains(c.Provider, StringComparer.OrdinalIgnoreCase))
            .OrderBy(c => c.Sort).ThenBy(c => c.Name)
            .Select(c =>
            {
                var state = health.State(c.Id);
                return new GpuSource(c.Provider, c.Name, state?.IsUp,
                    state?.Metrics ?? new Dictionary<string, double>(),
                    state?.Details);
            }),
    ];
}
