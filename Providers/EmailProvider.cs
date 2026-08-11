using System.Net;
using System.Net.Mail;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Email. Every other alert channel here needs a push service you already run, which is
/// fine until you consider the person this is actually for: their NAS goes down at two in
/// the morning, and what they have is an email address. This is the channel that works
/// with nothing else installed.
/// </summary>
public sealed class EmailProvider : IAlertChannel
{
    public string Type => "email";
    public string DisplayName => "Email (SMTP)";
    public string Icon => "✉️";
    public string Category => "Alerts";
    public string Description => "Sends alerts to an inbox through any SMTP server — your own, or your provider's.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("host", "SMTP server", FieldKind.Text, "smtp.gmail.com", Required: true),

        new("port", "Port", FieldKind.Number, Default: "587",
            Help: "587 with STARTTLS is what nearly everything wants. Port 465 (TLS from the first byte) " +
                  "is not supported here — use 587 if the server offers both."),

        new("tls", "Encryption", FieldKind.Select, Default: "starttls", Options:
        [
            new SelectOption("starttls", "STARTTLS (587)"),
            new SelectOption("none", "None — a server on your own LAN"),
        ]),

        new("username", "Username", FieldKind.Text,
            Help: "Leave blank for a relay on your own network that does not ask."),

        new("password", "Password", FieldKind.Password,
            Help: "For Gmail and most providers this has to be an app password, not your account password."),

        new("from", "From", FieldKind.Text, "labbytwo@example.com", Required: true,
            Help: "Many providers insist this matches the account that is signing in."),

        new("to", "To", FieldKind.Text, "me@example.com", Required: true,
            Help: "Several addresses, separated by commas, all get the same message."),

        new("prefix", "Subject prefix", FieldKind.Text, Default: "[LabbyTwo]",
            Help: "Makes a mail rule easy to write, which is how these end up somewhere you will see them."),
    ];

    /// <summary>
    /// Nothing here can be probed without sending mail, and a monitor that emailed every
    /// thirty seconds to prove email works would be its own outage. The Test button sends
    /// one real message instead, which is the honest check anyway.
    /// </summary>
    public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
        => Task.FromResult(
            connection.Settings.Get("host").Length > 0 && connection.Settings.Get("to").Length > 0
                ? ProbeResult.Up(TimeSpan.Zero, $"Ready — {connection.Settings.Get("to")}")
                : ProbeResult.Down(TimeSpan.Zero, "Needs a server and at least one address to send to."));

    public async Task SendAsync(Connection channel, Alert alert, CancellationToken ct)
    {
        var host = channel.Settings.Get("host");
        var recipients = channel.Settings.Get("to");

        if (host.Length == 0 || recipients.Length == 0)
            throw new InvalidOperationException("This channel has no SMTP server or no recipient.");

        var prefix = channel.Settings.Get("prefix", "[LabbyTwo]");
        using var message = new MailMessage
        {
            From = new MailAddress(channel.Settings.Get("from")),
            Subject = $"{prefix} {alert.Emoji} {alert.Title}".Trim(),
            Body = $"{alert.Title}\n\n{alert.Body}\n\n— LabbyTwo",
            IsBodyHtml = false,
        };

        foreach (var address in recipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            message.To.Add(address);

        using var client = new SmtpClient(host, channel.Settings.GetInt("port", 587))
        {
            EnableSsl = channel.Settings.Get("tls", "starttls") != "none",
            Timeout = 20_000,
        };

        if (channel.Settings.Get("username") is { Length: > 0 } user)
        {
            // Explicitly not default credentials: on Windows that would try the logged-in
            // account against your mail provider, which fails in a confusing way.
            client.UseDefaultCredentials = false;
            client.Credentials = new NetworkCredential(user, channel.Settings.Get("password"));
        }

        try
        {
            await client.SendMailAsync(message, ct);
        }
        catch (SmtpException ex)
        {
            throw new InvalidOperationException(Explain(ex), ex);
        }
    }

    /// <summary>
    /// SMTP failures are famously unhelpful, and three of them account for nearly every
    /// one people hit. Say what to do rather than what the server said.
    /// </summary>
    private static string Explain(SmtpException ex) => ex.StatusCode switch
    {
        SmtpStatusCode.MailboxBusy or SmtpStatusCode.MailboxUnavailable =>
            $"The server would not accept the recipient: {ex.Message}",

        SmtpStatusCode.ClientNotPermitted or SmtpStatusCode.TransactionFailed =>
            "The server refused to relay this. Usually the From address has to match the account signing in.",

        _ when ex.Message.Contains("5.7.8", StringComparison.Ordinal)
            || ex.Message.Contains("Authentication", StringComparison.OrdinalIgnoreCase) =>
            "Authentication failed. Gmail, Outlook and most providers need an app password here rather " +
            "than the account password, and only issue one once two-factor is on.",

        _ when ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("secure", StringComparison.OrdinalIgnoreCase) =>
            "The TLS handshake failed. This sends STARTTLS on the port you gave — port 465 expects TLS " +
            "immediately and will not work; try 587.",

        _ => ex.Message,
    };
}
