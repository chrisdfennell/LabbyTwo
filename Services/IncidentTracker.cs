using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Turns the change feed into incidents. Every service going down and every alert rule
/// firing is a signal; <see cref="IncidentRules"/> decides whether it starts an incident,
/// joins one, or ends one, and this keeps the result.
///
/// Reads the feed rather than the monitor and the evaluator directly, so an incident is
/// made of exactly the changes the feed shows — the two can never disagree about what
/// went down when.
///
/// The incidents that could still change — open ones, and ones closed within
/// <see cref="IncidentRules.JoinWindow"/> — are kept in memory, loaded once at startup, so
/// a signal is decided without a read and costs one small write.
///
/// A recovery the feed never saw is caught too. After each sweep and each alert pass the
/// open incidents' members are checked against what the monitor and the evaluator hold
/// now (see <see cref="IncidentRules.Reconcile"/>): a rule deleted while firing, or an
/// alert whose firing state was lost when LabbyTwo restarted, would otherwise hold its
/// incident open for ever.
/// </summary>
public sealed class IncidentTracker(
    IncidentStore incidents,
    ChangeStore changes,
    HealthMonitor monitor,
    MetricAlertService alerts,
    AppSettingsStore settings,
    ILogger<IncidentTracker> log) : IHostedService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<Incident>? _live;
    private bool _alertsEvaluated;

    public Task StartAsync(CancellationToken ct)
    {
        changes.Recorded += OnChange;
        monitor.Updated += OnSweep;
        alerts.Updated += OnAlertPass;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        changes.Recorded -= OnChange;
        monitor.Updated -= OnSweep;
        alerts.Updated -= OnAlertPass;
        return Task.CompletedTask;
    }

    /// <summary>The signal a change is, or null for a change that is not one — a container restart, a certificate.</summary>
    public static IncidentSignal? SignalFor(Change change, bool maintenance)
    {
        if (change.ConnectionId is not { Length: > 0 } connection)
            return null;

        return (change.Kind, change.Action) switch
        {
            (ChangeKinds.Status, ChangeActions.Down or ChangeActions.Up) => new IncidentSignal(
                IncidentMember.StatusKey(connection), ChangeKinds.Status, connection,
                NameFrom(change.Title, change.Action == ChangeActions.Down ? " went down" : " came back"),
                change.Action == ChangeActions.Down, change.At, maintenance),
            (ChangeKinds.Alert, ChangeActions.Firing or ChangeActions.Cleared) when change.Subject.Length > 0 => new IncidentSignal(
                IncidentMember.AlertKey(change.Subject, connection), ChangeKinds.Alert, connection,
                NameFrom(change.Title, prefix: change.Action == ChangeActions.Firing ? "Alert fired: " : "Alert cleared: "),
                change.Action == ChangeActions.Firing, change.At, maintenance),
            _ => null,
        };
    }

    /// <summary>The name inside a change's title — "NAS" from "NAS went down".</summary>
    private static string NameFrom(string title, string suffix = "", string prefix = "")
    {
        var name = title;
        if (prefix.Length > 0 && name.StartsWith(prefix, StringComparison.Ordinal))
            name = name[prefix.Length..];
        if (suffix.Length > 0 && name.EndsWith(suffix, StringComparison.Ordinal))
            name = name[..^suffix.Length];
        return name;
    }

    private void OnChange(Change change)
    {
        if (change.Kind is not (ChangeKinds.Status or ChangeKinds.Alert))
            return;

        // In the order they were recorded, one after another, not each on a task of its
        // own: two services going down in the same sweep arrive a moment apart, and which
        // was first is what names the incident. Off the recorder's thread all the same —
        // it is in the middle of a sweep.
        //
        // Whether LabbyTwo could see is read now, as the change is recorded, not when the
        // queue reaches it: by then sight may be back, and a failure recorded blind would
        // be counted after all.
        var blind = monitor.IsBlind;
        lock (_queueLock)
            _queue = _queue.ContinueWith(_ => ApplyChangeAsync(change, blind, CancellationToken.None), TaskScheduler.Default).Unwrap();
    }

    private readonly Lock _queueLock = new();
    private Task _queue = Task.CompletedTask;

    private async Task ApplyChangeAsync(Change change, bool blind, CancellationToken ct)
    {
        try
        {
            var maintenance = Maintenance.From(await settings.AllAsync(ct), change.At).On;
            if (SignalFor(change, maintenance) is not { } signal)
                return;

            // A failure while LabbyTwo cannot see the lab neither opens an incident nor
            // grows one. The monitor already holds its own failures back while that lasts;
            // this is for anything else in the feed that says "down" in the meantime.
            // Recoveries still count — something coming back is an answer.
            if (signal.Bad && blind)
            {
                log.LogInformation("Not adding \"{Title}\" to an incident: LabbyTwo cannot see the lab right now", change.Title);
                return;
            }

            await ApplyAsync(signal, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not add \"{Title}\" to an incident", change.Title);
        }
    }

    /// <summary>Applies one signal and stores what it changed. Public for tests.</summary>
    public async Task<Incident?> ApplyAsync(IncidentSignal signal, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var live = await LiveAsync(ct);
            if (IncidentRules.Apply(live, signal) is not { } changed)
                return null;

            var saved = await incidents.SaveAsync(changed, ct);
            Keep(live, saved, signal.At);
            return saved;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The incidents a signal could still touch, loaded on first use: the open ones and any
    /// closed recently enough to be rejoined. Loading lazily, rather than at start, keeps
    /// startup from waiting on a read the first outage may be days away from needing.
    /// </summary>
    private async Task<List<Incident>> LiveAsync(CancellationToken ct)
    {
        if (_live is not null)
            return _live;
        var recent = await incidents.RecentAsync(DateTimeOffset.Now - IncidentRules.JoinWindow - TimeSpan.FromDays(1), 50, ct: ct);
        _live = [.. recent.Where(i => i.IsOpen || DateTimeOffset.Now - i.LastActivity <= IncidentRules.JoinWindow)];
        return _live;
    }

    /// <summary>Puts a saved incident back in the live list, and lets go of any that can no longer change.</summary>
    private static void Keep(List<Incident> live, Incident saved, DateTimeOffset now)
    {
        live.RemoveAll(i => i.Id == saved.Id);
        live.Add(saved);
        live.RemoveAll(i => !i.IsOpen && now - i.LastActivity > IncidentRules.JoinWindow);
    }

    private void OnSweep() => _ = Task.Run(() => ReconcileAsync(DateTimeOffset.Now, CancellationToken.None));

    private void OnAlertPass()
    {
        _alertsEvaluated = true;
        _ = Task.Run(() => ReconcileAsync(DateTimeOffset.Now, CancellationToken.None));
    }

    /// <summary>
    /// Closes open members that are fine now according to the monitor and the evaluator,
    /// though no recovery was signalled. Nothing is read from the database unless an
    /// incident is open.
    /// </summary>
    public async Task ReconcileAsync(DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            if (_live is { } known && known.All(i => !i.IsOpen))
                return;

            await _gate.WaitAsync(ct);
            try
            {
                var live = await LiveAsync(ct);
                foreach (var incident in live.Where(i => i.IsOpen).ToList())
                {
                    if (IncidentRules.Reconcile(incident, StillBad, now) is { } changed)
                        Keep(live, await incidents.SaveAsync(changed, ct), now);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not check open incidents against what is up now");
        }
    }

    /// <summary>
    /// Whether a member is still down, by what is in memory now. Null — leave it be — when
    /// there is no way to tell yet: the monitor has not finished a sweep since starting, or
    /// the evaluator has not had a pass.
    /// </summary>
    private bool? StillBad(IncidentMember member)
    {
        if (member.ConnectionId is not { } connection)
            return null;

        if (member.Kind == ChangeKinds.Status)
        {
            if (monitor.State(connection) is { } state)
                return state.IsUp is { } up ? !up : null;
            // No state at all after a sweep has run: the connection was deleted or disabled,
            // and the monitor no longer watches it. That is the end of its part in this.
            return monitor.Status.SweepsCompleted > 0 ? false : null;
        }

        if (member.Kind == ChangeKinds.Alert && _alertsEvaluated)
        {
            var parts = member.Key.Split(':', 3);
            if (parts.Length != 3)
                return null;
            var breach = alerts.All.FirstOrDefault(b => b.RuleId == parts[1] && b.ConnectionId == parts[2]);
            // Gone from the evaluator altogether: the rule was deleted, disabled or muted.
            if (breach is null)
                return false;
            // Still firing, or breaching and counting down its sustain window again after a
            // restart: still bad. Only a breach that is neither is over.
            return breach.Firing || breach.Since is not null;
        }

        return null;
    }
}
