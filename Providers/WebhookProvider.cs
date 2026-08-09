using System.Text;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// One webhook URL covering Discord, Slack and ntfy, because the only real difference is
/// the JSON body — and guessing it from the hostname is right often enough that most
/// people never touch the format field.
/// </summary>
public sealed class WebhookProvider(IHttpClientFactory httpFactory) : IAlertChannel
{
    public string Type => "webhook";
    public string DisplayName => "Webhook (Discord / Slack / ntfy)";
    public string Icon => "🔔";
    public string Category => "Alerts";
    public string Description => "Posts down and recovery notices to a webhook URL. Paste a Discord or Slack webhook, or an ntfy topic URL.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Webhook URL", FieldKind.Url, "https://ntfy.sh/my-homelab", Required: true),
        new("format", "Payload format", FieldKind.Select, Default: "auto", Options:
        [
            new SelectOption("auto", "Detect from the URL (default)"),
            new SelectOption("discord", "Discord"),
            new SelectOption("slack", "Slack"),
            new SelectOption("plain", "Plain text body"),
        ]),
    ];

    /// <summary>
    /// Never touched by the monitor (<see cref="IAlertChannel"/> is unmonitored); this only
    /// answers the connections list, so it reports configured-ness rather than reachability.
    /// A GET would be wrong anyway — an ntfy topic URL streams, and a Discord webhook GET
    /// tells you nothing about whether a POST would be accepted.
    /// </summary>
    public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
        => Task.FromResult(connection.Settings.Get("url").Length > 0
            ? ProbeResult.Up(TimeSpan.Zero, $"Ready — {Describe(connection)}")
            : ProbeResult.Down(TimeSpan.Zero, "No webhook URL configured."));

    public async Task SendAsync(Connection channel, Alert alert, CancellationToken ct)
    {
        var url = channel.Settings.Get("url");
        if (url.Length == 0)
            throw new InvalidOperationException("No webhook URL configured.");

        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        var format = Resolve(channel);

        HttpContent content = format switch
        {
            "discord" => Json(new
            {
                username = "LabbyTwo",
                embeds = new[]
                {
                    new { title = $"{alert.Emoji} {alert.Title}", description = alert.Body, color = alert.Color },
                },
            }),
            "slack" => Json(new
            {
                text = $"{alert.Emoji} *{alert.Title}*",
                attachments = new[]
                {
                    new { text = alert.Body, color = alert.Level == AlertLevel.Down ? "danger" : "good" },
                },
            }),
            // ntfy and every generic receiver: the body is the message, and ntfy reads
            // the title and priority off these headers.
            _ => new StringContent(alert.PlainText, Encoding.UTF8, "text/plain"),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (format == "plain")
        {
            request.Headers.TryAddWithoutValidation("Title", $"LabbyTwo: {alert.Title}");
            request.Headers.TryAddWithoutValidation("Priority", alert.Level == AlertLevel.Down ? "high" : "default");
            request.Headers.TryAddWithoutValidation("Tags", alert.Level == AlertLevel.Down ? "rotating_light" : "white_check_mark");
        }

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"The webhook answered HTTP {(int)response.StatusCode}. {body[..Math.Min(body.Length, 200)]}".Trim());
        }
    }

    private static StringContent Json(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static string Resolve(Connection channel)
    {
        var configured = channel.Settings.Get("format", "auto");
        if (configured != "auto")
            return configured;
        var url = channel.Settings.Get("url");
        if (url.Contains("discord", StringComparison.OrdinalIgnoreCase))
            return "discord";
        if (url.Contains("hooks.slack.com", StringComparison.OrdinalIgnoreCase))
            return "slack";
        return "plain";
    }

    private static string Describe(Connection channel) => Resolve(channel) switch
    {
        "discord" => "Discord format",
        "slack" => "Slack format",
        _ => "plain-text format",
    };
}

/// <summary>Pushover, for a notification that reaches a phone even when nothing is open.</summary>
public sealed class PushoverProvider(IHttpClientFactory httpFactory) : IAlertChannel
{
    public string Type => "pushover";
    public string DisplayName => "Pushover";
    public string Icon => "📲";
    public string Category => "Alerts";
    public string Description => "Push notifications to your phone. Create an application at pushover.net/apps/build for the token; the user key is on your dashboard.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("token", "Application token", FieldKind.Password, Required: true),
        new("user", "User key", FieldKind.Password, Required: true),
    ];

    public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
        => Task.FromResult(connection.Settings.Get("token").Length > 0 && connection.Settings.Get("user").Length > 0
            ? ProbeResult.Up(TimeSpan.Zero, "Ready")
            : ProbeResult.Down(TimeSpan.Zero, "Both the application token and the user key are needed."));

    public async Task SendAsync(Connection channel, Alert alert, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = channel.Settings.Get("token"),
            ["user"] = channel.Settings.Get("user"),
            ["title"] = $"LabbyTwo: {alert.Title}",
            ["message"] = alert.Body,
            // Down is worth a noise; a recovery is not.
            ["priority"] = alert.Level == AlertLevel.Down ? "1" : "0",
        });

        using var response = await http.PostAsync("https://api.pushover.net/1/messages.json", form, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Pushover answered HTTP {(int)response.StatusCode}. {body[..Math.Min(body.Length, 200)]}".Trim());
        }
    }
}
