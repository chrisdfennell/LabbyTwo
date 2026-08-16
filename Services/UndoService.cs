namespace LabbyTwo.Services;

/// <summary>
/// The last thing you deleted, and the way back to it.
///
/// Every delete in LabbyTwo already asks "are you sure", and a confirmation is the wrong
/// tool for the mistake people actually make. Nobody is uncertain at the dialog — they
/// clicked ✕ on the row above the one they meant, and the dialog names a thing they are
/// not reading because they already know what they are deleting. A confirmation asks a
/// question; an undo answers it.
///
/// Scoped rather than singleton, unlike everything else in this app, because this is one
/// person's last action rather than a fact about the installation. With auth switched on
/// a singleton would offer your deletion to whoever else happened to be looking.
/// </summary>
public sealed class UndoService
{
    /// <param name="Caveat">
    /// What will not come back, when something will not. Restoring a connection cannot
    /// bring back the metrics that were recorded against it, and a promise of "undo" that
    /// quietly does less than it says is worse than no promise.
    /// </param>
    public sealed record Offer(
        string Description,
        string? Caveat,
        Func<CancellationToken, Task> Restore,
        DateTimeOffset Until);

    private Offer? _offer;

    public event Action? Changed;

    /// <summary>
    /// Long enough to notice the row that vanished was the wrong one, short enough that
    /// the bar is not still sitting there when you have moved on to something else.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(12);

    /// <summary>
    /// The offer, or null once it has lapsed. Expiry is decided here on read rather than
    /// by a timer that clears it, so a circuit that was idle through the window comes back
    /// to no offer rather than to a stale one it would happily replay.
    /// </summary>
    public Offer? Current(DateTimeOffset now) =>
        _offer is { } offer && offer.Until > now ? offer : null;

    public Offer? Current() => Current(DateTimeOffset.Now);

    public void Add(string description, Func<CancellationToken, Task> restore, string? caveat = null)
    {
        // One deep on purpose. An undo stack invites the question of what "undo" means
        // three deletes and an edit later, and the honest answer for a dashboard is that
        // nobody wants to find out — this covers the misclick, and the nightly backup
        // covers the rest.
        _offer = new Offer(description, caveat, restore, DateTimeOffset.Now + Window);
        Changed?.Invoke();
    }

    public async Task<bool> UndoAsync(CancellationToken ct = default)
    {
        if (Current() is not { } offer)
            return false;

        // Cleared before the restore runs, so a slow restore cannot be started twice by
        // an impatient second click.
        _offer = null;
        Changed?.Invoke();

        await offer.Restore(ct);
        return true;
    }

    public void Dismiss()
    {
        if (_offer is null)
            return;

        _offer = null;
        Changed?.Invoke();
    }
}
