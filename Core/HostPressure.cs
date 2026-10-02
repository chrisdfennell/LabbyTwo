using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>How hard the machine LabbyTwo runs on is working, in four words.</summary>
public enum PressureLevel
{
    /// <summary>Nothing to say.</summary>
    Ok,

    /// <summary>Working hard, coping. Shown, never notified.</summary>
    Busy,

    /// <summary>Falling behind: things are waiting on the CPU or the disks for minutes on end. Notified, once.</summary>
    Strained,

    /// <summary>The state the NAS was in before it stopped answering — shares dropping, the web UI timing out.</summary>
    Critical,
}

/// <summary>
/// One Linux pressure-stall line: the share of recent time in which some (or all) runnable
/// tasks were stalled waiting for this resource. "some avg60=40" means that for 40% of the
/// last minute at least one task could have run but was waiting — for the disk, for memory,
/// for a CPU. It is the most direct answer the kernel gives to "is this machine keeping up",
/// which is why it is preferred to the load average when it is there.
/// </summary>
public sealed record Psi(double SomeAvg10, double SomeAvg60, double SomeAvg300, double? FullAvg10 = null, double? FullAvg60 = null);

/// <summary>
/// What /proc says about the host, read in one go. Every field is nullable or defaulted
/// because every file is optional: PSI needs a 4.20+ kernel built with it (QTS 5 has it;
/// older firmware does not), and Windows, where LabbyTwo is developed, has none of them.
/// </summary>
/// <param name="CpuCount">CPUs on the host. From /proc/cpuinfo, which in a container still lists the host's, rather than
/// <see cref="Environment.ProcessorCount"/>, which a CPU limit on the container shrinks — and load is the host's, so it
/// must be divided by the host's CPUs.</param>
/// <param name="CpuCountFrom">Where <paramref name="CpuCount"/> came from, for the health page.</param>
/// <param name="MemoryIsContainers">True when /proc/meminfo reflects LabbyTwo's own container (LXCFS) rather than the host —
/// detected by MemTotal matching the container's cgroup memory limit. The memory figures then describe the container, and
/// the health page says so.</param>
/// <param name="IowaitPercent">Share of CPU time spent idle with disk I/O outstanding, since the previous read.</param>
/// <param name="CpuBusyPercent">Share of CPU time spent doing anything but idling or waiting, since the previous read.</param>
public sealed record HostSignals(
    DateTimeOffset At,
    double? Load1,
    double? Load5,
    double? Load15,
    int CpuCount,
    string CpuCountFrom,
    ulong? MemTotalKb,
    ulong? MemAvailableKb,
    ulong? SwapTotalKb,
    ulong? SwapFreeKb,
    bool MemoryIsContainers,
    double? IowaitPercent,
    double? CpuBusyPercent,
    Psi? Cpu,
    Psi? Memory,
    Psi? Io)
{
    /// <summary>Whether there was anything to read at all — false off Linux.</summary>
    public bool Available => Load1 is not null || MemTotalKb is not null;

    public double? LoadPerCore => Load1 is { } load && CpuCount > 0 ? load / CpuCount : null;

    public double? MemAvailablePercent => MemTotalKb is > 0 && MemAvailableKb is { } available
        ? available * 100.0 / MemTotalKb.Value
        : null;

    public ulong? SwapUsedKb => SwapTotalKb is { } total && SwapFreeKb is { } free && total >= free ? total - free : null;

    public double? SwapUsedPercent => SwapTotalKb is > 0 && SwapUsedKb is { } used ? used * 100.0 / SwapTotalKb.Value : null;

    public static HostSignals None(DateTimeOffset at) =>
        new(at, null, null, null, 0, "", null, null, null, null, false, null, null, null, null, null);
}

