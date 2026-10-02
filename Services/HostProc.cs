using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>
/// Reads the host's /proc from inside LabbyTwo's container. Small text files the kernel
/// writes on demand, read with no locks and no parsing beyond a split, so a read of all of
/// them costs well under a millisecond — which matters, because this runs every thirty
/// seconds on a NAS that may already be on its knees.
///
/// <para><b>What is the host's and what is the container's.</b> In a plain Docker container
/// <c>/proc/loadavg</c>, <c>/proc/stat</c>, <c>/proc/pressure/*</c> and <c>/proc/cpuinfo</c>
/// all describe the whole machine: namespaces do not virtualise them. <c>/proc/meminfo</c>
/// normally does too, but LXCFS — which some NAS container managers mount — replaces it with
/// the container's own figures; that is detected (MemTotal equal to the container's cgroup
/// limit) and said on the health page rather than silently reported as the NAS's memory.
/// <see cref="Environment.ProcessorCount"/> is not used for the core count because a CPU
/// limit on the container shrinks it, and dividing the host's load by the container's
/// share of CPUs would make a quiet NAS look overloaded.</para>
/// </summary>
/// <param name="root">Where /proc and /sys are: "/" in real life, a folder of sample files in tests.</param>
public sealed class HostProc(string root = "/")
{
    public static HostProc Default { get; } = new();

    private readonly object _sync = new();
    private ProcFiles.CpuTimes? _lastStat;
    private ulong? _memTotal;

    private string PathOf(string relative) => Path.Combine(root, relative);

    private string? ReadFile(string relative)
    {
        try
        {
            var path = PathOf(relative);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Everything at once. iowait and busy need two reads of /proc/stat, so the first call of
    /// a process has none and every later one differences against the call before it.
    /// </summary>
    public HostSignals Read(DateTimeOffset now)
    {
        var load = ProcFiles.LoadAvg(ReadFile("proc/loadavg"));
        var memory = ProcFiles.MemInfo(ReadFile("proc/meminfo"));
        var stat = ProcFiles.Stat(ReadFile("proc/stat"));

        (double Iowait, double Busy)? shares;
        lock (_sync)
        {
            shares = ProcFiles.CpuShares(_lastStat, stat);
            if (stat is not null)
                _lastStat = stat;
        }

        var (cpus, from) = CpuCount();
        ulong? Kb(string key) => memory.TryGetValue(key, out var value) ? value : null;
        var total = Kb("MemTotal");
        if (total is { } t)
            _memTotal = t * 1024;

        var limit = ProcFiles.CgroupLimit(ReadFile("sys/fs/cgroup/memory.max"))
                    ?? ProcFiles.CgroupLimit(ReadFile("sys/fs/cgroup/memory/memory.limit_in_bytes"));

        return new HostSignals(
            now,
            load?.One, load?.Five, load?.Fifteen,
            cpus, from,
            total, Kb("MemAvailable"), Kb("SwapTotal"), Kb("SwapFree"),
            ProcFiles.MeminfoIsContainers(total, limit),
            shares?.Iowait, shares?.Busy,
            ProcFiles.Pressure(ReadFile("proc/pressure/cpu")),
            ProcFiles.Pressure(ReadFile("proc/pressure/memory")),
            ProcFiles.Pressure(ReadFile("proc/pressure/io")));
    }

    private (int Count, string From)? _cpus;

    /// <summary>The host's CPUs, and which file said so. Read once: CPUs do not come and go on a NAS.</summary>
    public (int Count, string From) CpuCount() => _cpus ??= CountCpus();

    private (int Count, string From) CountCpus()
    {
        var listed = ProcFiles.CpuInfoCount(ReadFile("proc/cpuinfo"));
        if (listed > 0)
            return (listed, "/proc/cpuinfo");
        var online = ProcFiles.OnlineCount(ReadFile("sys/devices/system/cpu/online"));
        if (online > 0)
            return (online, "/sys/devices/system/cpu/online");
        return (Environment.ProcessorCount, "the runtime (may be this container's share)");
    }

    /// <summary>The host's memory in bytes, read once and remembered; 0 when there is no /proc/meminfo.</summary>
    public ulong MemTotalBytes()
    {
        if (_memTotal is { } known)
            return known;
        var total = ProcFiles.MemInfo(ReadFile("proc/meminfo")).TryGetValue("MemTotal", out var kb) ? kb * 1024 : 0;
        _memTotal = total;
        return total;
    }
}
