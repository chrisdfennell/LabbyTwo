using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// The parts of a note's checklist that need the note's text as well as its ticks: moving
/// ticks along after an edit, and resetting the whole list with a line in the change feed.
/// Ticking a single box needs neither and goes straight to <see cref="ChecklistStore"/>.
/// </summary>
public sealed class NoteChecklists(ChecklistStore store, Markdown markdown, ChangeStore changes, ILogger<NoteChecklists> log)
{
    /// <summary>
    /// After a note's text changed — an edit, a restored version — keeps each tick on the
    /// item it was on (see <see cref="Checklists.Reconcile"/>). Never throws: losing track
    /// of a tick is not worth failing the save that has already happened.
    /// </summary>
    public async Task ReconcileAsync(string noteId, string content, CancellationToken ct = default)
    {
        try
        {
            await store.ReconcileAsync(noteId, markdown.ChecklistItems(content), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not carry a note's checklist ticks over to its new text");
        }
    }

    /// <summary>
    /// Unticks every item in the note, for everybody, and says so in What changed — so
    /// "who cleared the restart checklist half way through?" has an answer. Nothing is
    /// recorded when there was nothing to clear. How many ticks went.
    /// </summary>
    public async Task<int> ResetAsync(string noteId, string title, string by, CancellationToken ct = default)
    {
        var cleared = await store.ResetAsync(noteId, ct);
        if (cleared == 0)
            return 0;
        try
        {
            var name = title.Length > 0 ? title : "Untitled";
            var who = by.Length > 0 ? $" by {by}" : "";
            await changes.RecordAsync(new Change(DateTimeOffset.Now, ChangeKinds.Checklist, ChangeActions.Reset, null, noteId,
                $"Checklist in “{name}” reset", $"{cleared} tick{(cleared == 1 ? "" : "s")} cleared{who}"), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Reset a checklist but could not record it in the change feed");
        }
        return cleared;
    }
}
