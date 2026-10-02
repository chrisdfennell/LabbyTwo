namespace LabbyTwo.Core;

/// <summary>One connection's last probe, as far as the GPU picture needs it.</summary>
/// <param name="Provider">Its provider type: "plex", "tautulli", "tdarr", "tunarr".</param>
/// <param name="Name">What the user called it.</param>
/// <param name="IsUp">The monitor's answer; null before the first probe.</param>
public sealed record GpuSource(
    string Provider,
    string Name,
    bool? IsUp,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyDictionary<string, string>? Details = null);

/// <summary>One app sharing the GPU, as a row on the card.</summary>
/// <param name="App">"Plex", "Tdarr", "Tunarr".</param>
/// <param name="Name">The connection's own name, for a box running two of something.</param>
/// <param name="OnGpu">How many jobs it has on the GPU right now.</param>
/// <param name="Summary">What those are, in words: "2 hardware transcodes", "nothing on the GPU".</param>
/// <param name="Note">Anything worth saying beside it — software transcodes, a CPU worker — or empty.</param>
/// <param name="Answering">False when the connection is down or not yet probed, so the numbers are not current.</param>
public sealed record GpuUser(string App, string Name, int OnGpu, string Summary, string Note, bool Answering);

/// <summary>Who is on the GPU, and whether they are getting in each other's way.</summary>
public sealed record GpuPicture(
    IReadOnlyList<GpuUser> Users,
    int PlexHardware,
    int TdarrGpu,
    int TunarrStreams,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// Tdarr has a GPU worker busy while Plex has a hardware transcode — the combination that
    /// makes a film stutter, and what the suggested alert watches.
    /// </summary>
    public bool PlexAndTdarrOverlap => TdarrGpu > 0 && PlexHardware > 0;

    /// <summary>How many apps have something on the GPU.</summary>
    public int AppsOnGpu => Users.Where(u => u.OnGpu > 0).Select(u => u.App).Distinct().Count();
}

/// <summary>
/// Who is using an Intel iGPU shared between containers, worked out from what the apps say
/// rather than from the GPU — the GPU cannot be asked from inside a container without
/// privileges (see <see cref="GpuSysfs"/>), but every app that uses it already reports what
/// it is doing, and LabbyTwo already asks each one every sweep. Nothing here makes a request:
/// it reads the monitor's last probe of each connection.
///
/// Plex and Tautulli describe the same server, so a Tautulli connection is only counted when
/// no Plex connection is answering with the hardware split — two rows saying the same two
/// transcodes would be counted as four.
/// </summary>
public static class GpuUsage
{
    /// <summary>The providers whose numbers the picture is made from.</summary>
    public static readonly IReadOnlyList<string> Providers = ["plex", "tautulli", "tdarr", "tunarr"];

    /// <summary>The GPU's own load at or above which it is called nearly flat out.</summary>
    public const double FlatOutPercent = 90;

    public static GpuPicture From(IEnumerable<GpuSource> sources, double? busyPercent = null)
    {
        var list = sources.Where(s => Providers.Contains(s.Provider, StringComparer.OrdinalIgnoreCase)).ToList();
        var plexAnswers = list.Any(s => Is(s, "plex") && s.IsUp == true && s.Metrics.ContainsKey("transcodes_hw"));

        var users = new List<GpuUser>();
        int plexHardware = 0, tdarrGpu = 0, tunarr = 0;

        foreach (var source in list)
        {
            var answering = source.IsUp == true;
            var m = answering ? source.Metrics : new Dictionary<string, double>();

            if (Is(source, "plex") || Is(source, "tautulli"))
            {
                if (Is(source, "tautulli") && plexAnswers)
                    continue;
                var hardware = Count(m, "transcodes_hw");
                var software = Count(m, "transcodes_sw");
                plexHardware += hardware;
                users.Add(new GpuUser("Plex", source.Name, hardware,
                    !answering ? NotAnswering(source)
                    : hardware > 0 ? Plural(hardware, "hardware transcode")
                    : "nothing on the GPU",
                    answering && software > 0 ? $"{Plural(software, "transcode")} in software" : "",
                    answering));
            }
            else if (Is(source, "tdarr"))
            {
                var gpu = Count(m, "gpu_workers_active");
                var cpu = Count(m, "cpu_workers_active");
                tdarrGpu += gpu;
                users.Add(new GpuUser("Tdarr", source.Name, gpu,
                    !answering ? NotAnswering(source)
                    : gpu > 0 ? Plural(gpu, "GPU worker")
                    : "nothing on the GPU",
                    answering && cpu > 0 ? $"{Plural(cpu, "CPU worker")} busy" : "",
                    answering));
            }
            else if (Is(source, "tunarr"))
            {
                var streams = Count(m, "active_sessions");
                var mode = source.Details?.GetValueOrDefault("hw_accel") ?? "";
                var onIntel = mode.Length == 0 || mode.Contains("qsv", StringComparison.OrdinalIgnoreCase)
                                               || mode.Contains("vaapi", StringComparison.OrdinalIgnoreCase);
                var onGpu = onIntel ? streams : 0;
                tunarr += onGpu;
                users.Add(new GpuUser("Tunarr", source.Name, onGpu,
                    !answering ? NotAnswering(source)
                    : onGpu > 0 ? Plural(onGpu, "channel") + " streaming"
                    : streams > 0 ? Plural(streams, "channel") + " streaming, not on Quick Sync"
                    : "nothing streaming",
                    answering && !onIntel ? $"ffmpeg is set to {(mode == "none" ? "software" : mode)}" : "",
                    answering));
            }
        }

        var warnings = new List<string>();
        if (tdarrGpu > 0 && plexHardware > 0)
            warnings.Add($"Tdarr is transcoding on the GPU while Plex has {Plural(plexHardware, "hardware transcode")} — Plex may stutter. " +
                         "Pausing Tdarr's GPU workers until they finish frees it up.");
        if (tdarrGpu > 0 && tunarr > 0)
            warnings.Add($"Tdarr is transcoding on the GPU while Tunarr is streaming {Plural(tunarr, "channel")} — channels may buffer.");
        if (busyPercent >= FlatOutPercent)
            warnings.Add($"The GPU is {busyPercent:0}% busy — another transcode may not keep up.");

        return new GpuPicture(users, plexHardware, tdarrGpu, tunarr, warnings);
    }

    private static bool Is(GpuSource source, string provider) =>
        string.Equals(source.Provider, provider, StringComparison.OrdinalIgnoreCase);

    private static int Count(IReadOnlyDictionary<string, double> metrics, string key) =>
        metrics.TryGetValue(key, out var value) && double.IsFinite(value) && value > 0 ? (int)Math.Round(value) : 0;

    private static string NotAnswering(GpuSource source) => source.IsUp is null ? "checking…" : "not answering";

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