/// <summary>
/// The parsing of each /proc file, apart from reading it, so each is a test with a sample
/// file. Lenient by design: a line this does not understand is skipped, never thrown on —
/// a kernel adding a field must not blind the warning.
/// </summary>
public static class ProcFiles
{
    /// <summary><c>/proc/loadavg</c>: "13.40 12.10 9.80 5/1234 56789".</summary>
    public static (double One, double Five, double Fifteen)? LoadAvg(string? text)
    {
        var parts = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !Number(parts[0], out var one) || !Number(parts[1], out var five) || !Number(parts[2], out var fifteen))
            return null;
        return (one, five, fifteen);
    }

    /// <summary><c>/proc/meminfo</c>: "MemAvailable:   1234567 kB" per line, into kilobytes by name.</summary>
    public static IReadOnlyDictionary<string, ulong> MemInfo(string? text)
    {
        var values = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var line in Lines(text))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var rest = line[(colon + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length > 0 && ulong.TryParse(rest[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                values[line[..colon].Trim()] = value;
        }
        return values;
    }

    /// <summary>The aggregate "cpu" line of <c>/proc/stat</c>, in clock ticks.</summary>
    /// <param name="Total">user + nice + system + idle + iowait + irq + softirq + steal. Guest time is already inside user.</param>
    public sealed record CpuTimes(ulong Total, ulong Idle, ulong Iowait);

    public static CpuTimes? Stat(string? text)
    {
        foreach (var line in Lines(text))
        {
            if (!line.StartsWith("cpu ", StringComparison.Ordinal))
                continue;
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                .Select(f => ulong.TryParse(f, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0UL)
                .ToArray();
            if (fields.Length < 4)
                return null;
            ulong total = 0;
            for (var i = 0; i < Math.Min(8, fields.Length); i++)
                total += fields[i];
            return new CpuTimes(total, fields[3], fields.Length > 4 ? fields[4] : 0);
        }
        return null;
    }

    /// <summary>
    /// iowait and busy as shares of the time between two reads of /proc/stat. Null when there
    /// is no earlier read, or the counters went backwards (they do not, short of a reboot,
    /// but a container restored from a checkpoint can see it).
    /// </summary>
    public static (double Iowait, double Busy)? CpuShares(CpuTimes? before, CpuTimes? now)
    {
        if (before is null || now is null || now.Total <= before.Total || now.Idle < before.Idle || now.Iowait < before.Iowait)
            return null;
        var total = (double)(now.Total - before.Total);
        var idle = (double)(now.Idle - before.Idle);
        var iowait = (double)(now.Iowait - before.Iowait);
        return (iowait * 100 / total, Math.Max(0, (total - idle - iowait) * 100 / total));
    }

    /// <summary>One file of <c>/proc/pressure</c>: a "some" line and, except for cpu on older kernels, a "full" one.</summary>
    public static Psi? Pressure(string? text)
    {
        double? some10 = null, some60 = null, some300 = null, full10 = null, full60 = null;
        foreach (var line in Lines(text))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            var values = parts.Skip(1)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => Number(p[1], out var v) ? v : (double?)null);
            if (parts[0] == "some")
            {
                some10 = values.GetValueOrDefault("avg10");
                some60 = values.GetValueOrDefault("avg60");
                some300 = values.GetValueOrDefault("avg300");
            }
            else if (parts[0] == "full")
            {
                full10 = values.GetValueOrDefault("avg10");
                full60 = values.GetValueOrDefault("avg60");
            }
        }
        return some10 is null || some60 is null ? null : new Psi(some10.Value, some60.Value, some300 ?? some60.Value, full10, full60);
    }

    /// <summary>Processors listed in <c>/proc/cpuinfo</c> — one "processor : N" line each, on x86 and ARM alike.</summary>
    public static int CpuInfoCount(string? text) =>
        Lines(text).Count(l => l.StartsWith("processor", StringComparison.Ordinal) && l.Contains(':'));

    /// <summary><c>/sys/devices/system/cpu/online</c>: "0-3", "0,2-5" → how many.</summary>
    public static int OnlineCount(string? text)
    {
        var count = 0;
        foreach (var range in (text ?? "").Trim().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var ends = range.Split('-');
            if (ends.Length == 1 && int.TryParse(ends[0], out _))
                count++;
            else if (ends.Length == 2 && int.TryParse(ends[0], out var from) && int.TryParse(ends[1], out var to) && to >= from)
                count += to - from + 1;
        }
        return count;
    }

    /// <summary>
    /// A cgroup memory limit in bytes: <c>memory.max</c> (v2) or <c>memory.limit_in_bytes</c>
    /// (v1). Null for "max", for v1's "no limit" (a number near 2^63), and for anything unreadable.
    /// </summary>
    public static ulong? CgroupLimit(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0 || value == "max" || !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
            return null;
        return bytes >= (ulong)long.MaxValue / 2 ? null : bytes;
    }

    /// <summary>
    /// Whether /proc/meminfo is describing the container rather than the host. Plain Docker
    /// shows the host's meminfo inside every container; LXCFS (used by some NAS container
    /// stations) replaces it with the container's own. The giveaway is MemTotal equalling the
    /// container's memory limit, to within a page or two.
    /// </summary>
    public static bool MeminfoIsContainers(ulong? memTotalKb, ulong? cgroupLimitBytes) =>
        memTotalKb is > 0 && cgroupLimitBytes is > 0 &&
        Math.Abs((double)memTotalKb.Value * 1024 - cgroupLimitBytes.Value) <= cgroupLimitBytes.Value * 0.01;

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    private static bool Number(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

/// <summary>
/// Turns readings into a <see cref="PressureLevel"/> that does not flap, and decides when to
/// say so. Pure — the clock is always a parameter — so the sustain times and the hysteresis
/// are tests rather than waiting.
///
/// <para><b>The thresholds.</b> Every comparison is strict, as alert rules' are. A level is
/// reached by any one of its signals:</para>
/// <list type="table">
/// <item><term>Busy</term><description>load above 1 per core; PSI io some avg60 above 20%; PSI cpu some avg60 above 50%;
/// iowait above 15%; less than 10% of memory available; QTS reporting CPU above 90%.</description></item>
/// <item><term>Strained</term><description>load above 2 per core; PSI io some avg60 above 40%; PSI memory some avg60 above
/// 10%; iowait above 30%; swap growing by more than 50 MB in five minutes while under 20% of memory is free.</description></item>
/// <item><term>Critical</term><description>load above 4 per core; PSI io some avg60 above 60%; PSI memory some avg60 above
/// 30%; swap growing while under 5% of memory is free.</description></item>
/// </list>
/// <para>The afternoon this was written for — load 13 on 4 cores, a transcoder reading
/// 12.5 TB — is 3.3 per core: Strained after five minutes, well before Critical's 4.</para>
///
/// <para><b>Sustain and hysteresis.</b> A level is entered only after its signals have held
/// for <see cref="SustainFor"/> — a backup's first minute is not a crisis — and left only
/// after every signal has been below three quarters of its threshold for <see cref="ClearAfter"/>,
/// so load hovering around 2.0 per core does not toggle Strained on and off.</para>
///
/// <para><b>Notifying once.</b> One notice when an episode reaches Strained, however high it
/// climbs or however long it lasts, and one when it is back to Busy or better — the
/// once-and-once of <see cref="SelfWatchLedger"/>. If the start was held (maintenance, quiet
/// hours) and the episode ended before the hold lifted, neither is sent.</para>
/// </summary>
public sealed class PressureTracker
{
    public static readonly TimeSpan ClearAfter = TimeSpan.FromMinutes(5);

    /// <summary>How far back the swap growth is measured.</summary>
    public static readonly TimeSpan SwapWindow = TimeSpan.FromMinutes(5);

    /// <summary>Swap growth that counts as "filling": 50 MB in five minutes.</summary>
    public const double SwapFillingKb = 50 * 1024;

    /// <summary>Of each threshold, how far a signal must fall before a level is left.</summary>
    public const double ClearFraction = 0.75;

    public static TimeSpan SustainFor(PressureLevel level) => level switch
    {
        PressureLevel.Busy => TimeSpan.FromMinutes(2),
        PressureLevel.Strained => TimeSpan.FromMinutes(5),
        PressureLevel.Critical => TimeSpan.FromMinutes(2),
        _ => TimeSpan.Zero,
    };

    private readonly Dictionary<PressureLevel, DateTimeOffset> _since = [];
    private readonly Queue<(DateTimeOffset At, ulong UsedKb)> _swap = new();
    private DateTimeOffset? _belowSince;

    public PressureLevel Level { get; private set; }

    /// <summary>What the signals say this instant, before sustain and hysteresis.</summary>
    public PressureLevel Raw { get; private set; }

    /// <summary>When <see cref="Level"/> last changed.</summary>
    public DateTimeOffset? Since { get; private set; }

    /// <summary>The reasons for the current raw level, in words: "load 13.4 on 4 cores".</summary>
    public IReadOnlyList<string> Reasons { get; private set; } = [];

    /// <summary>Swap growth over <see cref="SwapWindow"/>, in kilobytes; null until there is that much history.</summary>
    public double? SwapGrowthKb { get; private set; }

    /// <summary>An episode reached Strained and its notice has not gone out yet.</summary>
    public bool StartOwed { get; private set; }

    /// <summary>The episode's start notice went out, so its end will be announced too.</summary>
    public bool StartDelivered { get; private set; }

    /// <summary>The episode is over and its recovery notice has not gone out yet.</summary>
    public bool ClearOwed { get; private set; }

    /// <summary>The worst level the current episode has reached, for the recovery notice.</summary>
    public PressureLevel Worst { get; private set; }

    public bool InEpisode { get; private set; }

    /// <param name="qnapCpu">QTS's own CPU figure for this NAS, when there is a QNAP connection: the one number it can
    /// add that /proc inside a container cannot — a CPU limit on LabbyTwo's container does not hide it.</param>
    public PressureLevel Step(DateTimeOffset now, HostSignals signals, double? qnapCpu = null)
    {
        NoteSwap(now, signals.SwapUsedKb);
        var (raw, reasons) = Judge(signals, SwapGrowthKb, qnapCpu, 1.0);
        var (clear, _) = Judge(signals, SwapGrowthKb, qnapCpu, ClearFraction);
        Raw = raw;
        Reasons = reasons;

        foreach (var level in new[] { PressureLevel.Busy, PressureLevel.Strained, PressureLevel.Critical })
        {
            if (raw >= level)
                _since.TryAdd(level, now);
            else
                _since.Remove(level);
        }

        var reached = PressureLevel.Ok;
        foreach (var (level, since) in _since)
            if (now - since >= SustainFor(level) && level > reached)
                reached = level;

        if (reached > Level)
        {
            Move(reached, now);
            _belowSince = null;
        }
        else if (clear < Level)
        {
            _belowSince ??= now;
            if (now - _belowSince.Value >= ClearAfter)
            {
                Move(clear, now);
                _belowSince = null;
            }
        }
        else
        {
            _belowSince = null;
        }

        return Level;
    }

    private void Move(PressureLevel level, DateTimeOffset now)
    {
        Level = level;
        Since = now;

        if (level >= PressureLevel.Strained)
        {
            if (!InEpisode)
            {
                InEpisode = true;
                StartOwed = true;
                ClearOwed = false;
                Worst = level;
            }
            else if (level > Worst)
            {
                Worst = level;
            }
        }
        else if (InEpisode)
        {
            InEpisode = false;
            ClearOwed = StartDelivered;
            StartOwed = false;
            StartDelivered = false;
        }
    }

    /// <summary>The start notice went out (or will never be wanted).</summary>
    public void StartSent()
    {
        StartOwed = false;
        StartDelivered = true;
    }

    public void ClearSent() => ClearOwed = false;

    private void NoteSwap(DateTimeOffset now, ulong? usedKb)
    {
        if (usedKb is not { } used)
        {
            SwapGrowthKb = null;
            return;
        }
        _swap.Enqueue((now, used));
        while (_swap.Count > 0 && now - _swap.Peek().At > SwapWindow + TimeSpan.FromMinutes(1))
            _swap.Dequeue();

        // The oldest sample at least nearly a window old; until there is one, no verdict.
        var old = _swap.FirstOrDefault(s => now - s.At >= SwapWindow - TimeSpan.FromSeconds(30));
        SwapGrowthKb = old.At == default ? null : (double)used - old.UsedKb;
    }

    /// <summary>
    /// The level the signals point to, and why, with every threshold scaled by
    /// <paramref name="scale"/> — 1 to enter, <see cref="ClearFraction"/> to judge leaving.
    /// </summary>
    public static (PressureLevel Level, IReadOnlyList<string> Reasons) Judge(
        HostSignals s, double? swapGrowthKb, double? qnapCpu, double scale)
    {
        var level = PressureLevel.Ok;
        var reasons = new List<string>();

        void Hit(PressureLevel at, string why)
        {
            if (at > level)
                level = at;
            if (!reasons.Contains(why))
                reasons.Add(why);
        }

        if (s.LoadPerCore is { } perCore)
        {
            // One decimal whatever its size: "load 13.4", as uptime and top print it.
            var why = $"load {s.Load1!.Value.ToString("0.#", CultureInfo.InvariantCulture)} on {s.CpuCount} core{(s.CpuCount == 1 ? "" : "s")}";
            if (perCore > 4 * scale) Hit(PressureLevel.Critical, why);
            else if (perCore > 2 * scale) Hit(PressureLevel.Strained, why);
            else if (perCore > 1 * scale) Hit(PressureLevel.Busy, why);
        }

        if (s.Io is { } io)
        {
            var why = $"tasks stalled on the disks {Fmt(io.SomeAvg60)}% of the last minute";
            if (io.SomeAvg60 > 60 * scale) Hit(PressureLevel.Critical, why);
            else if (io.SomeAvg60 > 40 * scale) Hit(PressureLevel.Strained, why);
            else if (io.SomeAvg60 > 20 * scale) Hit(PressureLevel.Busy, why);
        }

        if (s.Memory is { } memory)
        {
            var why = $"tasks stalled on memory {Fmt(memory.SomeAvg60)}% of the last minute";
            if (memory.SomeAvg60 > 30 * scale) Hit(PressureLevel.Critical, why);
            else if (memory.SomeAvg60 > 10 * scale) Hit(PressureLevel.Strained, why);
        }

        if (s.Cpu is { } cpu && cpu.SomeAvg60 > 50 * scale)
            Hit(PressureLevel.Busy, $"tasks waiting for a CPU {Fmt(cpu.SomeAvg60)}% of the last minute");

        if (s.IowaitPercent is { } iowait)
        {
            var why = $"CPUs waiting on the disks {Fmt(iowait)}% of the time";
            if (iowait > 30 * scale) Hit(PressureLevel.Strained, why);
            else if (iowait > 15 * scale) Hit(PressureLevel.Busy, why);
        }

        var free = s.MemAvailablePercent;
        var filling = swapGrowthKb is { } growth && growth > SwapFillingKb * scale;
        if (filling)
        {
            var why = $"swap filling (+{Fmt(swapGrowthKb!.Value / 1024)} MB in 5 min)";
            if (free is { } f1 && f1 < 5 / scale) Hit(PressureLevel.Critical, why);
            else if (free is { } f2 && f2 < 20 / scale) Hit(PressureLevel.Strained, why);
        }
        if (free is { } available && available < 10 / scale)
            Hit(PressureLevel.Busy, $"only {Fmt(available)}% of memory free");

        if (qnapCpu is { } q && q > 90 * scale)
            Hit(PressureLevel.Busy, $"QTS reports CPU at {Fmt(q)}%");

        return (level, reasons);
    }

    private static string Fmt(double value) =>
        value.ToString(value >= 10 ? "0" : "0.#", CultureInfo.InvariantCulture);

    /// <summary>"Busy", "Strained" — the level as the page and the phone say it.</summary>
    public static string Word(PressureLevel level) => level switch
    {
        PressureLevel.Busy => "Busy",
        PressureLevel.Strained => "Strained",
        PressureLevel.Critical => "Critical",
        _ => "OK",
    };

    /// <summary>
    /// The notice: "The NAS is struggling: load 13.4 on 4 cores, swap filling. Busiest: tdarr
    /// 193% CPU, 40 MB/s read; plex 40% CPU." Reasons first, worst three only, then the three
    /// busiest containers — the line somebody needs to decide what to stop.
    /// </summary>
    public static string Sentence(string host, IReadOnlyList<string> reasons, IReadOnlyList<string> busiest)
    {
        var why = reasons.Count == 0 ? "it has been working flat out for several minutes" : string.Join(", ", reasons.Take(3));
        var text = $"The {host} is struggling: {why}.";
        if (busiest.Count > 0)
            text += $" Busiest: {string.Join("; ", busiest.Take(3))}.";
        return text;
    }
}
