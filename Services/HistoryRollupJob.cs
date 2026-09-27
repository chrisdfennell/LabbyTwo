using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Folds raw samples older than the raw retention into hourly summaries, then expires
/// summaries older than theirs. This is what keeps the samples table at a week of rows
/// rather than a month, and what lets a chart look back a year.
///
/// A background job rather than a step in the monitor's sweep, which is where pruning
/// used to happen: the sweep waits for it, and the first rollup on an install that kept a
/// month of raw samples is minutes of work. Here it delays nothing but itself.
/// </summary>
public sealed class HistoryRollupJob(HistoryStore history, ILogger<HistoryRollupJob> log) : IBackgroundJob
{
    /// <summary>
    /// How long one run may keep going. Past it the run stops where it is — every batch is
    /// complete in itself — and the next run carries on. Well inside the interval, so a
    /// long catch-up never reads as a job stuck running on the health page.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    public string Name => "history-rollup";

    /// <summary>
    /// Every quarter of an hour. An hour becomes due for summarising once an hour, so in
    /// the steady state most runs find one hour per series to fold; the shorter interval is
    /// for the first catch-up after an upgrade, which then takes a few runs rather than a
    /// few hours.
    /// </summary>
    public TimeSpan Interval => TimeSpan.FromMinutes(15);

    public async Task RunAsync(CancellationToken ct)
    {
        var result = await history.RollupAsync(Budget, ct);
        await history.PruneAsync(ct);

        if (result.Batches > 0)
        {
            log.LogInformation(
                "Summarised old samples into {Hours} hourly row(s) and removed {Rows} raw row(s) in {Batches} batch(es){More}",
                result.HoursWritten, result.RowsDeleted, result.Batches,
                result.Finished ? "" : "; more remain for the next run");
        }
    }
}
