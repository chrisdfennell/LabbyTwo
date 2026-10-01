using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>
/// What happens to an alert after it fires: held by a mute window and delivered when the
/// window ends, escalated when nobody has dealt with it, and its recovery sent everywhere
/// it was said to be broken.
///
/// The rules, in one place:
/// <list type="bullet">
/// <item>An alert that fires inside a mute window covering it is not sent. It still fires —
/// the dashboard shows it, with "muted by …" — and if it is still firing when the window
/// ends it is sent then, once. One that clears inside the window is never mentioned at
/// all: nobody was told it broke, so there is nothing to tell them about it coming back.</item>
/// <item>An alert still firing <see cref="EscalationPolicy.AfterMinutes"/> after it was
/// delivered is sent again to the escalation channels, and again every
/// <see cref="EscalationPolicy.RepeatMinutes"/> if set. For one a mute window held, the
/// clock starts when the window ends, not when it fired.</item>
/// <item>An escalation is held by everything that holds an alert — maintenance, a
/// silence, a dependency that is down, a mute window, and quiet hours in "nothing" mode —
/// and goes as soon as none of them apply if the alert is still firing. Quiet hours in
/// "down only" mode let it through, because it is a down alert.</item>
/// <item>A recovery goes to the rule's channel and every channel the alert was escalated
/// to.</item>
/// </list>
///
/// The ledger (<see cref="Storage.FiringAlertStore"/>) is in the database, so a restart
/// neither escalates everything again nor forgets one that came due while LabbyTwo was
/// stopped — that one goes once, on the first pass after it starts.
/// </summary>
public sealed partial class AlertService
{
    /// <summary>
    /// One change to the ledger at a time. A pass reads an entry, awaits a send, then writes
    /// it; a recovery landing in between would otherwise be followed by an escalation of the
    /// alert it had just cleared.
    /// </summary>
    private readonly SemaphoreSlim _ledger = new(1, 1);

    /// <summary>The alerts that have fired and not cleared, for delivery. Loaded on first use.</summary>
    public async Task<IReadOnlyCollection<FiringAlert>> FiringAsync(CancellationToken ct)
    {
        await firing.LoadAsync(ct);
        return firing.All;
    }

    /// <summary>What the ledger knows about one alert, from memory. Null when it is not firing or has not been read yet.</summary>
    public FiringAlert? Delivery(string key) => firing.Get(key);

    /// <summary>
    /// Alerts somebody has said they are dealing with, by key, with the <see cref="FiringAlert.Since"/>
    /// of the firing they acknowledged — so it answers for that outage and not the next one.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _acknowledged = new();

    /// <summary>Raised when an alert is acknowledged, so a page showing it can say so.</summary>
    public event Action? Acknowledged;

    /// <summary>
    /// "I've seen it, I'm on it" — from the phone view. Holds this alert's escalations until
    /// it clears; the first notice has already gone, and the recovery still goes. It is the
    /// escalation that exists to find somebody who has not noticed, and somebody has.
    ///
    /// Kept in memory only, on purpose. It is a statement about the next hour, not a
    /// setting, and a restart in the middle of an outage that brings the escalations back
    /// errs on the side of being told. False when the alert is not firing (or the ledger
    /// has not been read yet), so there is nothing to acknowledge.
    /// </summary>
    public bool Acknowledge(string key)
    {
        if (firing.Get(key) is not { } entry)
            return false;
        _acknowledged[key] = entry.Since;
        Acknowledged?.Invoke();
        return true;
    }

    /// <summary>Whether the firing under <paramref name="key"/> has been acknowledged.</summary>
    public bool IsAcknowledged(string key) =>
        firing.Get(key) is { } entry && _acknowledged.TryGetValue(key, out var since) && since == entry.Since;

    /// <summary>
    /// The mute window holding this alert back right now, or null. From the windows as last
    /// read, so a page can ask while it draws without touching the database.
    /// </summary>
    public MuteWindow? MutedBy(string? ruleId, string connectionId, DateTimeOffset now) =>
        MuteWindow.Muting(mutes.Current, ruleId, connectionId, now, Zone);

