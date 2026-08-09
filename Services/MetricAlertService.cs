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
    ILogger<MetricAlertService> log) : IHostedService
{
    /// <summary>
    /// Live state per rule *and* connection: a rule with no connection watches many, and
    /// each has to breach and clear on its own.
    /// </summary>
    public sealed record Breach(string RuleId, string ConnectionId, DateTimeOffset? Since, bool Firing, double LastValue);

    private readonly ConcurrentDictionary<string, Breach> _breaches = new();

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
        try
        {
            await EvaluateAsync(DateTimeOffset.Now, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The sweep already completed and recorded history; a rule bug must not
            // surface as an unobserved task exception.
            log.LogError(ex, "Evaluating alert rules failed");
        }
    }

    /// <summary>
    /// One evaluation pass. <paramref name="now"/> is a parameter so the sustain window
    /// can be tested without waiting minutes for it.
    /// </summary>
    public async Task EvaluateAsync(DateTimeOffset now, CancellationToken ct)
    {
        var active = await rules.AllAsync(ct);
        var connections = await config.ConnectionsAsync(ct);
        var live = new HashSet<string>();

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

                var state = monitor.State(connection.Id);
                if (state?.Metrics.TryGetValue(rule.Metric, out var value) != true)
                    continue;

                live.Add(Key(rule.Id, connection.Id));
                await ApplyAsync(rule, connection, value, now, ct);
            }
        }

        // Forget rules and connections that went away, so a deleted rule does not keep
        // showing as firing and a recreated one starts its sustain window fresh.
        foreach (var key in _breaches.Keys.Where(k => !live.Contains(k)))
            _breaches.TryRemove(key, out _);

        Updated?.Invoke();
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

            if (!firing && sustained)
            {
                firing = true;
                await SendAsync(rule, connection, spec, value, AlertLevel.Down, ct);
            }

            _breaches[key] = new Breach(rule.Id, connection.Id, since, firing, value);
            return;
        }

        if (rule.IsCleared(value))
        {
            if (previous?.Firing == true)
                await SendAsync(rule, connection, spec, value, AlertLevel.Up, ct);

            _breaches[key] = new Breach(rule.Id, connection.Id, null, false, value);
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
        var reading = spec.Format(value, spec.Decimals == 0 && Math.Abs(value) < 100 ? 1 : spec.Decimals);
        var limit = spec.Format(level == AlertLevel.Down ? rule.Threshold : rule.ClearsAt);

        var alert = level == AlertLevel.Down
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

        await alerts.BroadcastAsync(alert, ct);
    }
}
