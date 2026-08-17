using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LabbyTwo.Core;
using LabbyTwo.Providers;

namespace LabbyTwo.NtfyPlugin;

/// <summary>
/// <a href="https://ntfy.sh">ntfy</a>, the notification channel for people who would rather
/// not add a cloud account to a dashboard whose whole point is that it runs on their own
/// hardware. Publish to a topic over plain HTTP; subscribe from a phone, a browser, or a
/// shell script.
///
/// The four channels LabbyTwo ships are email, a raw webhook, IFTTT and Pushover — three of
/// which are somebody else's service, and the fourth of which is a URL you have to shape
/// yourself. ntfy is the one that is neither: self-hostable, and with a message format that
/// has somewhere to put the things an <see cref="Alert"/> already knows.
///
/// It is also the worked example for <see cref="IAlertChannel"/>, which was the one
/// extension point with no plugin demonstrating it.
/// </summary>
public sealed class NtfyProvider(IHttpClientFactory httpFactory) : IAlertChannel
{
    public string Type => "ntfy";
    public string DisplayName => "ntfy";
    public string Icon => "📣";
    public string Category => "Alerts";

    public string Description =>
        "Push notifications to a phone or browser through ntfy — the public ntfy.sh, or your own server. " +
        "Anyone who knows the topic name can read it, so use a long one or turn on access control.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Server", FieldKind.Url, "https://ntfy.sh", Required: true, Default: "https://ntfy.sh",
            Help: "Your own ntfy if you run one — http://192.168.86.57:8080 and the like."),

        new("topic", "Topic", FieldKind.Text, "labbytwo-a7f3c1", Required: true,
            Help: "On a server without access control this is the only thing keeping the notifications " +
                  "private, so make it long and unguessable rather than “alerts”."),

        // Both auth styles ntfy supports. Neither is needed on a private LAN server that
        // has no access control turned on, which is why both are tucked away.
        new("token", "Access token", FieldKind.Password, Help: "An ntfy token (tk_…). Leave blank if the topic is open.")
        {
            Advanced = true,
        },
        new("username", "Username", FieldKind.Text, Help: "Only if your server uses basic auth instead of a token.")
        {
            Advanced = true,
        },
        new("password", "Password", FieldKind.Password) { Advanced = true },

        new("click_url", "Open when tapped", FieldKind.Url, "http://192.168.86.57:5150",
            Help: "Where the notification takes you. Your LabbyTwo address is the useful answer — " +
                  "a phone alert you can act on beats one you have to go and find the dashboard for.")
        {
            Advanced = true,
        },
    ];

    /// <summary>
    /// Only a shape check. The Test button on an alert channel sends a real notification
    /// through <see cref="SendAsync"/> rather than calling this, which is the right test:
    /// a server that answers a health check and then refuses your token has told you
    /// nothing useful.
    /// </summary>
    public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var url = connection.Settings.Get("url");
        var topic = connection.Settings.Get("topic");

        if (url.Length == 0 || topic.Length == 0)
            return Task.FromResult(ProbeResult.Down(TimeSpan.Zero, "A server and a topic are both needed."));

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            return Task.FromResult(ProbeResult.Down(TimeSpan.Zero, $"“{url}” is not a URL ntfy could be at."));

        return Task.FromResult(ProbeResult.Up(TimeSpan.Zero, $"Publishing to {url.TrimEnd('/')}/{topic}"));
    }

    public async Task SendAsync(Connection channel, Alert alert, CancellationToken ct)
    {
        var url = channel.Settings.Get("url").TrimEnd('/');
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);

        // The JSON form, posted to the server root, rather than the header form posted to
        // /topic. Headers are latin-1 by the letter of HTTP, and a title carrying a degree
        // sign or an em dash — both of which LabbyTwo writes — arrives mangled or gets the
        // request rejected outright. A JSON body is UTF-8 and has no such argument.
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new NtfyMessage
            {
                Topic = channel.Settings.Get("topic"),
                Title = alert.Title,
                Message = alert.Body,
                Priority = Priority(alert),
                Tags = [Tag(alert.Level)],
                Click = channel.Settings.Get("click_url") is { Length: > 0 } click ? click : null,
            }),
        };

        // A token wins if both are filled in: it is the narrower credential, and somebody
        // who has pasted one has moved on from the username they left behind.
        if (channel.Settings.Get("token") is { Length: > 0 } token)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }
        else if (channel.Settings.Get("username") is { Length: > 0 } user)
        {
            var basic = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{user}:{channel.Settings.Get("password")}"));
            request.Headers.TryAddWithoutValidation("Authorization", $"Basic {basic}");
        }

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"ntfy answered HTTP {(int)response.StatusCode}. {body[..Math.Min(body.Length, 200)]}".Trim());
        }
    }

    /// <summary>
    /// ntfy's five levels, used for what they are actually for. 5 bypasses a phone's Do Not
    /// Disturb; 2 arrives without a sound. So a tornado warning wakes you, something going
    /// down buzzes, and the recovery an hour later is waiting when you next look — which is
    /// the difference between a channel you keep on and one you mute in a fortnight.
    /// </summary>
    private static int Priority(Alert alert) => alert switch
    {
        { Urgent: true } => 5,
        { Level: AlertLevel.Down } => 4,
        { Level: AlertLevel.Up } => 2,
        _ => 3,
    };

    /// <summary>
    /// ntfy turns a tag that is a known emoji shortcode into the emoji, and shows anything
    /// else as a label. These are all shortcodes, so the notification reads the same way the
    /// dashboard does.
    /// </summary>
    private static string Tag(AlertLevel level) => level switch
    {
        AlertLevel.Down => "rotating_light",
        AlertLevel.Up => "white_check_mark",
        _ => "information_source",
    };

    /// <summary>
    /// ntfy's publish format. Its field names are lower case and it rejects nothing it does
    /// not recognise, but sending nulls for the optional half is untidy — hence the ignore
    /// condition.
    /// </summary>
    private sealed record NtfyMessage
    {
        [JsonPropertyName("topic")] public string Topic { get; init; } = "";
        [JsonPropertyName("title")] public string Title { get; init; } = "";
        [JsonPropertyName("message")] public string Message { get; init; } = "";
        [JsonPropertyName("priority")] public int Priority { get; init; } = 3;
        [JsonPropertyName("tags")] public string[] Tags { get; init; } = [];

        [JsonPropertyName("click"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Click { get; init; }
    }
}
