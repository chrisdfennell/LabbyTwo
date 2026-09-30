using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Once an hour: notes the database's size from its header (for growth per day), and every
/// twelve hours surveys what takes the space (see <see cref="StorageManager"/>). The survey
/// is a few index seeks per series, a series at a time with pauses between — a background
/// read the disk barely notices — and nothing else in LabbyTwo ever waits for it.
///
/// Not at startup: the monitor's restore and first sweeps want the disk more, and the last
/// survey is kept across the restart anyway.
/// </summary>
public sealed class StorageSurveyJob(StorageManager storage, HistoryStore history) : IBackgroundJob
{
    public string Name => "storage-survey";

    public TimeSpan Interval => TimeSpan.FromHours(1);

    public async Task RunAsync(CancellationToken ct)
    {
        // A compaction holds the database; its own figures are what the page shows meanwhile.
        if (history.Writes.Compacting)
            return;

        var now = DateTimeOffset.Now;
        await storage.RecordSizeAsync(await history.FactsAsync(ct), now, ct);
        if (storage.SurveyDue(now))
            await storage.SurveyNowAsync(ct);
    }
}
