using System.Diagnostics;
using LabbyTwo.Core;
using LabbyTwo.Services;

namespace LabbyTwo.Providers;

/// <summary>
/// "This host": the machine LabbyTwo runs on, as a connection — so its load, memory, swap,
/// iowait and pressure-stall figures are chartable, alertable and usable in <c>{{metric}}</c>
/// like anything else.
///
/// <para>Why a connection rather than more of LabbyTwo's own health page. The early warning
/// (<see cref="HostPressureWatch"/>) runs whether or not this exists, from memory, and needs
/// no history. But "what was the load at 3 pm when the shares dropped" is a chart, and charts,
/// rules, the phone's pins and the Home Assistant bridge all already work on a connection's
/// metrics. Making the host one more connection gets all of that with nothing new to build,
/// and leaves the choice of keeping a history — a few rows a minute — to the person who
/// adds it.</para>
///
/// <para>A probe costs nothing: it hands over the watch's last reading, which is at most
/// thirty seconds old, and only reads /proc itself if the watch is not running.</para>
/// </summary>
public sealed class HostProvider : IConnectionProvider
{
    public string Type => "host";
    public string DisplayName => "This host";
    public string Icon => "🖥️";
    public string Category => "Infrastructure";
    public string Description => "The machine LabbyTwo runs on: load per core, memory, swap, disk wait and Linux pressure-stall figures, read from /proc. Needs LabbyTwo on Linux; nothing to set up.";

    public IReadOnlyList<FieldSpec> Fields => [];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("load_1m", "Load (1 min)", "", 2),
        new("load_per_core", "Load per core", "", 2),
        new("cpu_count", "CPUs"),
        new("iowait_percent", "Waiting on disks", "%", 1),
        new("cpu_busy_percent", "CPU busy", "%", 1),
        new("mem_available_percent", "Memory available", "%", 1),
        new("swap_used_percent", "Swap used", "%", 1),
        new("psi_cpu_avg60", "Stalled on CPU (1 min)", "%", 1),
        new("psi_memory_avg60", "Stalled on memory (1 min)", "%", 1),
        new("psi_io_avg60", "Stalled on disks (1 min)", "%", 1),
        new("psi_io_avg10", "Stalled on disks (10 s)", "%", 1),
        new("pressure_level", "Pressure (0 OK – 3 critical)"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Overloaded", "load_per_core", Comparison.Above, 2, ClearThreshold: 1.5, ForMinutes: 5,
            Why: "More than two tasks waiting per CPU for five minutes. The NAS that prompted this sat at 3.3 per core while its shares dropped."),
        new("Disks can't keep up", "psi_io_avg60", Comparison.Above, 40, ClearThreshold: 25, ForMinutes: 5,
            Why: "Tasks stalled on disk I/O for more than 40% of each minute, for five minutes."),
    ];

    public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var now = DateTimeOffset.Now;
        var report = HostPressureWatch.Current;
        var signals = report is not null && now - report.At < TimeSpan.FromSeconds(90)
            ? report.Signals
            : HostProc.Default.Read(now);
        stopwatch.Stop();

        if (!signals.Available)
            return Task.FromResult(ProbeResult.Down(stopwatch.Elapsed,
                "There is no /proc here to read — LabbyTwo is not running on Linux."));

        var metrics = ToMetrics(signals);
        if (report is not null)
            metrics["pressure_level"] = report.LevelNumber;

        var message = report is { } r ? $"{PressureTracker.Word(r.Level)}" : "Read";
        if (signals.Load1 is { } load)
            message += $" · load {load:0.##} on {signals.CpuCount} CPU{(signals.CpuCount == 1 ? "" : "s")}";
        return Task.FromResult(ProbeResult.Up(stopwatch.Elapsed, message, metrics));
    }

    /// <summary>The signals as metrics, leaving out whatever this kernel does not report.</summary>
    public static Dictionary<string, double> ToMetrics(HostSignals s)
    {
        var metrics = new Dictionary<string, double>();
        void Put(string key, double? value)
        {
            if (value is { } v && !double.IsNaN(v))
                metrics[key] = Math.Round(v, 2);
        }

        Put("load_1m", s.Load1);
        Put("load_per_core", s.LoadPerCore);
        Put("cpu_count", s.CpuCount > 0 ? s.CpuCount : null);
        Put("iowait_percent", s.IowaitPercent);
        Put("cpu_busy_percent", s.CpuBusyPercent);
        Put("mem_available_percent", s.MemAvailablePercent);
        Put("swap_used_percent", s.SwapUsedPercent);
        Put("psi_cpu_avg60", s.Cpu?.SomeAvg60);
        Put("psi_memory_avg60", s.Memory?.SomeAvg60);
        Put("psi_io_avg60", s.Io?.SomeAvg60);
        Put("psi_io_avg10", s.Io?.SomeAvg10);
        return metrics;
    }
}
