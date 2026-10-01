using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Keeps notes' and Markdown cards' version history to <see cref="NotesStore.KeepFor"/>,
/// and lets a deleted note's or card's history go <see cref="NotesStore.DeletedFor"/> after
/// it was deleted. The per-note and per-card caps need no job — every save trims its own —
/// so this only has the slow edges to deal with, and every few hours is plenty. In batches,
/// so a first run after months off never holds the write lock for long.
/// </summary>
public sealed class NoteHistoryRetentionJob(NotesStore notes, WidgetHistoryStore cards, ILogger<NoteHistoryRetentionJob> log) : IBackgroundJob
{
    public string Name => "note-history-retention";

    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task RunAsync(CancellationToken ct)
    {
        var dropped = await notes.PruneAsync(ct);
        if (dropped > 0)
            log.LogInformation("Removed {Versions} old note version(s)", dropped);
        var droppedCards = await cards.PruneAsync(ct);
        if (droppedCards > 0)
            log.LogInformation("Removed {Versions} old Markdown card version(s)", droppedCards);
    }
}
