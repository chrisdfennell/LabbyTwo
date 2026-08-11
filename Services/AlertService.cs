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
    AppSettingsStore settings,
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

        if (await SuppressedAsync(change.Connection, change.IsUp, CancellationToken.None) is { } reason)
        {
            log.LogInformation("Alert for {Connection} not sent: {Reason}", change.Connection.Name, reason);
            return;
        }

        var alert = change.IsUp
            ? new Alert(AlertLevel.Up, $"{change.Connection.Name} is back",
                change.PreviousDuration is { } down
                    ? $"Recovered after {Humanise(down)} down."
                    : "Recovered.")
            : new Alert(AlertLevel.Down, $"{change.Connection.Name} is down", change.Message);

        await BroadcastAsync(alert, CancellationToken.None);
    }

    /// <summary>
    /// Why this connection should not be interrupting anyone right now, or null if it may.
    /// One place, because status changes and threshold rules must agree — a rule that
    /// alerted while a connection was silenced would make silencing worthless.
    /// </summary>
    public async Task<string?> SuppressedAsync(Connection connection, bool isRecovery, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;

        if (connection.IsSilenced(now))
            return $"silenced until {connection.SilencedUntil:HH:mm}";

        // Ten services behind one VPN going quiet is one fault, not eleven. The parent's
        // own alert still goes out, and it is the one that names the actual cause.
        if (connection.DependsOn is { Length: > 0 } parentId
            && await config.ConnectionAsync(parentId, ct) is { } parent
            && monitor.State(parent.Id) is { IsUp: false })
        {
            return $"{parent.Name}, which it depends on, is down";
        }

        return null;
    }

    /// <summary>Whether the hour allows this alert. Separate from suppression: it is about when, not what.</summary>
    public async Task<bool> AllowedNowAsync(Alert alert, CancellationToken ct)
    {
        var policy = AlertPolicy.From(await settings.AllAsync(ct));
        return policy.Allows(alert, DateTimeOffset.Now);
    }

    /// <summary>
    /// Sends to every enabled alert channel, or to one when a rule names it. Used by status
    /// changes and threshold rules alike.
    /// </summary>
    public async Task<int> BroadcastAsync(Alert alert, CancellationToken ct, string? channelId = null)
    {
        if (!await AllowedNowAsync(alert, ct))
        {
            log.LogInformation("Quiet hours: not sending \"{Title}\"", alert.Title);
            return 0;
        }

        var channels = await ChannelsAsync(ct);

        if (channelId is { Length: > 0 })
        {
            // A rule pointing at a channel that has since been deleted should shout through
            // whatever is left rather than going quiet — losing an alert is worse than
            // sending it somewhere unexpected.
            var chosen = channels.Where(pair => pair.Connection.Id == channelId).ToList();
            if (chosen.Count > 0)
                channels = chosen;
            else
                log.LogWarning("A rule names a channel that no longer exists; sending to all instead.");
        }

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
