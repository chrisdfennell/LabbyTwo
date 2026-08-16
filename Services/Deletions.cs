using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Deleting a tab, a card or a connection, with the way back captured first.
///
/// One place rather than three, because the interesting part of each of these is not the
/// delete — it is knowing what else the delete takes with it. A tab takes its cards and
/// its notes; a connection takes its history and cuts every card that was bound to it
/// loose. Written at each call site, those details would be got right once and then drift
/// the first time somebody changes what a delete cascades to.
/// </summary>
public sealed class Deletions(ConfigStore config, NotesStore notes, UndoService undo)
{
    public async Task WidgetAsync(Widget widget, CancellationToken ct = default)
    {
        await config.DeleteWidgetAsync(widget.Id, ct);

        // A card is only its own row, so putting it back is the row going back. Sort comes
        // with it, which is what lands it in the slot it came out of rather than at the end.
        undo.Add(Describe("card", widget.Title),
            async token => await config.SaveWidgetAsync(widget, token));
    }

    public async Task TabAsync(Tab tab, CancellationToken ct = default)
    {
        var widgets = await config.WidgetsForTabAsync(tab.Id, ct);
        var written = await notes.ForTabAsync(tab.Id, ct);

        await config.DeleteTabAsync(tab.Id, ct);

        undo.Add(Describe("tab", tab.Name), async token =>
        {
            await config.SaveTabAsync(tab, token);

            foreach (var widget in widgets)
                await config.SaveWidgetAsync(widget, token);

            // Restore rather than Save: Save stamps updated_at with now and writes sort 0,
            // which is right for an edit and would silently reshuffle a notes tab here.
            foreach (var note in written)
                await notes.RestoreAsync(note, token);
        });
    }

    public async Task ConnectionAsync(Connection connection, CancellationToken ct = default)
    {
        // Deleting a connection un-binds the cards that used it rather than removing them,
        // so they survive as "the connection this widget used is gone". Undo therefore has
        // to re-bind them, or it puts the connection back and leaves the dashboard still
        // covered in that message — an undo that visibly does not undo.
        var bound = (await config.WidgetsAsync(ct))
            .Where(widget => widget.ConnectionId == connection.Id)
            .Select(widget => widget.Id)
            .ToHashSet();

        await config.DeleteConnectionAsync(connection.Id, ct);

        undo.Add(Describe("connection", connection.Name), async token =>
        {
            await config.SaveConnectionAsync(connection, token);

            if (bound.Count == 0)
                return;

            foreach (var widget in (await config.WidgetsAsync(token)).Where(w => bound.Contains(w.Id)))
                await config.SaveWidgetAsync(widget with { ConnectionId = connection.Id }, token);
        },
        // Said out loud because it is the one thing here that undo cannot deliver. The
        // samples and status events go with the connection and are not captured: a month
        // of history for twenty connections is not something to hold in memory against a
        // twelve-second maybe.
        caveat: "Its recorded history is not coming back.");
    }

    private static string Describe(string kind, string name) =>
        name is { Length: > 0 }
            ? $"Deleted the “{name}” {kind}."
            : $"Deleted a {kind}.";
}
