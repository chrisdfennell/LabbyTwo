using System.Net;
using System.Net.Sockets;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// The family status page's server side: its settings, the view it draws, and the
/// "something's broken" reports it takes in.
///
/// Everything the anonymous page learns comes through <see cref="ViewAsync"/>, which builds
/// a <see cref="FamilyView"/> — names the owner typed, four states, and when each began. It
/// never hands the page a <see cref="Connection"/> or a probe result, so nothing the monitor
/// knows about a host, a port or an error can end up in the markup by accident.
/// </summary>
public sealed class FamilyStatus(
    AppSettingsStore settings,
    ConfigStore config,
    HealthMonitor monitor,
    FamilyReportStore reports,
    ChangeStore changes,
    AlertService alerts,
    Offload offload,
    ILogger<FamilyStatus> log)
{
    /// <summary>What a report's "what's broken" is when it is none of the listed things.</summary>
    public const string SomethingElse = "other";

    public const string SomethingElseName = "Something else";

    public async Task<FamilyStatusSettings> SettingsAsync(CancellationToken ct = default) =>
        FamilyStatusSettings.From(await offload.Run(c => settings.AllAsync(c), ct));

    public Task SaveAsync(FamilyStatusSettings value, CancellationToken ct = default) =>
        offload.Run(async c =>
        {
            await settings.SaveAsync(FamilyStatusSettings.Key, value.ToJson(), c);
            return true;
        }, ct);

    /// <summary>The page as it stands right now.</summary>
    public async Task<FamilyView> ViewAsync(FamilyStatusSettings family, CancellationToken ct = default)
    {
        var (bag, connections) = await offload.Run(async c =>
            (await settings.AllAsync(c), await config.ConnectionsAsync(c)), ct);

        var now = DateTimeOffset.Now;
        var byId = connections.ToDictionary(c => c.Id);
        return FamilyView.Build(
            family,
            byId,
            id => monitor.State(id) is { } state
                ? new FamilyView.Probe(state.IsUp, state.ConsecutiveFailures > 0, state.ChangedAt)
                : null,
            Maintenance.From(bag, now),
            now);
    }

    /// <summary>What happened to a report.</summary>
    public enum Outcome
    {
        Sent,

        /// <summary>Recorded, but no notification: maintenance is on, or the thing is silenced.</summary>
        Held,

        /// <summary>The item chosen is not on the page.</summary>
        Unknown,
    }

    /// <summary>
    /// Records a report, puts it in the change feed, and tells the owner through the alert
    /// channels. The rate limits are the caller's: this is the part after a report has been
    /// allowed.
    ///
    /// Maintenance and a silenced connection hold the notification — the owner is already
    /// working on it — but the report is still kept and shown. Quiet hours are left to
    /// <see cref="AlertService.BroadcastAsync(Alert, CancellationToken, string?)"/>, as for
    /// every other non-urgent alert: "the TV isn't working" is not worth waking anybody for,
    /// and it will be waiting on the dashboard in the morning.
    ///
    /// The notification is sent in the background. Whoever pressed the button should be
    /// thanked straight away, not kept waiting on a slow webhook.
    /// </summary>
    public async Task<Outcome> ReportAsync(
        FamilyStatusSettings family, string? itemId, string? message, string? reporter, CancellationToken ct = default)
    {
        FamilyItem? item = null;
        if (itemId != SomethingElse)
        {
            item = family.Items.FirstOrDefault(i => i.Id == itemId);
            if (item is null)
                return Outcome.Unknown;
        }

        var text = FamilyText.Clean(message, FamilyText.MaxMessage);
        var who = FamilyText.Clean(reporter, FamilyText.MaxReporter);
        var what = item is null ? SomethingElseName : item.Name.Length > 0 ? item.Name : "Unnamed";
        var now = DateTimeOffset.Now;

        var (bag, connection) = await offload.Run(async c =>
            (await settings.AllAsync(c), item is null ? null : await config.ConnectionAsync(item.ConnectionId, c)), ct);

        var report = await offload.Run(c => reports.AddAsync(
            new FamilyReport(0, now, item?.Id ?? SomethingElse, connection?.Id, what, text, who), c), ct);

        var title = item is null
            ? $"{(who.Length > 0 ? who : "Someone")} reported something isn't working"
            : $"{(who.Length > 0 ? who : "Someone")} reported {what} isn't working";

        try
        {
            await offload.Run(c => changes.RecordAsync(new Change(
                now, ChangeKinds.Report, ChangeActions.Reported, connection?.Id,
                report.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), title, text), c), ct);
        }
        catch (Exception ex)
        {
            // The report itself is stored; losing its line in the feed is not worth an error page.
            log.LogWarning(ex, "Could not record a family report in the change feed");
        }

        var held = Maintenance.From(bag, now).On || connection?.IsSilenced(now) == true;
        if (held)
        {
            log.LogInformation("Family report about {Item} kept without a notification: maintenance or silenced", what);
            return Outcome.Held;
        }

        var alert = Notification(what, text, who);
        _ = Task.Run(async () =>
        {
            try
            {
                await alerts.BroadcastAsync(alert, CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not send the notification for a family report");
            }
        }, CancellationToken.None);

        return Outcome.Sent;
    }

    /// <summary>
    /// The alert a report sends. What the family typed goes in as text: escaped for the
    /// channels that render Markdown, and with @-mentions and Slack's angle-bracket links
    /// broken for the ones that do not, so a message cannot ping a whole server or become a link.
    /// </summary>
    public static Alert Notification(string what, string message, string reporter)
    {
        var who = reporter.Length > 0 ? reporter : "Someone";
        var title = FamilyText.ForPlainNotification($"{who} says {what} isn't working");

        var plain = message.Length > 0
            ? $"“{FamilyText.ForPlainNotification(message)}”"
            : "No message.";
        var markdown = message.Length > 0
            ? $"“{FamilyText.ForMarkdownNotification(message)}”"
            : "No message.";

        return new Alert(AlertLevel.Info, title, plain + "\nSent from the family status page.")
        {
            Markdown = markdown + "\nSent from the family status page.",
            Tag = "family-report",
            Link = "settings#family",
        };
    }

    /// <summary>
    /// Whether a request is from the home network, for <c>/family</c> without a token.
    ///
    /// The address is the one ASP.NET settled on after the forwarded-header middleware, which
    /// believes only the proxies in Labby:Proxy:TrustedProxies. That alone is not enough: a
    /// Cloudflare tunnel or a reverse proxy that is not listed as trusted makes every visitor
    /// from the internet arrive from the proxy's own private address. So a request still
    /// carrying any forwarding header — one the middleware did not consume, or any of
    /// Cloudflare's — is never taken for a local one. Being wrong here costs a family member
    /// using the link instead; being wrong the other way publishes the page.
    /// </summary>
    public static bool IsLocal(HttpContext context)
    {
        var headers = context.Request.Headers;
        foreach (var header in ProxyHeaders)
            if (headers.ContainsKey(header))
                return false;

        return IsPrivate(context.Connection.RemoteIpAddress);
    }

    private static readonly string[] ProxyHeaders =
    [
        "X-Forwarded-For", "Forwarded", "X-Real-IP",
        "CF-Connecting-IP", "CF-Ray", "CF-IPCountry", "True-Client-IP", "Cdn-Loop",
    ];

    /// <summary>Loopback, RFC 1918, carrier-grade NAT, link-local, and IPv6 unique-local.</summary>
    public static bool IsPrivate(IPAddress? address)
    {
        if (address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
    }
}
