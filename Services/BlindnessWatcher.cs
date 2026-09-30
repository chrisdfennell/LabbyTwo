using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Says so when LabbyTwo loses sight of the lab, and when it gets it back: one line in the
/// change feed at each end, and — if it goes on for longer than
/// <see cref="BlindnessRules.NotifyAfter"/> — one notification. That one is worth sending
/// even though every down alert is being held: a monitor that has quietly stopped seeing
/// is exactly what nobody finds out about until they needed it.
///
/// Only one per spell, however long it lasts, and nothing when it ends: the notice already
/// said nothing was being alerted on, and the feed has the end.
/// </summary>
public sealed class BlindnessWatcher(
    HealthMonitor monitor,
    ChangeStore changes,
    AlertService alerts,
    ILogger<BlindnessWatcher> log) : IHostedService
{
    /// <summary>When the spell that has been notified about started, so each spell is told at most once.</summary>
    private DateTimeOffset? _notifiedFor;

    private int _checking;

    public Task StartAsync(CancellationToken ct)
    {
        monitor.BlindnessChanged += OnChanged;
        monitor.Updated += OnSweep;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        monitor.BlindnessChanged -= OnChanged;
        monitor.Updated -= OnSweep;
        return Task.CompletedTask;
    }

    private void OnChanged(Blindness before, Blindness after) =>
        _ = Task.Run(() => RecordAsync(before, after, DateTimeOffset.Now, CancellationToken.None));

    private void OnSweep()
    {
        if (!monitor.IsBlind || Interlocked.Exchange(ref _checking, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await NotifyIfLongAsync(monitor.Blindness, DateTimeOffset.Now, CancellationToken.None);
            }
            finally
            {
                Volatile.Write(ref _checking, 0);
            }
        });
    }

    /// <summary>The line in the feed for a start or an end. Public, with the clock as a parameter, for tests.</summary>
    public async Task RecordAsync(Blindness before, Blindness after, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await changes.RecordAsync(Entry(before, after, now), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not record that LabbyTwo {What} the lab", after.Impaired ? "lost sight of" : "can see");
        }
    }

    /// <summary>What the feed says when sight is lost or comes back.</summary>
    public static Change Entry(Blindness before, Blindness after, DateTimeOffset now)
    {
        if (after.Impaired)
            return new Change(now, ChangeKinds.Monitor, ChangeActions.Down, null, "labbytwo",
                $"LabbyTwo can't see the lab: {after.Reason}", after.Detail);

        var lasted = before.Since is { } since && now > since ? $" after {Ago.Duration(now - since)}" : "";
        return new Change(now, ChangeKinds.Monitor, ChangeActions.Up, null, "labbytwo",
            $"LabbyTwo can see the lab again{lasted}",
            "Checks are being recorded again. Anything that really went down while it could not see is reported by " +
            "the next checks in the usual way.");
    }

    /// <summary>Sends the one notification for a spell once it has gone on long enough. Public for tests.</summary>
    /// <returns>True when it sent (or tried to send) it now.</returns>
    public async Task<bool> NotifyIfLongAsync(Blindness blindness, DateTimeOffset now, CancellationToken ct)
    {
        if (!blindness.Impaired || blindness.Since is not { } since || _notifiedFor == since ||
            blindness.For(now) < BlindnessRules.NotifyAfter)
            return false;

        _notifiedFor = since;
        try
        {
            await alerts.BroadcastAsync(new Alert(AlertLevel.Info,
                "LabbyTwo can't see the lab",
                $"{blindness.Headline} It has lasted {Ago.Duration(blindness.For(now))}. {blindness.Detail}")
            {
                Tag = "labbytwo:blind",
                Link = "health",
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not send the notice that LabbyTwo cannot see the lab");
        }
        return true;
    }
}
