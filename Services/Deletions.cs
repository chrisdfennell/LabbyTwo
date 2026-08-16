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

    public Task ConnectionAsync(Connection connection, CancellationToken ct = default) =>
        ConnectionsAsync([connection], ct);

    /// <summary>
    /// One or twenty, down one path. A bulk delete that skipped the capture would be the
    /// one delete in the app with no way back, which is precisely backwards: deleting
    /// twenty things at once is when you most want one.
    /// </summary>
    public async Task ConnectionsAsync(IReadOnlyCollection<Connection> connections, CancellationToken ct = default)
    {
        if (connections.Count == 0)
            return;

        var ids = connections.Select(connection => connection.Id).ToHashSet();

        // Deleting a connection un-binds the cards that used it rather than removing them,
        // so they survive as "the connection this widget used is gone". Undo therefore has
        // to re-bind them, or it puts the connections back and leaves the dashboard still
        // covered in that message — an undo that visibly does not undo. Which card went
        // with which connection is recorded, not just that it was bound to something.
        var bound = (await config.WidgetsAsync(ct))
            .Where(widget => widget.ConnectionId is { } id && ids.Contains(id))
            .Select(widget => (Widget: widget.Id, Connection: widget.ConnectionId!))
            .ToList();

        foreach (var connection in connections)
            await config.DeleteConnectionAsync(connection.Id, ct);

        var one = connections.Count == 1;

        undo.Add(
            one ? Describe("connection", connections.First().Name)
                : $"Deleted {connections.Count} connections.",
            async token =>
            {
                foreach (var connection in connections)
                    await config.SaveConnectionAsync(connection, token);

                if (bound.Count == 0)
                    return;

                var widgets = await config.WidgetsAsync(token);
                foreach (var (widgetId, connectionId) in bound)
                {
                    if (widgets.FirstOrDefault(widget => widget.Id == widgetId) is { } widget)
                        await config.SaveWidgetAsync(widget with { ConnectionId = connectionId }, token);
                }
            },
            // Said out loud because it is the one thing here that undo cannot deliver. The
            // samples and status events go with the connection and are not captured: a
            // month of history for twenty connections is not something to hold in memory
            // against a twelve-second maybe.
            caveat: one
                ? "Its recorded history is not coming back."
                : "Their recorded history is not coming back.");
    }

    private static string Describe(string kind, string name) =>
        name is { Length: > 0 }
            ? $"Deleted the “{name}” {kind}."
            : $"Deleted a {kind}.";
}
