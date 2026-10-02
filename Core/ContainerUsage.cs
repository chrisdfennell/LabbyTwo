using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>
/// One container's resources at one moment, as the resource poller worked them out from two
/// of Docker's stats answers. Rates are null until there are two samples to difference, and
/// again straight after a counter went backwards (the container restarted between them).
/// </summary>
/// <param name="Id">The full container id.</param>
/// <param name="Name">Its name without Docker's leading slash — the key everything else uses.</param>
/// <param name="State">Docker's word for it: running, paused, exited…</param>
/// <param name="CpuPercent">As <c>docker stats</c> shows it: one busy core is 100%.</param>
/// <param name="MemoryUsed">Bytes, with the page cache the kernel can drop taken off.</param>
/// <param name="MemoryLimit">Bytes. A container with no limit reports the host's memory here.</param>
/// <param name="ReadPerSecond">Block I/O read, bytes per second.</param>
/// <param name="WritePerSecond">Block I/O written, bytes per second.</param>
/// <param name="RxPerSecond">Network received, bytes per second.</param>
/// <param name="TxPerSecond">Network sent, bytes per second.</param>
/// <param name="Pids">Processes and threads in it, when the kernel says.</param>
/// <param name="At">When Docker read it.</param>
public sealed record ContainerReading(
    string Id,
    string Name,
    string State,
    double? CpuPercent,
    ulong MemoryUsed,
    ulong MemoryLimit,
    double? ReadPerSecond,
    double? WritePerSecond,
    double? RxPerSecond,
    double? TxPerSecond,
    ulong? Pids,
    DateTimeOffset At)
{
    public bool IsRunning => State == "running";

    /// <summary>Read and write together, bytes per second; null when neither is known yet.</summary>
    public double? DiskPerSecond => ReadPerSecond is null && WritePerSecond is null
        ? null
        : (ReadPerSecond ?? 0) + (WritePerSecond ?? 0);

    /// <summary>Memory as a share of its limit; null when it has no limit of its own.</summary>
    public double? MemoryPercent(ulong hostMemory = 0) =>
        ContainerUsage.HasOwnLimit(MemoryLimit, hostMemory) ? MemoryUsed * 100.0 / MemoryLimit : null;
}

/// <summary>
/// The arithmetic and the choices behind "who is eating the NAS": which containers get a
/// history, under which metric names, and how a busy one is described in a sentence.
///
/// <para><b>Why only some containers are recorded.</b> Polling every running container is
/// cheap — one short request each, a few at a time — but recording every one is not: forty
/// containers times four numbers is a hundred and sixty series in a samples table that, on
/// the installation this was built for, is already 580 MB. So every container is measured
/// and shown live, but only the <see cref="DefaultTop"/> busiest by CPU or by disk, plus any
/// pinned by name, are written to history. Idle containers never qualify, so on a quiet
/// night only the handful doing anything are recorded at all. A container that was recorded
/// in the last hour stays in the live readings, at zero if it stopped, so an alert on it can
/// see it go quiet and clear rather than being left firing on its last value.</para>
///
/// <para><b>Why the keys look like <c>container_cpu:tdarr</c>.</b> It is the same shape as a
/// NAS volume's <c>disk_percent:vol2</c> (<see cref="VolumeMetric"/>): the measured metric,
/// a colon, the slugged name. Everything that understands the measured metric's unit
/// understands each container's without being told again, and the metric pickers, charts,
/// <c>{{metric}}</c> and <c>{{table}}</c> see them as ordinary metrics of the Docker
/// connection.</para>
/// </summary>
public static class ContainerUsage
{
    public const string CpuMetric = "container_cpu";
    public const string MemoryMetric = "container_mem_mb";
    public const string ReadMetric = "container_read_mbs";
    public const string WriteMetric = "container_write_mbs";

    /// <summary>The busiest container's CPU — one number a single alert rule can watch for "any container".</summary>
    public const string BusiestCpuMetric = "container_cpu_max";

    /// <summary>The highest disk read rate of any container, MB/s.</summary>
    public const string BusiestReadMetric = "container_read_max_mbs";

    /// <summary>The container closest to its own memory limit, as a percentage of that limit.</summary>
    public const string FullestMemoryMetric = "container_mem_limit_max_percent";

    public static readonly IReadOnlyList<string> PerContainerMetrics = [CpuMetric, MemoryMetric, ReadMetric, WriteMetric];

    public const int DefaultTop = 10;
    public const int MaxTop = 25;
    public const int DefaultSeconds = 60;
    public const int MinSeconds = 30;
    public const int MaxSeconds = 300;