    /// <summary>
    /// An alert has just started firing. Records it, then sends it — unless something is
    /// holding it, in which case it is still recorded, so the dashboard, the end of a mute
    /// window and escalation all know it is there.
    /// </summary>
    /// <param name="ruleId">Null for a connection going down.</param>
    /// <param name="channelId">The rule's channel; null for every channel.</param>
    /// <param name="since">When the condition began. The same as <paramref name="now"/> for a down alert.</param>
    /// <param name="value">The reading that fired it, kept so a restart has something to show.</param>
    public async Task FiredAsync(
        Connection connection, string? ruleId, Alert alert, string? channelId,
        DateTimeOffset since, double value, DateTimeOffset now, CancellationToken ct)
    {
        await firing.LoadAsync(ct);
        await _ledger.WaitAsync(ct);
        try
        {
            var key = alert.Tag ?? (ruleId is null ? FiringAlert.StatusKey(connection.Id) : FiringAlert.RuleKey(ruleId, connection.Id));
            var muting = MuteWindow.Muting(await mutes.AllAsync(ct), ruleId, connection.Id, now, Zone);

            // Written before the send, as the rule evaluator writes its breach: anything that
            // looks while a slow webhook is being waited on must see the alert is out.
            var entry = new FiringAlert(
                key, ruleId, connection.Id, since, now, muting?.Name, null, 0, [],
                alert.Title, alert.Body, alert.Link, value);
            await firing.SaveAsync(entry, ct);

            if (await SuppressedAsync(connection, false, now, ct) is { } reason)
            {
                log.LogInformation("Alert for {Connection} not sent: {Reason}", connection.Name, reason);
                return;
            }

            if (muting is not null)
            {
                log.LogInformation("Alert for {Connection} held by the mute window {Window} until it ends",
                    connection.Name, muting.Name);
                return;
            }

            // The note — "LabbyTwo will try: restart plex in 5 min" — goes on the notice
            // only, not into the ledger's copy, which an escalation half an hour later sends
            // again and by when it would no longer be true.
            alert = await WithFiringNoteAsync(connection, entry, alert, now, ct);
            await BroadcastAsync(alert, channelId is { Length: > 0 } ? [channelId] : null, now, ct);
        }
        finally
        {
            _ledger.Release();
        }
    }

    /// <summary>
    /// An alert has cleared. Sends the recovery to wherever the alert went — including every
    /// channel it was escalated to — unless it was never delivered in the first place.
    /// </summary>
    public async Task ClearedAsync(
        Connection connection, string? ruleId, Alert alert, string? channelId, DateTimeOffset now, CancellationToken ct)
    {
        await firing.LoadAsync(ct);
        await _ledger.WaitAsync(ct);
        try
        {
            var key = alert.Tag ?? (ruleId is null ? FiringAlert.StatusKey(connection.Id) : FiringAlert.RuleKey(ruleId, connection.Id));
            var entry = firing.Get(key);
            await firing.RemoveAsync(key, ct);

            // Asked before anything decides whether to send, because it is also how
            // self-healing learns that what it did worked — which is true whether or not
            // anybody is told.
            if (await ClearedNoteAsync(connection, key, entry, now, ct) is { Length: > 0 } note)
                alert = alert with { Body = $"{alert.Body} {note}".Trim() };

            if (entry?.HeldBy is { } window)
            {
                log.LogInformation("{Connection} recovered inside the mute window {Window}; it was never announced, so neither is this",
                    connection.Name, window);
                return;
            }

            if (await SuppressedAsync(connection, true, now, ct) is { } reason)
            {
                log.LogInformation("Alert for {Connection} not sent: {Reason}", connection.Name, reason);
                return;
            }

            if (MuteWindow.Muting(await mutes.AllAsync(ct), ruleId, connection.Id, now, Zone) is { } muting)
            {
                log.LogInformation("Recovery for {Connection} not sent: muted by {Window}", connection.Name, muting.Name);
                return;
            }

            await BroadcastAsync(alert, FiringAlert.RecoveryChannels(channelId, entry), now, ct);
        }
        finally
        {
            _ledger.Release();
        }
    }

    /// <summary>
    /// Stops following an alert without saying anything — its rule was deleted or disabled,
    /// or its connection muted. Nobody asked for a recovery notice from a rule they removed.
    /// </summary>
    public async Task ForgetAsync(IEnumerable<string> keys, CancellationToken ct)
    {
        await firing.LoadAsync(ct);
        await _ledger.WaitAsync(ct);
        try
        {
            foreach (var key in keys)
                await firing.RemoveAsync(key, ct);
        }
        finally
        {
            _ledger.Release();
        }
    }

