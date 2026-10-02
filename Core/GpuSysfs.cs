using System.Globalization;
using System.Text.RegularExpressions;

namespace LabbyTwo.Core;

/// <summary>
/// One look at an Intel GPU through sysfs: the files the i915 and xe kernel drivers publish
/// under <c>/sys/class/drm/cardN</c> for anyone to read.
/// </summary>
/// <param name="Root">The drm directory the card was found under.</param>
/// <param name="Card">"card0", "card1".</param>
/// <param name="Driver">"i915" or "xe" when the kernel says; empty when it does not.</param>
/// <param name="ActualMhz">What the GPU is actually clocked at right now. Zero while it sleeps.</param>
/// <param name="MaxMhz">The most it can be clocked at — the hardware's RP0, else the configured ceiling.</param>
/// <param name="SleepMs">The running total of milliseconds spent in RC6, the deep-sleep state, since boot.</param>
public sealed record GpuSample(
    string Root,
    string Card,
    string Driver,
    double? ActualMhz,
    double? MaxMhz,
    double? SleepMs)
{
    /// <summary>Whether anything at all was readable — a card whose files all failed is no reading.</summary>
    public bool HasAnything => ActualMhz is not null || MaxMhz is not null || SleepMs is not null;
}

/// <summary>What looking for the GPU found: a sample, or the plain reason there is none.</summary>
public sealed record GpuLook(GpuSample? Sample, string Why);

/// <summary>
/// How busy the Intel GPU is, read the only way a container can without privileges.
///
/// The real measure of busyness — per-engine utilisation, what <c>intel_gpu_top</c> shows —
/// comes from the i915 perf PMU, which needs <c>CAP_PERFMON</c> or a privileged container
/// and a <c>perf_event_open</c> syscall. LabbyTwo asks for neither: a dashboard is the last
/// thing that should hold the keys to the host's kernel. What is left is sysfs, which the
/// drivers publish world-readable:
///
/// <list type="bullet">
/// <item>the actual GPU frequency, and the most it can reach — a GPU at its ceiling is working,
/// one at zero is asleep;</item>
/// <item>RC6 residency, a running total of the time the GPU spent in its deep-sleep state. Two
/// samples apart, the share of the gap it was <em>not</em> asleep is how much of the time it
/// was awake. That is an upper bound on busyness rather than busyness itself — a GPU doing a
/// little every few milliseconds stays out of RC6 — but it tracks a transcode closely, and
/// "it never slept" is exactly the signal that a third job will not fit.</item>
/// </list>
///
/// Per-engine figures (the video engine Quick Sync uses, apart from render) are not in sysfs
/// on any kernel this has met, so they are not offered. Every path is tried in each of the
/// layouts the drivers have used — legacy i915 (<c>gt_act_freq_mhz</c>), per-GT i915
/// (<c>gt/gt0/rps_act_freq_mhz</c>) and xe (<c>device/tile0/gt0/freq0/act_freq</c>) — and a
/// file that is missing or unreadable costs that one number, never the reading.
///
/// Docker does not namespace sysfs, so <c>/sys/class/drm</c> inside a container is usually
/// the host's own and readable as it is. Where firmware hides it, the host's can be mounted
/// read-only at <c>/host/sys</c> — see the README for why that takes two lines, not one.
/// </summary>
public static partial class GpuSysfs
{
    /// <summary>Where to look when the connection names nowhere: the container's own sysfs, then a mounted host copy.</summary>
    public static readonly IReadOnlyList<string> DefaultRoots = ["/sys/class/drm", "/host/sys/class/drm"];

    /// <summary>The plain answer for when nothing can be read, as the card and the probe both say it.</summary>
    public const string NotVisible =
        "GPU load isn't visible from inside a container on this system; showing who's transcoding instead.";

    [GeneratedRegex(@"^card\d+$")]
    private static partial Regex CardName();

    private static readonly string[] ActualPaths =
        ["gt_act_freq_mhz", "gt/gt0/rps_act_freq_mhz", "device/tile0/gt0/freq0/act_freq"];