    /// <summary>
    /// Below this a container is idle as far as history is concerned. Half a percent of one
    /// core, or a tenth of a megabyte a second: the background hum of a sleeping container.
    /// </summary>
    public const double IdleCpu = 0.5;

    public const double IdleDiskBytes = 100_000;

    /// <summary>How long a container recorded once stays in the live readings after it stops qualifying.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromHours(1);

    /// <summary>Megabytes as rates are written: decimal, like a disk's own figures.</summary>
    public const double Megabyte = 1_000_000;

    /// <summary>Memory as <c>docker stats</c> writes it: binary.</summary>
    public const double Mebibyte = 1024 * 1024;

    public static string KeyFor(string metric, string container) => VolumeMetric.KeyFor(metric, container);

    /// <summary>Whether a key is one container's share of one of the per-container metrics.</summary>
    public static bool TryParse(string? key, out string metric, out string container)
    {
        if (VolumeMetric.TryParse(key, out metric, out container) && PerContainerMetrics.Contains(metric))
            return true;
        metric = "";
        container = "";
        return false;
    }

    /// <summary>The configured poll interval, held inside what the NAS can afford; 0 means off.</summary>
    public static int Seconds(int configured) =>
        configured <= 0 ? 0 : Math.Clamp(configured, MinSeconds, MaxSeconds);

    public static int Top(int configured) => Math.Clamp(configured, 0, MaxTop);

    /// <summary>The pinned list as typed: names separated by commas, spaces or new lines.</summary>
    public static IReadOnlyList<string> ParsePinned(string? text) =>
        [.. (text ?? "").Split([',', ' ', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.TrimStart('/'))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// A memory limit of the container's own. With none set, Docker reports the host's
    /// memory (or near enough — some kernels report the largest page-aligned value), and
    /// "40% of its limit" would really mean "40% of the NAS".
    /// </summary>
    public static bool HasOwnLimit(ulong limit, ulong hostMemory)
    {
        if (limit == 0 || limit >= (ulong)long.MaxValue / 2)
            return false;
        return hostMemory == 0 || limit < hostMemory * 0.98;
    }

    private static bool IsBusy(ContainerReading reading) =>
        reading.CpuPercent > IdleCpu || reading.DiskPerSecond > IdleDiskBytes;

    /// <summary>
    /// The containers worth a history this round: the busiest by CPU and the busiest by disk,
    /// taken in turn so neither crowds the other out, until there are <paramref name="top"/>;
    /// then every pinned one that is there, whatever it is doing. Ties go by name, so the
    /// same readings always choose the same containers.
    /// </summary>
    public static IReadOnlyList<string> Choose(IReadOnlyList<ContainerReading> readings, int top, IReadOnlyCollection<string> pinned)
    {
        var busy = readings.Where(r => r.IsRunning && IsBusy(r)).ToList();
        var byCpu = busy.Where(r => r.CpuPercent > IdleCpu)
            .OrderByDescending(r => r.CpuPercent).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        var byDisk = busy.Where(r => r.DiskPerSecond > IdleDiskBytes)
            .OrderByDescending(r => r.DiskPerSecond).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();

        var chosen = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; chosen.Count < top && (i < byCpu.Count || i < byDisk.Count); i++)
        {
            if (i < byCpu.Count && chosen.Count < top && seen.Add(byCpu[i].Name))
                chosen.Add(byCpu[i].Name);
            if (i < byDisk.Count && chosen.Count < top && seen.Add(byDisk[i].Name))
                chosen.Add(byDisk[i].Name);
        }

        foreach (var name in pinned)
        {
            var match = readings.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null && seen.Add(match.Name))
                chosen.Add(match.Name);
        }
        return chosen;
    }

    /// <summary>
    /// One container's four numbers under its own keys. A stopped or paused one is at zero
    /// CPU and zero disk, which is true, and is what lets an alert on it clear; its memory is
    /// left out rather than claimed to be zero.
    /// </summary>
    public static void AddMetrics(IDictionary<string, double> metrics, ContainerReading reading)
    {
        if (!reading.IsRunning)
        {
            metrics[KeyFor(CpuMetric, reading.Name)] = 0;
            metrics[KeyFor(ReadMetric, reading.Name)] = 0;
            metrics[KeyFor(WriteMetric, reading.Name)] = 0;
            return;
        }

        if (reading.CpuPercent is { } cpu)
            metrics[KeyFor(CpuMetric, reading.Name)] = Math.Round(cpu, 1);
        metrics[KeyFor(MemoryMetric, reading.Name)] = Math.Round(reading.MemoryUsed / Mebibyte, 1);
        if (reading.ReadPerSecond is { } read)
            metrics[KeyFor(ReadMetric, reading.Name)] = Math.Round(read / Megabyte, 2);
        if (reading.WritePerSecond is { } write)
            metrics[KeyFor(WriteMetric, reading.Name)] = Math.Round(write / Megabyte, 2);
    }

