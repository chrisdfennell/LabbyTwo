using System.Collections.Concurrent;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Evaluates threshold rules after every probe sweep and sends through the same channels
/// up/down alerting uses. It knows nothing about disks or temperatures — it compares
/// numbers a provider happened to report, which is why one rule covers every integration,
/// including ones written after it.
/// </summary>
public sealed class MetricAlertService(
    AlertRuleStore rules,
    ConfigStore config,
    Registry registry,
    HealthMonitor monitor,
    AlertService alerts,
    AppSettingsStore appSettings,
    CapacityForecasts forecasts,
    ILogger<MetricAlertService> log) : IHostedService
{
    /// <summary>
    /// Live state per rule *and* connection: a rule with no connection watches many, and
    /// each has to breach and clear on its own.
    /// </summary>
    public sealed record Breach(string RuleId, string ConnectionId, DateTimeOffset? Since, bool Firing, double LastValue);

    private readonly ConcurrentDictionary<string, Breach> _breaches = new();

    /// <summary>
    /// One evaluation at a time. A pass reads a breach, awaits the send, and then writes
    /// it; two passes interleaved both read "not firing" and both send. Sweeps finishing
    /// close together are ordinary — "Test all" is a burst of them — so this is not a
    /// theoretical race.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Set when a sweep finishes, cleared by the pass that picks it up. A sweep that lands
    /// while a pass is running is not dropped: the running pass goes round once more, so
    /// the newest readings are always evaluated without a queue of passes that would all
    /// see the same thing.
    /// </summary>
    private int _pending;

    private static string Key(string ruleId, string connectionId) => $"{ruleId}|{connectionId}";

    /// <summary>Every rule/connection pair being tracked, with its last reading.</summary>
    public IReadOnlyCollection<Breach> All => [.. _breaches.Values];

    /// <summary>What is currently alerting, for the UI.</summary>
    public IReadOnlyCollection<Breach> Firing => [.. _breaches.Values.Where(b => b.Firing)];

    public bool IsFiring(string ruleId) => _breaches.Values.Any(b => b.Firing && b.RuleId == ruleId);

    /// <summary>Fires after an evaluation pass so a widget showing active alerts can refresh.</summary>
    public event Action? Updated;

    public Task StartAsync(CancellationToken ct)
    {
        monitor.Updated += OnSweepCompleted;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        monitor.Updated -= OnSweepCompleted;
        return Task.CompletedTask;
    }

    private void OnSweepCompleted() => _ = EvaluateSafelyAsync();

    private async Task EvaluateSafelyAsync()
    {
        Interlocked.Exchange(ref _pending, 1);

        // WaitAsync(0) rather than waiting: if a pass is already running it will see the
        // flag and go round again, so there is nothing for this caller to queue behind.
        // The outer loop covers the moment between that pass's last look at the flag and
        // its releasing the gate, when a sweep could otherwise set the flag and find the
        // gate still held, and be forgotten.
        while (Volatile.Read(ref _pending) == 1 && await _gate.WaitAsync(0))
        {
            try
            {
                while (Interlocked.Exchange(ref _pending, 0) == 1)
                {
                    try
                    {
                        await EvaluatePassAsync(DateTimeOffset.Now, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        // The sweep already completed and recorded history; a rule bug must
                        // not surface as an unobserved task exception.
                        log.LogError(ex, "Evaluating alert rules failed");
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// One evaluation pass. <paramref name="now"/> is a parameter so the sustain window
    /// can be tested without waiting minutes for it. Waits its turn behind any pass
    /// already running, for the same reason the sweep-driven ones do.
    /// </summary>
    public async Task EvaluateAsync(DateTimeOffset now, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EvaluatePassAsync(now, ct);
        }
        finally
        {
            _gate.Release();
        }

        // A sweep that finished while this held the gate found it busy and left the flag
        // for whoever held it — which was this, and this does not look at the flag.
        if (Volatile.Read(ref _pending) == 1)
            _ = EvaluateSafelyAsync();
    }

    private async Task EvaluatePassAsync(DateTimeOffset now, CancellationToken ct)
    {
        var active = await rules.AllAsync(ct);
        var connections = await config.ConnectionsAsync(ct);

        // Every rule/connection pair that is still meant to be watched, whether or not it
        // had a reading this time. Kept separate from "had a reading" on purpose: a probe
        // that failed carries no metrics, and treating that as "this pair no longer exists"
        // threw away a firing breach on one dropped packet — so the next good reading
        // fired it again as if new, and a recovery during the gap was never announced.
        var watched = new HashSet<string>();

        foreach (var rule in active.Where(r => r.Enabled && r.Metric.Length > 0))
        {
            var targets = rule.ConnectionId is null
                ? connections.Where(monitor.IsMonitored)
                : connections.Where(c => c.Id == rule.ConnectionId);

            foreach (var connection in targets)
            {
                // A muted connection stays muted for thresholds too — one switch, not two.
                if (!connection.AlertsEnabled)
                    continue;

                var key = Key(rule.Id, connection.Id);
                watched.Add(key);

                if (!TryReading(connection.Id, rule.Metric, out var value))
                {
                    // No reading, so no news: a firing breach stays firing, and will clear
                    // (and say so) when a reading says it has. A breach still inside its
                    // sustain window starts the window again, though — minutes in which
                    // nobody could see the value are not minutes it was seen to hold.
                    if (_breaches.TryGetValue(key, out var waiting) && waiting is { Firing: false, Since: not null })
                        _breaches[key] = waiting with { Since = null };
                    continue;
                }

                await ApplyAsync(rule, connection, value, now, ct);
            }
        }

        // Forget rules and connections that went away — deleted, disabled, muted, or no
        // longer a target — so a deleted rule does not keep showing as firing and a
        // recreated one starts its sustain window fresh.
        foreach (var key in _breaches.Keys.Where(k => !watched.Contains(k)))
            _breaches.TryRemove(key, out _);

        Updated?.Invoke();
    }

    /// <summary>
    /// The number a rule compares. A measured metric comes from the monitor's last probe; a
    /// forecast (<c>days_until_full:…</c>) from <see cref="CapacityForecasts"/>, which works
    /// them out on its own timer. Either way a missing value means "no reading", so a volume
    /// that has not got enough history for a forecast is treated like a probe that failed —
    /// no news — rather than as a breach or a recovery.
    /// </summary>
    private bool TryReading(string connectionId, string metric, out double value)
    {
        if (CapacityMetric.TryParse(metric, out _))
            return forecasts.TryGetValue(connectionId, metric, out value);

        value = 0;
        return monitor.State(connectionId)?.Metrics.TryGetValue(metric, out value) == true;
    }

    private async Task ApplyAsync(AlertRule rule, Connection connection, double value, DateTimeOffset now, CancellationToken ct)
    {
        var key = Key(rule.Id, connection.Id);
        var previous = _breaches.GetValueOrDefault(key);
        var spec = registry.Metric(connection, rule.Metric);

        if (rule.IsBreaching(value))
        {
            var since = previous?.Since ?? now;
            var sustained = now - since >= TimeSpan.FromMinutes(Math.Max(0, rule.ForMinutes));
            var firing = previous?.Firing ?? false;

            // The new state is written before the send is awaited, not after. The send is
            // the slow part — a webhook, an SMTP server — and anything that reads this
            // breach in the meantime has to see that the alert is already on its way.
            _breaches[key] = new Breach(rule.Id, connection.Id, since, firing || sustained, value);

            if (!firing && sustained)
                await SendAsync(rule, connection, spec, value, AlertLevel.Down, ct);
            return;
        }

        if (rule.IsCleared(value))
        {
            _breaches[key] = new Breach(rule.Id, connection.Id, null, false, value);

            if (previous?.Firing == true)
                await SendAsync(rule, connection, spec, value, AlertLevel.Up, ct);
            return;
        }

        // Between the two thresholds: hold whatever it was doing, but keep the value
        // fresh so the UI shows the real reading.
        _breaches[key] = previous is null
            ? new Breach(rule.Id, connection.Id, null, false, value)
            : previous with { LastValue = value };
    }

    private async Task SendAsync(
        AlertRule rule, Connection connection, MetricSpec spec, double value, AlertLevel level, CancellationToken ct)
    {
        // A notification saying "-6.0°C" to someone who thinks in Fahrenheit is a puzzle
        // rather than a warning, so the message follows the same setting the UI does.
        var system = Units.Preferences.From(await appSettings.AllAsync(ct));
        var reading = Units.Format(spec, value, system, spec.Decimals == 0 && Math.Abs(value) < 100 ? 1 : spec.Decimals);
        var limit = Units.Format(spec, level == AlertLevel.Down ? rule.Threshold : rule.ClearsAt, system);

        // A forecast is said in words. "Days until full is 23.4 days" claims a precision two
        // weeks of history does not have, and a recovery to "∞ days" is not a sentence.
        var forecast = CapacityMetric.TryParse(rule.Metric, out var measured)
            ? forecasts.Get(connection.Id, measured)
            : null;

        var alert = forecast is not null
            ? level == AlertLevel.Down
                ? new Alert(AlertLevel.Down,
                    $"{connection.Name} · {registry.Metric(connection, measured).Label} {forecast.Describe()}",
                    $"At the current rate. {forecast.Explain()} This rule warns under {limit}.")
                : new Alert(AlertLevel.Up,
                    $"{connection.Name} · {registry.Metric(connection, measured).Label} is no longer close to full",
                    $"Now {forecast.Describe()}.")
            : level == AlertLevel.Down
            ? new Alert(AlertLevel.Down,
                $"{connection.Name} · {spec.Label} is {reading}",
                $"{spec.Label} went {rule.ComparisonWord} {limit}" +
                (rule.ForMinutes > 0 ? $" and stayed there for {rule.ForMinutes} minute(s)." : "."))
            : new Alert(AlertLevel.Up,
                $"{connection.Name} · {spec.Label} is back to {reading}",
                $"{spec.Label} returned past {limit}.");

        log.Log(level == AlertLevel.Down ? LogLevel.Warning : LogLevel.Information,
            "Alert rule {Rule} {State} for {Connection}: {Metric} = {Value}",
            rule.Describe(spec.Label, connection.Name),
            level == AlertLevel.Down ? "fired" : "cleared",
            connection.Name, rule.Metric, value);

        // The rule still changes state above — it is only the notification that is held —
        // so the Alerts page keeps showing the truth while a silence is in force.
        if (await alerts.SuppressedAsync(connection, level == AlertLevel.Up, ct) is { } reason)
        {
            log.LogInformation("Alert for {Connection} not sent: {Reason}", connection.Name, reason);
            return;
        }

        await alerts.BroadcastAsync(alert, ct, rule.ChannelId);
    }
}
