using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Fans a status change out to every configured alert channel. Knows nothing about
/// Discord or Pushover — it asks the registry which connections are alert channels and
/// hands each one an <see cref="Alert"/>.
/// </summary>
public sealed class AlertService(
    ConfigStore config,
    Registry registry,
    HealthMonitor monitor,
    ILogger<AlertService> log) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        monitor.StatusChanged += OnStatusChangedAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        monitor.StatusChanged -= OnStatusChangedAsync;
        return Task.CompletedTask;
    }

    private async Task OnStatusChangedAsync(HealthMonitor.StatusChange change)
    {
        if (!change.Connection.AlertsEnabled)
            return;

        var alert = change.IsUp
            ? new Alert(AlertLevel.Up, $"{change.Connection.Name} is back",
                change.PreviousDuration is { } down
                    ? $"Recovered after {Humanise(down)} down."
                    : "Recovered.")
            : new Alert(AlertLevel.Down, $"{change.Connection.Name} is down", change.Message);

        await BroadcastAsync(alert, CancellationToken.None);
    }

    /// <summary>Sends to every enabled alert channel. Used by status changes and the digest alike.</summary>
    public async Task<int> BroadcastAsync(Alert alert, CancellationToken ct)
    {
        var channels = await ChannelsAsync(ct);
        if (channels.Count == 0)
            return 0;

        // One broken channel must not stop the others, so each send is isolated.
        var results = await Task.WhenAll(channels.Select(async pair =>
        {
            try
            {
                await pair.Channel.SendAsync(pair.Connection, alert, ct);
                return true;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not send an alert through {Channel}", pair.Connection.Name);
                return false;
            }
        }));

        return results.Count(sent => sent);
    }

    /// <summary>Sends one alert through a single channel, surfacing the failure to the caller (the Test button).</summary>
    public async Task SendTestAsync(Connection channel, CancellationToken ct)
    {
        if (registry.Provider(channel.Provider) is not IAlertChannel provider)
            throw new InvalidOperationException($"“{channel.Name}” is not an alert channel.");

        await provider.SendAsync(channel, new Alert(AlertLevel.Info,
            "Test notification",
            "If you are reading this, LabbyTwo can reach this channel."), ct);
    }

    public async Task<IReadOnlyList<(Connection Connection, IAlertChannel Channel)>> ChannelsAsync(CancellationToken ct = default)
    {
        var connections = await config.ConnectionsAsync(ct);
        return
        [
            .. connections
                .Where(c => c.Enabled)
                .Select(c => (Connection: c, Channel: registry.Provider(c.Provider) as IAlertChannel))
                .Where(pair => pair.Channel is not null)
                .Select(pair => (pair.Connection, pair.Channel!))
        ];
    }

    private static string Humanise(TimeSpan span) => span.TotalMinutes switch
    {
        < 1 => $"{span.TotalSeconds:0} seconds",
        < 60 => $"{span.TotalMinutes:0} minutes",
        < 48 * 60 => $"{span.TotalHours:0.#} hours",
        _ => $"{span.TotalDays:0.#} days",
    };
}