    /// <summary>
    /// The three "any container" numbers, over every running container — not only the
    /// recorded ones, because the container that has just started eating the NAS is by
    /// definition one that was not busy a minute ago.
    /// </summary>
    public static void AddBusiest(IDictionary<string, double> metrics, IReadOnlyList<ContainerReading> readings, ulong hostMemory)
    {
        var running = readings.Where(r => r.IsRunning).ToList();
        if (running.Count == 0)
            return;

        if (running.Where(r => r.CpuPercent is not null).Select(r => r.CpuPercent!.Value).DefaultIfEmpty(double.NaN).Max() is var cpu && !double.IsNaN(cpu))
            metrics[BusiestCpuMetric] = Math.Round(cpu, 1);
        if (running.Where(r => r.ReadPerSecond is not null).Select(r => r.ReadPerSecond!.Value).DefaultIfEmpty(double.NaN).Max() is var read && !double.IsNaN(read))
            metrics[BusiestReadMetric] = Math.Round(read / Megabyte, 2);
        if (running.Select(r => r.MemoryPercent(hostMemory)).Where(p => p is not null).Select(p => p!.Value).DefaultIfEmpty(double.NaN).Max() is var memory && !double.IsNaN(memory))
            metrics[FullestMemoryMetric] = Math.Round(memory, 1);
    }

    public static ContainerReading? BusiestByCpu(IEnumerable<ContainerReading> readings) =>
        readings.Where(r => r.IsRunning && r.CpuPercent is not null)
            .OrderByDescending(r => r.CpuPercent).ThenBy(r => r.Name, StringComparer.Ordinal).FirstOrDefault();

    public static ContainerReading? BusiestByDisk(IEnumerable<ContainerReading> readings) =>
        readings.Where(r => r.IsRunning && r.DiskPerSecond is not null)
            .OrderByDescending(r => r.DiskPerSecond).ThenBy(r => r.Name, StringComparer.Ordinal).FirstOrDefault();

    public static ContainerReading? FullestMemory(IEnumerable<ContainerReading> readings, ulong hostMemory) =>
        readings.Where(r => r.IsRunning && r.MemoryPercent(hostMemory) is not null)
            .OrderByDescending(r => r.MemoryPercent(hostMemory)).ThenBy(r => r.Name, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>
    /// The busiest few, worst first, by how much of the NAS they are taking: CPU in cores
    /// and disk in tens of megabytes a second count about the same. The order a person
    /// reading "the NAS is struggling" wants — the one most likely to be the cause first.
    /// </summary>
    public static IReadOnlyList<ContainerReading> Busiest(IEnumerable<ContainerReading> readings, int count) =>
        [.. readings.Where(r => r.IsRunning && IsBusy(r))
            .OrderByDescending(Weight).ThenBy(r => r.Name, StringComparer.Ordinal)
            .Take(count)];

    private static double Weight(ContainerReading reading) =>
        (reading.CpuPercent ?? 0) / 100 + (reading.DiskPerSecond ?? 0) / (10 * Megabyte);

    /// <summary>"tdarr 193% CPU, 40 MB/s read" — what it is doing that matters, nothing that doesn't.</summary>
    public static string Describe(ContainerReading reading)
    {
        var parts = new List<string>();
        if (reading.CpuPercent is { } cpu && cpu > IdleCpu)
            parts.Add($"{Number(cpu)}% CPU");
        if (reading.ReadPerSecond is { } read && read > IdleDiskBytes)
            parts.Add($"{Rate(read)} read");
        if (reading.WritePerSecond is { } write && write > IdleDiskBytes)
            parts.Add($"{Rate(write)} written");
        return parts.Count == 0 ? $"{reading.Name} (quiet)" : $"{reading.Name} {string.Join(", ", parts)}";
    }

    /// <summary>A byte rate in decimal megabytes, or kilobytes when it is small: "40 MB/s", "350 kB/s".</summary>
    public static string Rate(double bytesPerSecond)
    {
        var mb = bytesPerSecond / Megabyte;
        if (mb >= 1)
            return $"{Number(mb)} MB/s";
        var kb = bytesPerSecond / 1000;
        return kb >= 1 ? $"{kb.ToString("0", CultureInfo.InvariantCulture)} kB/s" : "0 kB/s";
    }

    private static string Number(double value) =>
        value.ToString(value >= 10 ? "0" : "0.#", CultureInfo.InvariantCulture);
}
