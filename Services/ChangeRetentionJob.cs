using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Keeps the change feed to <see cref="LabbyOptions.ChangeRetention"/> and incidents to
/// <see cref="LabbyOptions.IncidentRetention"/>. A background job beside the samples rollup,
/// for the same reason that one is: the monitor's sweep waits for anything it does, and
/// tidying up is nobody's reason to be late.
///
/// Every few hours rather than daily, so that on the first run after a long time off the
/// work is a few thousand rows, not months of them in one go — though with an index on the
/// time, in batches, it is small either way.
/// </summary>
public sealed class ChangeRetentionJob(ChangeStore changes, IncidentStore incidents, ILogger<ChangeRetentionJob> log) : IBackgroundJob
{
    public string Name => "change-retention";

    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task RunAsync(CancellationToken ct)
    {
        var dropped = await changes.PruneAsync(ct);
        var closed = await incidents.PruneAsync(ct);
        if (dropped + closed > 0)
            log.LogInformation("Removed {Changes} old change(s) and {Incidents} old incident(s)", dropped, closed);
    }
}
