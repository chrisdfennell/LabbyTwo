using LabbyTwo.Core;
using LabbyTwo.Services.WebPush;
using LabbyTwo.Storage;

namespace LabbyTwo.Providers;

/// <summary>
/// Notifications straight from LabbyTwo to a phone or a desktop browser, through the Web
/// Push service that browser already talks to — no Pushover account, no ntfy server. The
/// devices themselves are added from Alerts → Browser push, on each device; this channel
/// only says which of them it speaks to.
///
/// Quiet hours, silences and maintenance are not this class's business: AlertService
/// decides whether an alert goes out at all before any channel sees it, exactly as it does
/// for a webhook.
/// </summary>
public sealed class BrowserPushProvider(IServiceProvider services) : IAlertChannel
{
    public const string ProviderType = "browser-push";

    public string Type => ProviderType;
    public string DisplayName => "Browser push (this app)";
    public string Icon => "📳";
    public string Category => "Alerts";
    public string Description =>
        "Notifications on your phone or desktop straight from LabbyTwo — no third-party account. " +
        "Add each device from Alerts → Browser push, opened on that device.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("devices", "Devices", FieldKind.Text, "Everything subscribed",
            Help: "Leave empty to notify every subscribed device. Or list device names, separated by commas, " +
                  "to send only to those — \"Chris's phone, Office PC\"."),
    ];

    // Resolved when used rather than injected. The registry builds every provider to list
    // them, on every page and in every test that enumerates providers, and none of that
    // should need the database or the data-protection keyring just to learn this one's name.
    private PushSubscriptionStore Store => services.GetRequiredService<PushSubscriptionStore>();
    private WebPushSender Sender => services.GetRequiredService<WebPushSender>();

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var devices = Chosen(connection, await Store.AllAsync(ct));
        return devices.Count > 0
            ? ProbeResult.Up(TimeSpan.Zero, $"Ready — {devices.Count} device(s)")
            : ProbeResult.Down(TimeSpan.Zero, "No subscribed device matches. Add one from Alerts → Browser push.");
    }

    public async Task SendAsync(Connection channel, Alert alert, CancellationToken ct)
    {
        var devices = Chosen(channel, await Store.AllAsync(ct));
        if (devices.Count == 0)
            throw new InvalidOperationException(
                "No device is subscribed to this channel. Open Alerts → Browser push on your phone " +
                "or computer and press Notify this device.");

        var outcomes = await Sender.SendAsync(devices, Message(alert), ct);

        // One phone that has gone away must not make the channel look broken while the
        // other devices are getting everything, so it is only an error when nothing arrived.
        if (outcomes.Any(o => o.Result == PushResult.Delivered))
            return;

        throw new InvalidOperationException(string.Join(" ", outcomes.Select(o => $"{o.Device.Name}: {o.Message}")));
    }

    /// <summary>
    /// How an alert looks on a lock screen. An urgent alert — a tornado warning — stays until
    /// it is dismissed and sounds again even if one is already showing. So does anything going
    /// down: it replaces the notification it shares a tag with, and a replacement is silent
    /// unless told otherwise. A recovery or a summary replaces quietly.
    /// </summary>
    public static PushMessage Message(Alert alert)
    {
        var loud = alert.Urgent || alert.Level == AlertLevel.Down;
        return new PushMessage(
            Title: $"{alert.Emoji} {alert.Title}",
            Body: alert.Body,
            Tag: alert.Tag ?? alert.Title,
            Url: alert.Link ?? "/",
            Urgent: alert.Urgent,
            Renotify: loud,
            // "high" is what a push service will wake a phone in battery saver for. Nothing
            // else is worth that, but "normal" still arrives promptly on a phone in use.
            Urgency: loud ? "high" : "normal",
            // How long a push service keeps a message for a phone that is switched off. A
            // warning a day late is still worth seeing; an "is down" from last night is not.
            TimeToLive: alert.Urgent || alert.Level == AlertLevel.Info ? TimeSpan.FromHours(24) : TimeSpan.FromHours(6));
    }

    /// <summary>Every device, or the ones the channel names — matched without regard to case or spacing.</summary>
    public static IReadOnlyList<PushSubscription> Chosen(Connection channel, IReadOnlyList<PushSubscription> all)
    {
        var names = channel.Settings.Get("devices")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0)
            return all;

        return [.. all.Where(device => names.Contains(device.Name.Trim(), StringComparer.OrdinalIgnoreCase))];
    }
}
