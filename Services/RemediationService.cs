using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Self-healing: runs the <see cref="Remediation"/> attached to an alert rule or to a
/// connection going down, judges whether it helped, and says so.
///
/// It reads the same ledger of firing alerts that escalation reads
/// (<see cref="FiringAlertStore"/>), after every alert pass, so "down for five minutes"
/// means exactly what an escalation's "still firing after fifteen" means, and a mute window
/// or maintenance that holds one holds the other.
///
/// The guardrails, in the order they are checked before anything runs:
/// <list type="number">
/// <item>The global switch (<see cref="RemediationSettings.Enabled"/>) is on.</item>
/// <item>Nothing holds the alert: not maintenance, not a silence, not a parent that is
/// down (restarting Plex will not bring the NAS back), not a mute window. Anything
/// somebody has said they already know about is left to them.</item>
/// <item>It has attempts left in this outage, and the cooldown since its last run is over —
/// see <see cref="RemediationRules"/>, which is also the loop guard.</item>
/// <item>The whole installation has made fewer than <see cref="RemediationSettings.MaxPerHour"/>
/// runs in the last hour, counted from the change feed so a restart does not reset it.</item>
/// <item>The target may be acted on: never LabbyTwo's own container; a protected container
/// or a dangerous action only when the remediation opts in (<see cref="RemediationGuards"/>).</item>
/// </list>
///
/// Every run, verdict and refusal goes into the change feed as kind
/// <see cref="ChangeKinds.Remediation"/> against the connection that alerted, which is
/// what puts it on that incident's timeline — the timeline is the feed read for the
/// incident's window.
///
/// Locking: <see cref="_gate"/> guards the state rows and is only ever held for memory and
/// small writes, never while a container restarts or a notification is sent. The alert
/// service calls <see cref="ClearedNoteAsync"/> while holding its own ledger lock, so this
/// class must never call into the ledger while holding the gate — and it does not.
/// </summary>
public sealed class RemediationService(
    RemediationStore store,
    AlertService alerts,
    MetricAlertService evaluator,
    AlertRuleStore rules,
    ConfigStore config,
    AppSettingsStore settings,
    ChangeStore changes,
    IRemediationActions actions,
    ILogger<RemediationService> log) : IHostedService, IAlertNotes
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>One pass at a time; a sweep that lands mid-pass is picked up by the next one.</summary>
    private readonly SemaphoreSlim _pass = new(1, 1);

    public Task StartAsync(CancellationToken ct)
    {
        alerts.Notes = this;
        evaluator.Updated += OnAlertPass;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        evaluator.Updated -= OnAlertPass;
        if (ReferenceEquals(alerts.Notes, this))
            alerts.Notes = null;
        return Task.CompletedTask;
    }

    private void OnAlertPass() => _ = Task.Run(async () =>
    {
        // Skipped rather than queued when a pass is running: the next sweep is at most a
        // minute away, and nothing here is so urgent that two passes should stack.
        if (!await _pass.WaitAsync(0))
            return;
        try
        {
            await PassAsync(DateTimeOffset.Now, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Self-healing pass failed");
        }
        finally
        {
            _pass.Release();
        }
    });

    /// <summary>One pass at <paramref name="now"/>. Public so tests move the clock rather than wait for it.</summary>
    public async Task EvaluateAsync(DateTimeOffset now, CancellationToken ct)
    {
        await _pass.WaitAsync(ct);
        try
        {
            await PassAsync(now, ct);
        }
        finally
        {
            _pass.Release();
        }
    }

    /// <summary>A run decided on under the gate and carried out after it.</summary>
    private sealed record PendingRun(Remediation Remediation, FiringAlert Entry, Connection Connection,
        RemediationState? Before, RemediationState Reserved);

    private async Task PassAsync(DateTimeOffset now, CancellationToken ct)
    {
        await store.LoadStatesAsync(ct);
        var remediations = await store.AllAsync(ct);
        var firing = await alerts.FiringAsync(ct);
        if (remediations.Count == 0 && store.States.Count == 0)
            return;

        var global = RemediationSettings.From(await settings.AllAsync(ct));
        var connections = await config.ConnectionsAsync(ct);
        var runs = new List<PendingRun>();
        var notices = new List<Func<Task>>();

        await _gate.WaitAsync(ct);
        try
        {
            var firingKeys = firing.Select(f => f.Key).ToHashSet();

            // Outages that ended without the alert service telling us — a recovery missed
            // across a restart, a rule deleted while firing — and ones long enough over to forget.
            foreach (var state in store.States.Where(s => !firingKeys.Contains(s.AlertKey)).ToList())
            {
                var remediation = remediations.FirstOrDefault(r => r.Trigger == TriggerOf(state.AlertKey));
                if (state.CheckAt is not null)
                    await HelpedAsync(state, ConnectionOf(state.AlertKey), connections, now, ct);
                else if (RemediationRules.Expired(remediation, state, now))
                    await store.RemoveStateAsync(state.AlertKey, ct);
            }

            var used = await RunsInLastHourAsync(now, ct);

            foreach (var entry in firing)
            {
                var remediation = remediations.FirstOrDefault(r => r.Trigger == Remediation.TriggerFor(entry));
                if (remediation is not { Enabled: true, IsComplete: true }
                    || connections.FirstOrDefault(c => c.Id == entry.ConnectionId) is not { } connection)
                    continue;

                var state = RemediationRules.Current(remediation, entry, store.State(entry.Key));
                switch (RemediationRules.Decide(remediation, entry, state, now))
                {
                    case RemediationRules.Step.NotHelped:
                        notices.AddRange(await NotHelpedAsync(remediation, entry, connection, state!, now, ct));
                        continue;

                    case RemediationRules.Step.Run:
                        break;

                    default:
                        continue;
                }

                if (await HeldBecauseAsync(global, entry, connection, used, now, ct) is { } held)
                {
                    await HeldAsync(remediation, entry, connection, state, held, now, ct);
                    continue;
                }

                // Reserved before anything runs, and stored: if LabbyTwo stops half way
                // through a restart, the attempt is still counted when it starts again.
                var reserved = new RemediationState(entry.Key,
                    state?.EpisodeStart ?? entry.Since,
                    (state?.Attempts ?? 0) + 1,
                    now,
                    now + remediation.CheckAfter,
                    state?.Did ?? "",
                    RemediationOutcomes.Waiting,
                    "",
                    false);
                await store.SaveStateAsync(reserved, ct);
                runs.Add(new PendingRun(remediation, entry, connection, state, reserved));
                used++;
            }
        }
        finally
        {
            _gate.Release();
        }

        foreach (var run in runs)
            notices.AddRange(await RunAsync(run, now, ct));

        foreach (var notice in notices)
        {
            try
            {
                await notice();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Self-healing could not send its notice");
            }
        }
    }

    /// <summary>
    /// Why it must not run now though it is due, or null. Everything that holds an alert
    /// holds its remediation too — somebody who has put the lab in maintenance, silenced
    /// the connection or set a mute window has said they know, and a restart they did not
    /// ask for is the last thing they want in the middle of their own work.
    /// </summary>
    private async Task<string?> HeldBecauseAsync(
        RemediationSettings global, FiringAlert entry, Connection connection, int used, DateTimeOffset now, CancellationToken ct)
    {
        if (!global.Enabled)
            return "self-healing is switched off";
        if (await alerts.SuppressedAsync(connection, false, now, ct) is { } reason)
            return reason;
        if ((entry.HeldBy ?? alerts.MutedBy(entry.RuleId, connection.Id, now)?.Name) is { } window)
            return $"muted by “{window}”";
        if (used >= global.MaxPerHour)
            return global.MaxPerHour == 0
                ? "the hourly limit is set to 0"
                : $"LabbyTwo has already run {used} automatic action{(used == 1 ? "" : "s")} in the last hour, the most allowed";
        return null;
    }

    /// <summary>Prepares and runs one reserved attempt, then records what happened.</summary>
    private async Task<IReadOnlyList<Func<Task>>> RunAsync(PendingRun run, DateTimeOffset now, CancellationToken ct)
    {
        var (remediation, entry, connection, before, reserved) = run;

        RemediationPlan plan;
        try
        {
            plan = await actions.PrepareAsync(remediation, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            plan = RemediationPlan.Refuse("run its action", "Ran its action", ex.GetBaseException().Message);
        }

        ActionResult? result = null;
        if (plan.Run is { } call)
        {
            try
            {
                result = await call(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = ActionResult.Failed(ex.GetBaseException().Message);
            }
        }

        await _gate.WaitAsync(ct);
        try
        {
            // The alert may have cleared while the action ran, and the verdict been written
            // already — in which case that is the story, and this only adds the run to the feed.
            var current = store.State(entry.Key);
            var untouched = current == reserved;

            if (plan.Refused is { } why)
            {
                // Not an attempt: nothing ran. Put the state back as it was, with the reason,
                // so the feed says why once and the next pass can try again when it can.
                if (untouched)
                {
                    var restored = (before ?? new RemediationState(entry.Key, now, 0, null, null, "", "", "", false)) with { Note = why };
                    await store.SaveStateAsync(restored, ct);
                }
                if (before?.Note != why)
                    await RecordAsync(new Change(now, ChangeKinds.Remediation, ChangeActions.Skipped, connection.Id, Subject(remediation),
                        $"Did not {plan.Doing}", $"{why} ({entry.Title})"), ct);
                return [];
            }

            var at = Clock(now);
            if (result is { Ok: true })
            {
                log.LogWarning("Self-healing: {Did} because {Title}", plan.Did, entry.Title);
                if (untouched)
                {
                    // A disruptive action — a reboot — is judged once its target is expected back, not before.
                    var check = now + Max(remediation.CheckAfter, plan.Disrupts ?? TimeSpan.Zero);
                    await store.SaveStateAsync(reserved with { Did = plan.Did, CheckAt = check }, ct);
                }
                else if (current is not null && current.Did.Length == 0)
                {
                    await store.SaveStateAsync(current with { Did = plan.Did }, ct);
                }
                await RecordAsync(new Change(now, ChangeKinds.Remediation, ChangeActions.Remediated, connection.Id, Subject(remediation),
                    $"{plan.Did} automatically",
                    $"Because {entry.Title} for {Minutes(now - entry.ClockFrom)}. Attempt {reserved.Attempts} of {remediation.MaxAttempts}. " +
                    $"{result.Message}".Trim()), ct);
                return [];
            }

            // It ran and failed. That is an attempt used, and judged at once.
            var message = result?.Message ?? "it did not run";
            var gaveUp = reserved.Attempts >= remediation.MaxAttempts;
            await store.SaveStateAsync(reserved with
            {
                Did = plan.Did,
                CheckAt = null,
                Outcome = RemediationOutcomes.Failed,
                GaveUp = gaveUp,
            }, ct);
            await RecordAsync(new Change(now, ChangeKinds.Remediation, ChangeActions.Failed, connection.Id, Subject(remediation),
                $"Could not {plan.Doing}", $"{message} ({entry.Title})"), ct);

            return gaveUp
                ? [() => GiveUpAsync(remediation, entry,
                    $"LabbyTwo tried to {plan.Doing} at {at}, but it failed: {message.TrimEnd('.')}.", now, ct)]
                : [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The check came due and the alert is still firing. Records it, and gives up if that was the last attempt.</summary>
    private async Task<IReadOnlyList<Func<Task>>> NotHelpedAsync(
        Remediation remediation, FiringAlert entry, Connection connection, RemediationState state, DateTimeOffset now, CancellationToken ct)
    {
        var gaveUp = state.Attempts >= remediation.MaxAttempts;
        await store.SaveStateAsync(state with { CheckAt = null, Outcome = RemediationOutcomes.NotHelped, GaveUp = gaveUp }, ct);

        var did = state.Did.Length > 0 ? state.Did : "Its action ran";
        var at = state.LastRunAt is { } last ? Clock(last) : "";
        var next = gaveUp
            ? remediation.IfNotFixed == IfNotFixed.Escalate ? " Out of attempts, so it was escalated." : " Out of attempts, so you were told."
            : $" It will try again {remediation.CooldownMinutes} min after the last try.";
        await RecordAsync(new Change(now, ChangeKinds.Remediation, ChangeActions.NotHelped, connection.Id, Subject(remediation),
            $"{did}, but it did not help",
            $"{entry.Title} was still firing {Minutes(now - (state.LastRunAt ?? now))} later.{next}"), ct);

        return gaveUp
            ? [() => GiveUpAsync(remediation, entry, $"{did} at {at} — it didn't help.", now, ct)]
            : [];
    }

    /// <summary>
    /// Out of attempts and still broken: tell somebody, the way the remediation asked — on
    /// the alert's own channels, or as an escalation. Either way held by what holds any alert.
    /// </summary>
    private async Task GiveUpAsync(Remediation remediation, FiringAlert entry, string what, DateTimeOffset now, CancellationToken ct)
    {
        var alert = new Alert(AlertLevel.Down,
            $"Still firing · {entry.Title}",
            $"{what} It is still firing, and LabbyTwo will not try again for this outage.")
        {
            Tag = entry.Key,
            Link = entry.Link,
        };

        if (remediation.IfNotFixed == IfNotFixed.Escalate)
        {
            await alerts.EscalateNowAsync(entry.Key, alert, now, ct);
            return;
        }

        if (await config.ConnectionAsync(entry.ConnectionId, ct) is not { } connection
            || await alerts.SuppressedAsync(connection, false, now, ct) is not null
            || alerts.MutedBy(entry.RuleId, connection.Id, now) is not null)
            return;

        var channel = entry.RuleId is { } ruleId ? (await rules.GetAsync(ruleId, ct))?.ChannelId : null;
        await alerts.BroadcastAsync(alert, channel is { Length: > 0 } ? [channel] : null, now, ct);
    }

    /// <summary>
    /// Something holds the remediation back. Written to the feed once per reason per outage
    /// rather than every sweep: "did not restart plex: maintenance" is worth reading once.
    /// </summary>
    private async Task HeldAsync(
        Remediation remediation, FiringAlert entry, Connection connection, RemediationState? state, string why, DateTimeOffset now, CancellationToken ct)
    {
        if (state?.Note == why)
            return;

        await store.SaveStateAsync((state ?? new RemediationState(entry.Key, now, 0, null, null, "", "", "", false)) with { Note = why }, ct);
        var doing = await SafeDescribeAsync(remediation, ct);
        await RecordAsync(new Change(now, ChangeKinds.Remediation, ChangeActions.Skipped, connection.Id, Subject(remediation),
            $"Did not {doing}", $"Held back: {why}. ({entry.Title})"), ct);
        log.LogInformation("Self-healing held for {Title}: {Why}", entry.Title, why);
    }

    /// <summary>The alert cleared with a run waiting on its verdict: it helped.</summary>
    private async Task<string?> HelpedAsync(
        RemediationState state, string connectionId, IReadOnlyList<Connection> connections, DateTimeOffset now, CancellationToken ct)
    {
        await store.SaveStateAsync(state with { CheckAt = null, Outcome = RemediationOutcomes.Helped }, ct);

        var name = connections.FirstOrDefault(c => c.Id == connectionId)?.Name ?? "it";
        var did = state.Did.Length > 0 ? state.Did : "Its action ran";
        var at = state.LastRunAt is { } last ? Clock(last) : "";
        await RecordAsync(new Change(now, ChangeKinds.Remediation, ChangeActions.Helped, connectionId, "",
            $"{did} — {name} recovered",
            state.LastRunAt is { } ran ? $"Cleared {Minutes(now - ran)} after the run." : ""), ct);
        return $"{did} at {at} — it recovered.";
    }

    // ---- IAlertNotes --------------------------------------------------------------

    /// <summary>"LabbyTwo will try: restart plex in 5 min." — or that it already has, for an alert back inside its cooldown.</summary>
    public async Task<string?> FiringNoteAsync(Connection connection, FiringAlert entry, DateTimeOffset now, CancellationToken ct)
    {
        var remediation = await store.GetAsync(Remediation.TriggerFor(entry), ct);
        if (remediation is not { Enabled: true, IsComplete: true })
            return null;
        if (!RemediationSettings.From(await settings.AllAsync(ct)).Enabled)
            return null;

        await store.LoadStatesAsync(ct);
        var state = RemediationRules.Current(remediation, entry, store.State(entry.Key));
        var doing = await SafeDescribeAsync(remediation, ct);

        if (state is { LastRunAt: { } last } && (state.GaveUp || state.Attempts >= remediation.MaxAttempts))
            return $"LabbyTwo already tried to {doing} at {Clock(last)} and will not try again for this outage.";

        return $"LabbyTwo will try: {doing} {RemediationRules.When(remediation, entry, now)}.";
    }

    /// <summary>The alert cleared: if a run was waiting on its verdict, it helped — say so on the recovery.</summary>
    public async Task<string?> ClearedNoteAsync(Connection connection, string key, FiringAlert? entry, DateTimeOffset now, CancellationToken ct)
    {
        await store.LoadStatesAsync(ct);
        if (store.State(key) is not { CheckAt: not null })
            return null;

        await _gate.WaitAsync(ct);
        try
        {
            return store.State(key) is { CheckAt: not null } state
                ? await HelpedAsync(state, connection.Id, [connection], now, ct)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- for the page -------------------------------------------------------------

    /// <summary>The automatic actions of the last <paramref name="days"/> days, newest first.</summary>
    public Task<IReadOnlyList<Change>> RecentAsync(int days, int limit, DateTimeOffset now, CancellationToken ct = default) =>
        changes.QueryAsync(new ChangeQuery(now.AddDays(-days), now.AddSeconds(1), [ChangeKinds.Remediation], Limit: limit), ct);

    // ---- helpers ------------------------------------------------------------------

    /// <summary>Runs counted against the hourly cap: every one that actually ran, whether it worked or not.</summary>
    private async Task<int> RunsInLastHourAsync(DateTimeOffset now, CancellationToken ct)
    {
        var recent = await changes.QueryAsync(
            new ChangeQuery(now.AddHours(-1), now.AddSeconds(1), [ChangeKinds.Remediation], Limit: 500), ct);
        return recent.Count(c => c.Action is ChangeActions.Remediated or ChangeActions.Failed);
    }

    private async Task RecordAsync(Change change, CancellationToken ct)
    {
        try
        {
            await changes.RecordAsync(change, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The feed is the record, not the mechanism: losing a row must not stop a verdict.
            log.LogWarning(ex, "Could not record \"{Title}\" in the change feed", change.Title);
        }
    }

    private async Task<string> SafeDescribeAsync(Remediation remediation, CancellationToken ct)
    {
        try
        {
            return await actions.DescribeAsync(remediation, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "Could not describe a remediation");
            return "run its action";
        }
    }

    private static string Subject(Remediation remediation) =>
        remediation.Kind == RemediationKind.RestartContainer ? remediation.Container : remediation.ActionId;

    /// <summary>The trigger a stored alert key answers to: <c>status:{c}</c> → <c>down:{c}</c>, <c>rule:{r}:{c}</c> → <c>rule:{r}</c>.</summary>
    public static string TriggerOf(string alertKey)
    {
        var parts = alertKey.Split(':', 3);
        return parts switch
        {
            ["status", var connection] => Remediation.DownTrigger(connection),
            ["rule", var rule, _] => Remediation.RuleTrigger(rule),
            _ => "",
        };
    }

    private static string ConnectionOf(string alertKey)
    {
        var parts = alertKey.Split(':', 3);
        return parts switch
        {
            ["status", var connection] => connection,
            ["rule", _, var connection] => connection,
            _ => "",
        };
    }

    /// <summary>A time as the notices say it, in the zone the alert service reads mute windows in.</summary>
    private string Clock(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, alerts.Zone).ToString("HH:mm");

    private static string Minutes(TimeSpan span) =>
        span.TotalMinutes < 1 ? "under a minute" : $"{Math.Round(span.TotalMinutes):0} min";

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