    /// <summary>
    /// Everything owed at <paramref name="now"/>: first notices a mute window was holding
    /// whose window has ended, and escalations that have come due. Run after every alert
    /// pass — every probe sweep — which is as often as anything here can change.
    /// </summary>
    public async Task FollowUpAsync(DateTimeOffset now, CancellationToken ct)
    {
        await firing.LoadAsync(ct);
        await _ledger.WaitAsync(ct);
        try
        {
            if (firing.All.Count == 0)
                return;

            var bag = await settings.AllAsync(ct);
            var fallback = EscalationPolicy.From(bag);
            var quiet = AlertPolicy.From(bag);
            var allRules = await rules.AllAsync(ct);
            var windows = await mutes.AllAsync(ct);
            var connections = await config.ConnectionsAsync(ct);

            // An acknowledgement lasts as long as the firing it was given for.
            foreach (var (key, since) in _acknowledged)
            {
                if (firing.Get(key)?.Since != since)
                    _acknowledged.TryRemove(key, out _);
            }

            foreach (var entry in firing.All)
            {
                var connection = connections.FirstOrDefault(c => c.Id == entry.ConnectionId);
                var rule = entry.RuleId is null ? null : allRules.FirstOrDefault(r => r.Id == entry.RuleId);

                // Anything that is no longer a live alert is dropped quietly: its connection
                // or rule was deleted, disabled or muted, or — for a down alert whose
                // recovery was missed, say across a restart — it is plainly up again.
                if (connection is null
                    || !connection.AlertsEnabled
                    || (entry.RuleId is not null && rule is not { Enabled: true })
                    || (entry.IsStatus && monitor.State(connection.Id) is { IsUp: true }))
                {
                    await firing.RemoveAsync(entry.Key, ct);
                    continue;
                }

                var muting = MuteWindow.Muting(windows, entry.RuleId, connection.Id, now, Zone);

                if (entry.HeldBy is { } heldBy)
                {
                    if (muting is not null)
                    {
                        // One window can hand over to another; the page should name the one in force.
                        if (muting.Name != heldBy)
                            await firing.SaveAsync(entry with { HeldBy = muting.Name }, ct);
                        continue;
                    }

                    await DeliverHeldAsync(entry, heldBy, connection, rule, now, ct);
                    continue;
                }

                // Somebody said they are on it: escalating is for when nobody has noticed.
                if (IsAcknowledged(entry.Key))
                    continue;

                var policy = entry.IsStatus ? fallback : EscalationPolicy.For(rule, fallback);
                if (policy.DueAt(entry.ClockFrom, entry.EscalatedAt) is not { } due || due > now)
                    continue;

                var escalation = new Alert(AlertLevel.Down,
                    $"Still firing · {entry.Title}",
                    $"Not cleared after {Humanise(now - entry.ClockFrom)}. {entry.Body}".TrimEnd())
                {
                    Tag = entry.Key,
                    Link = entry.Link,
                };

                // Held, not dropped: the entry stays due, so the escalation goes on the first
                // pass after whatever is holding it lets go — if the alert has not cleared.
                if (await SuppressedAsync(connection, false, now, ct) is { } reason)
                {
                    log.LogDebug("Escalation for {Title} waiting: {Reason}", entry.Title, reason);
                    continue;
                }
                if (muting is not null || !quiet.Allows(escalation, now, Zone))
                    continue;

                var to = policy.Channels.Count == 0 ? [FiringAlert.AllChannels] : policy.Channels;
                await firing.SaveAsync(entry with
                {
                    EscalatedAt = now,
                    Escalations = entry.Escalations + 1,
                    EscalatedTo = [.. entry.EscalatedTo.Concat(to).Distinct()],
                }, ct);

                log.LogWarning("Escalating {Title}: still firing after {Minutes:0} minute(s)",
                    entry.Title, (now - entry.ClockFrom).TotalMinutes);
                await SendToAsync(escalation, policy.Channels.Count == 0 ? null : policy.Channels, ct);
            }
        }
        finally
        {
            _ledger.Release();
        }
    }

    /// <summary>
    /// The first notice a mute window held, now that the window has ended and the alert is
    /// still firing. Sent once, subject to everything else that holds an alert at this
    /// moment — a window ending during maintenance is still maintenance — and either way no
    /// longer owed. The escalation clock starts now.
    /// </summary>
    private async Task DeliverHeldAsync(
        FiringAlert entry, string window, Connection connection, AlertRule? rule, DateTimeOffset now, CancellationToken ct)
    {
        await firing.SaveAsync(entry with { HeldBy = null, ClockFrom = now }, ct);

        if (await SuppressedAsync(connection, false, now, ct) is { } reason)
        {
            log.LogInformation("Alert for {Connection} held by {Window} not sent when it ended: {Reason}",
                connection.Name, window, reason);
            return;
        }

        var alert = new Alert(AlertLevel.Down, entry.Title,
            $"{entry.Body} Held during “{window}”, which has ended, and still firing.".TrimStart())
        {
            Tag = entry.Key,
            Link = entry.Link,
        };
        alert = await WithFiringNoteAsync(connection, entry with { HeldBy = null, ClockFrom = now }, alert, now, ct);
        await BroadcastAsync(alert, rule?.ChannelId is { Length: > 0 } channel ? [channel] : null, now, ct);
    }
}
