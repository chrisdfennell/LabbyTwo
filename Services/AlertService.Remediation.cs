using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>
/// Something with a line to add to an alert as it is sent — what LabbyTwo is about to do
/// about it, or what it did. Asked by <see cref="AlertService"/> rather than bolted on by
/// whoever sends the alert, so a down alert and a rule alert, a first notice and one a mute
/// window held, all carry the same line.
/// </summary>
public interface IAlertNotes
{
    /// <summary>A sentence to add to the first notice of an alert that has just fired, or null.</summary>
    Task<string?> FiringNoteAsync(Connection connection, FiringAlert entry, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// An alert has cleared. Called whether or not the recovery is then sent — the note is
    /// also the record that something worked — and returns a sentence for the recovery, or null.
    /// </summary>
    Task<string?> ClearedNoteAsync(Connection connection, string key, FiringAlert? entry, DateTimeOffset now, CancellationToken ct);
}

/// <summary>Self-healing's way in: the notes above, and escalating on demand. See <see cref="RemediationService"/>.</summary>
public sealed partial class AlertService
{
    /// <summary>
    /// Who adds lines to alerts. Set by <see cref="RemediationService"/> when it starts,
    /// rather than injected, because it depends on this service and not the other way
    /// round — and nothing else here needs to know it exists.
    /// </summary>
    public IAlertNotes? Notes { get; set; }

    /// <summary>The alert with a note added to its body, or unchanged. A note that throws costs the note, not the alert.</summary>
    private async Task<Alert> WithFiringNoteAsync(Connection connection, FiringAlert entry, Alert alert, DateTimeOffset now, CancellationToken ct)
    {
        if (Notes is not { } notes)
            return alert;
        try
        {
            return await notes.FiringNoteAsync(connection, entry, now, ct) is { Length: > 0 } note
                ? alert with { Body = $"{alert.Body} {note}".Trim() }
                : alert;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not add self-healing's note to \"{Title}\"", alert.Title);
            return alert;
        }
    }

    /// <summary>Tells <see cref="Notes"/> an alert cleared, and returns its line for the recovery.</summary>
    private async Task<string?> ClearedNoteAsync(Connection connection, string key, FiringAlert? entry, DateTimeOffset now, CancellationToken ct)
    {
        if (Notes is not { } notes)
            return null;
        try
        {
            return await notes.ClearedNoteAsync(connection, key, entry, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not tell self-healing that {Key} cleared", key);
            return null;
        }
    }

    /// <summary>
    /// Escalates a firing alert now, without waiting for its escalation clock — self-healing
    /// has tried and it did not help, which is exactly the "nobody has dealt with it" the
    /// clock stands in for. Goes where the alert's escalation policy sends it (every channel
    /// when it has none), and is written in the ledger as an escalation, so the recovery goes
    /// there too and the clock carries on from here rather than escalating it again at once.
    ///
    /// Held by whatever holds an alert, like any escalation. Returns how many channels it
    /// reached: zero when held, or when the alert has already cleared.
    /// </summary>
    public async Task<int> EscalateNowAsync(string key, Alert alert, DateTimeOffset now, CancellationToken ct)
    {
        await firing.LoadAsync(ct);
        await _ledger.WaitAsync(ct);
        try
        {
            if (firing.Get(key) is not { } entry
                || await config.ConnectionAsync(entry.ConnectionId, ct) is not { } connection)
                return 0;

            if (await SuppressedAsync(connection, false, now, ct) is { } reason)
            {
                log.LogInformation("Escalation of {Title} not sent: {Reason}", entry.Title, reason);
                return 0;
            }
            if (MuteWindow.Muting(await mutes.AllAsync(ct), entry.RuleId, connection.Id, now, Zone) is not null
                || !await AllowedAtAsync(alert, now, ct))
                return 0;

            var fallback = EscalationPolicy.From(await settings.AllAsync(ct));
            var rule = entry.RuleId is null ? null : (await rules.AllAsync(ct)).FirstOrDefault(r => r.Id == entry.RuleId);
            var policy = entry.IsStatus ? fallback : EscalationPolicy.For(rule, fallback);
            IReadOnlyList<string> to = policy.Channels.Count == 0 ? [FiringAlert.AllChannels] : policy.Channels;

            await firing.SaveAsync(entry with
            {
                EscalatedAt = now,
                Escalations = entry.Escalations + 1,
                EscalatedTo = [.. entry.EscalatedTo.Concat(to).Distinct()],
            }, ct);

            log.LogWarning("Escalating {Title}: self-healing did not fix it", entry.Title);
            return await SendToAsync(alert, policy.Channels.Count == 0 ? null : policy.Channels, ct);
        }
        finally
        {
            _ledger.Release();
        }
    }
}
