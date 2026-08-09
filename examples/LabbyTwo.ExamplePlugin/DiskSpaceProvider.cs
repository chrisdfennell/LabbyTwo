using System.Diagnostics;
using LabbyTwo.Core;

namespace LabbyTwo.ExamplePlugin;

/// <summary>
/// A complete, useful provider in one file: how full a path on the LabbyTwo host is.
/// Handy for watching the data volume itself, and short enough to read in one sitting if
/// you are about to write your own.
///
/// Nothing registers this. Dropping the built DLL into the plugins folder is the whole
/// installation, because the host scans for classes implementing this interface.
/// </summary>
public sealed class DiskSpaceProvider : IConnectionProvider
{
    public string Type => "example-disk";
    public string DisplayName => "Disk space (example plugin)";
    public string Icon => "💽";
    public string Category => "System";
    public string Description => "Free space on a path the LabbyTwo container can see. An example of what a plugin looks like.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("path", "Path", FieldKind.Text, "/app/data", Required: true,
            Help: "As the LabbyTwo process sees it — inside the container, not on the host."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("disk_percent", "Used", "%", 1),
        new("free_gb", "Free", " GB", 1),
        new("total_gb", "Capacity", " GB", 1),
    ];

    public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var path = connection.Settings.Get("path");
        if (path.Length == 0)
            return Task.FromResult(ProbeResult.Down(TimeSpan.Zero, "No path configured."));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var drive = new DriveInfo(Path.GetFullPath(path));
            if (!drive.IsReady)
                return Task.FromResult(ProbeResult.Down(stopwatch.Elapsed, $"{path} is not mounted."));

            const double gb = 1024d * 1024 * 1024;
            var total = drive.TotalSize / gb;
            var free = drive.AvailableFreeSpace / gb;
            var used = total > 0 ? (total - free) / total * 100 : 0;
            stopwatch.Stop();

            var metrics = new Dictionary<string, double>
            {
                ["disk_percent"] = used,
                ["free_gb"] = free,
                ["total_gb"] = total,
            };

            // Up means reachable, nothing more. "Nearly full" is not a probe failure, and
            // reporting it as one to borrow the alerting would make every tile and uptime
            // figure lie. Point an alert rule at disk_percent instead — Settings → Alerts.
            return Task.FromResult(ProbeResult.Up(
                stopwatch.Elapsed,
                $"{free:0.#} GB free of {total:0.#} GB ({used:0.#}% used)",
                metrics));
        }
        catch (Exception ex)
        {
            // A probe must never throw. The message here is what the user sees on the
            // tile and in the alert, so it should be the thing they need to act on.
            stopwatch.Stop();
            return Task.FromResult(ProbeResult.Down(stopwatch.Elapsed, ex.GetBaseException().Message));
        }
    }
}