    private static readonly string[] MaxPaths =
    [
        "gt_RP0_freq_mhz", "gt/gt0/rps_RP0_freq_mhz", "device/tile0/gt0/freq0/rp0_freq",
        "gt_max_freq_mhz", "gt/gt0/rps_max_freq_mhz", "device/tile0/gt0/freq0/max_freq",
    ];

    private static readonly string[] SleepPaths =
        ["power/rc6_residency_ms", "gt/gt0/rc6_residency_ms", "device/tile0/gt0/gtidle/idle_residency_ms"];

    /// <summary>
    /// The first Intel GPU readable under any of <paramref name="roots"/>, or why there is
    /// none. <paramref name="card"/> narrows it to one card by name for a box with two.
    /// Never throws; a handful of small file reads, so cheap enough for every sweep.
    /// </summary>
    public static GpuLook Look(IEnumerable<string> roots, string? card = null)
    {
        var sawCards = false;
        foreach (var root in roots)
        {
            IEnumerable<string> cards;
            try
            {
                if (!Directory.Exists(root))
                    continue;
                // File-system entries rather than directories: every cardN in /sys/class/drm is
                // a symbolic link, and whether a directory listing follows one varies.
                cards = Directory.EnumerateFileSystemEntries(root)
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .Where(name => CardName().IsMatch(name))
                    .Where(name => string.IsNullOrWhiteSpace(card) || string.Equals(name, card.Trim(), StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var name in cards)
            {
                sawCards = true;
                var sample = Read(root, name);
                if (sample.HasAnything)
                    return new GpuLook(sample, "");
            }
        }

        return new GpuLook(null, sawCards
            ? "The GPU is listed but none of its frequency or sleep files can be read — it is not an Intel GPU, " +
              "or only /sys/class/drm was mounted and the links in it lead to /sys/devices, which was not. " + NotVisible
            : NotVisible);
    }

    /// <summary>One card's files, each read on its own so a missing one costs only itself.</summary>
    public static GpuSample Read(string root, string card)
    {
        var directory = Path.Combine(root, card);
        return new GpuSample(
            root,
            card,
            Driver(directory),
            First(directory, ActualPaths),
            First(directory, MaxPaths),
            First(directory, SleepPaths));
    }

    /// <summary>
    /// The share of the time between two samples the GPU was out of RC6, 0–100. Null when
    /// either lacks the counter, the gap is too short to mean anything, or the counter went
    /// backwards — a reboot, or a different card answering.
    /// </summary>
    public static double? BusyPercent(double previousSleepMs, DateTimeOffset previousAt, double sleepMs, DateTimeOffset at)
    {
        var wall = (at - previousAt).TotalMilliseconds;
        var slept = sleepMs - previousSleepMs;
        if (wall < 1000 || slept < 0)
            return null;
        return Math.Clamp(100 * (1 - slept / wall), 0, 100);
    }

    private static double? First(string directory, IEnumerable<string> paths)
    {
        foreach (var relative in paths)
        {
            if (Number(Path.Combine(directory, relative)) is { } value)
                return value;
        }
        return null;
    }

    private static double? Number(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var text = File.ReadAllText(path).Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                ? value
                : null;
        }
        catch (Exception)
        {
            // Permission denied, or a link into a directory that was not mounted.
            return null;
        }
    }

    /// <summary>The kernel driver, from the device's uevent file, else where its driver link points.</summary>
    private static string Driver(string directory)
    {
        try
        {
            var uevent = Path.Combine(directory, "device", "uevent");
            if (File.Exists(uevent))
            {
                foreach (var line in File.ReadLines(uevent))
                {
                    if (line.StartsWith("DRIVER=", StringComparison.Ordinal))
                        return line["DRIVER=".Length..].Trim();
                }
            }

            var link = new DirectoryInfo(Path.Combine(directory, "device", "driver"));
            if (link.LinkTarget is { Length: > 0 } target)
                return Path.GetFileName(target.TrimEnd('/'));
        }
        catch (Exception)
        {
            // Nothing lost but the name.
        }
        return "";
    }
}
