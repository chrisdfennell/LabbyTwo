using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// The parts of a checklist that need the Markdown it is written in as well as its ticks:
/// moving ticks along after an edit, and resetting the whole list with a line in the change
/// feed. Ticking a single box needs neither and goes straight to <see cref="ChecklistStore"/>.
///
/// The same for every owner — a note, a dashboard's Markdown card, a custom page's Markdown
/// block — since all that differs between them is the key the ticks live under
/// (<see cref="ChecklistOwners"/>) and the name a person knows the thing by.
/// </summary>
public sealed class MarkdownChecklists(ChecklistStore store, Markdown markdown, ChangeStore changes, ILogger<MarkdownChecklists> log)
{
    /// <summary>
    /// After the owner's text changed — an edit, a restored version — keeps each tick on the
    /// item it was on (see <see cref="Checklists.Reconcile"/>). Never throws: losing track
    /// of a tick is not worth failing the save that has already happened.
    /// </summary>
    /// <param name="owner">The owner key, from <see cref="ChecklistOwners"/>.</param>
    public async Task ReconcileAsync(string owner, string content, CancellationToken ct = default)
    {
        try
        {
            await store.ReconcileAsync(owner, markdown.ChecklistItems(content), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not carry checklist ticks over to new text");
        }
    }

    /// <summary>
    /// Unticks every item the owner has, for everybody, and says so in What changed — so
    /// "who cleared the restart checklist half way through?" has an answer. Nothing is
    /// recorded when there was nothing to clear. How many ticks went.
    /// </summary>
    /// <param name="owner">The owner key, from <see cref="ChecklistOwners"/>.</param>
    /// <param name="name">What a person calls it — the note's or the card's title.</param>
    public async Task<int> ResetAsync(string owner, string name, string by, CancellationToken ct = default)
    {
        var cleared = await store.ResetAsync(owner, ct);
        if (cleared == 0)
            return 0;
        try
        {
            var shown = name.Length > 0 ? name : "Untitled";
            var who = by.Length > 0 ? $" by {by}" : "";
            await changes.RecordAsync(new Change(DateTimeOffset.Now, ChangeKinds.Checklist, ChangeActions.Reset, null, owner,
                $"Checklist in “{shown}” reset", $"{cleared} tick{(cleared == 1 ? "" : "s")} cleared{who}"), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Reset a checklist but could not record it in the change feed");
        }
        return cleared;
    }
}
