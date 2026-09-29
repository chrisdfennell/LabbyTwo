using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Fans a status change out to every configured alert channel. Knows nothing about
/// Discord or Pushover — it asks the registry which connections are alert channels and
/// hands each one an <see cref="Alert"/>.
///
/// It is also where an alert's life after its first notice is decided: whether a mute
/// window is holding it, whether it has gone unanswered long enough to escalate, and where
/// its recovery has to go. That lives here rather than in either source of alerts because
/// down alerts and rule alerts must follow exactly the same rules, and both already come
/// here to be sent. See AlertService.FollowUp.cs.
/// </summary>
public sealed partial class AlertService(
    ConfigStore config,
    Registry registry,
    HealthMonitor monitor,
    AppSettingsStore settings,
    AlertRuleStore rules,
    MuteWindowStore mutes,
    FiringAlertStore firing,
    ILogger<AlertService> log) : IHostedService
{
    /// <summary>
    /// The zone mute windows and quiet hours are read in: the machine's, as everywhere else
    /// in LabbyTwo. Settable so a test can use one whose clocks change on known dates.
    /// </summary>
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

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

        var now = DateTimeOffset.Now;
        var alert = change.IsUp
            ? new Alert(AlertLevel.Up, $"{change.Connection.Name} is back",
                change.PreviousDuration is { } down
                    ? $"Recovered after {Humanise(down)} down."
                    : "Recovered.")
            : new Alert(AlertLevel.Down, $"{change.Connection.Name} is down", change.Message);

        // Down and back share a tag, so a channel that can replace a notification shows
        // whichever is true now rather than both.
        alert = alert with { Tag = FiringAlert.StatusKey(change.Connection.Id), Link = "settings/connections" };

        if (change.IsUp)
            await ClearedAsync(change.Connection, null, alert, null, now, CancellationToken.None);
        else
            await FiredAsync(change.Connection, null, alert, null, now, 0, now, CancellationToken.None);
    }

    /// <summary>
    /// Why this connection should not be interrupting anyone right now, or null if it may.
    /// One place, because status changes and threshold rules must agree — a rule that
    /// alerted while a connection was silenced would make silencing worthless.
    /// </summary>
    public Task<string?> SuppressedAsync(Connection connection, bool isRecovery, CancellationToken ct) =>
        SuppressedAsync(connection, isRecovery, DateTimeOffset.Now, ct);

    /// <summary><see cref="SuppressedAsync(Connection, bool, CancellationToken)"/> at a given moment.</summary>
    public async Task<string?> SuppressedAsync(Connection connection, bool isRecovery, DateTimeOffset now, CancellationToken ct)
    {
        // First, because it is the one that outranks everything: a maintenance window is
        // somebody saying they already know. Checked here rather than beside quiet hours
        // in BroadcastAsync so that it also silences the recovery, and so the log says
        // which connection was held rather than only which alert.
        if (Maintenance.From(await settings.AllAsync(ct), now) is { On: true } maintenance)
            return maintenance.Reason;

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
    public Task<bool> AllowedNowAsync(Alert alert, CancellationToken ct) => AllowedAtAsync(alert, DateTimeOffset.Now, ct);

    private async Task<bool> AllowedAtAsync(Alert alert, DateTimeOffset now, CancellationToken ct)
    {
        var policy = AlertPolicy.From(await settings.AllAsync(ct));
        return policy.Allows(alert, now, Zone);
    }

    /// <summary>
    /// Sends to every enabled alert channel, or to one when a rule names it. Used by status
    /// changes and threshold rules alike.
    /// </summary>
    public Task<int> BroadcastAsync(Alert alert, CancellationToken ct, string? channelId = null) =>
        BroadcastAsync(alert, channelId is { Length: > 0 } ? [channelId] : null, DateTimeOffset.Now, ct);

    /// <summary>
    /// Sends to the chosen channels — null or empty is every one — if the hour allows it.
    /// A set rather than one channel for the recovery of an escalated alert, which goes to
    /// wherever the alert went first and everywhere it was escalated to.
    /// </summary>
    public async Task<int> BroadcastAsync(Alert alert, IReadOnlyCollection<string>? channelIds, DateTimeOffset now, CancellationToken ct)
    {
        if (!await AllowedAtAsync(alert, now, ct))
        {
            log.LogInformation("Quiet hours: not sending \"{Title}\"", alert.Title);
            return 0;
        }

        return await SendToAsync(alert, channelIds, ct);
    }

    /// <summary>
    /// The send itself, with no question of whether it should happen: every caller has
    /// already decided that.
    /// </summary>
    private async Task<int> SendToAsync(Alert alert, IReadOnlyCollection<string>? channelIds, CancellationToken ct)
    {
        var channels = await ChannelsAsync(ct);

        if (channelIds is { Count: > 0 })
        {
            // A rule pointing at a channel that has since been deleted should shout through
            // whatever is left rather than going quiet — losing an alert is worse than
            // sending it somewhere unexpected.
            var chosen = channels.Where(pair => channelIds.Contains(pair.Connection.Id)).ToList();
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
