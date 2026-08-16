using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// How long a TLS certificate has left.
///
/// This is the outage that gives no warning. Everything is up, every probe is green, and
/// then one morning nothing works and the reason is a date that passed at 3am. Renewal is
/// usually automatic, which makes it worse rather than better: automatic renewal fails
/// silently, and the only sign is a number quietly counting down that nobody is looking at.
///
/// It reports days remaining as an ordinary metric on purpose, so the existing threshold
/// rules do the alerting — there is nothing new here to configure or maintain, and
/// "cert_days_left below 21" is a rule you can already write.
/// </summary>
public sealed class CertificateProvider : IConnectionProvider
{
    public string Type => "certificate";
    public string DisplayName => "TLS certificate";
    public string Icon => "🔐";
    public string Category => "Network";
    public string Description =>
        "Days until a TLS certificate expires, and who issued it. For the renewal that stops working quietly.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("host", "Hostname", FieldKind.Text, "cloud.example.com", Required: true,
            Help: "The name the certificate is for. Checked from wherever LabbyTwo runs."),
        new("port", "Port", FieldKind.Number, Default: "443"),
        new("sni", "Server name (SNI)", FieldKind.Text,
            Help: "Only if the certificate you want is not the one served for the hostname above — "
                + "one address hosting several sites behind a proxy."),
    ];

    /// <summary>
    /// A certificate's expiry moves once a day, and the renewal you are watching for
    /// happens weeks before it matters. Asking every thirty seconds would open a TLS
    /// handshake against somebody else's server 2,880 times a day to be told the same
    /// date, which is rude at best and looks like a scanner at worst.
    /// </summary>
    public TimeSpan MinimumInterval => TimeSpan.FromHours(6);

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("cert_days_left", "Certificate expires in", " days"),
        new("cert_trusted", "Certificate trusted"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Certificate expiring soon", "cert_days_left", Comparison.Below, 21, ClearThreshold: 30,
            Why: "Three weeks is enough time to find out why the renewal stopped and fix it by hand. "
               + "Let's Encrypt renews at 30 days, so anything below 21 means two attempts have "
               + "already failed silently."),
        new("Certificate nearly expired", "cert_days_left", Comparison.Below, 5, ClearThreshold: 10,
            Why: "The one to send to your phone. At five days the automatic renewal is not coming back "
               + "on its own and somebody has to look."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var host = connection.Settings.Get("host").Trim();
        if (host.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No hostname configured.");

        // Paste-tolerant: somebody will type the URL they were looking at rather than the
        // bare name, and refusing that is a worse experience than accepting it.
        if (Uri.TryCreate(host, UriKind.Absolute, out var url) && url.Host.Length > 0)
            host = url.Host;

        var port = Math.Clamp(connection.Settings.GetInt("port", 443), 1, 65535);
        var sni = connection.Settings.Get("sni").Trim() is { Length: > 0 } name ? name : host;

        var started = DateTimeOffset.UtcNow;
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, ct);

            X509Certificate2? served = null;
            var trusted = false;

            using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                (_, certificate, chain, errors) =>
                {
                    served = certificate as X509Certificate2
                             ?? chain?.ChainElements.FirstOrDefault()?.Certificate;
                    trusted = errors == SslPolicyErrors.None;

                    // Always accept. This provider is inspecting a certificate, not relying
                    // on one — refusing an untrusted chain here would mean the self-signed
                    // certificates that are normal on a LAN could never be watched at all,
                    // which is the same reasoning as ProviderHttp's.
                    return true;
                });

            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = sni }, ct);

            var elapsed = DateTimeOffset.UtcNow - started;

            if (served is null)
                return ProbeResult.Down(elapsed, "The handshake completed but produced no certificate.");

            var now = DateTimeOffset.Now;
            var expires = new DateTimeOffset(served.NotAfter);
            var starts = new DateTimeOffset(served.NotBefore);

            // Fractional days, so a chart of the countdown is a smooth slope rather than a
            // staircase, and so "0 days" means expired rather than "expires sometime today".
            var daysLeft = (expires - now).TotalDays;

            var readings = new Dictionary<string, double>
            {
                ["cert_days_left"] = Math.Round(daysLeft, 2),
                ["cert_trusted"] = trusted ? 1 : 0,
            };

            var issuer = served.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
            if (string.IsNullOrWhiteSpace(issuer))
                issuer = served.Issuer;

            // Down only for a certificate that is actually invalid *by date*, which is the
            // failure this exists to catch and is not a matter of taste. An untrusted chain
            // is reported and charted but is not "down": self-signed is normal on a LAN, and
            // a provider that called every homelab certificate a fault would be turned off
            // within a week.
            if (now > expires)
            {
                // "2d ago" is the useful phrasing for the renewal that failed last week,
                // and useless for a certificate that lapsed in 2015 — Ago counts in days
                // for ever, so that reads "4143d 15h ago" and has to be decoded. Past a
                // season, the date says it better.
                var lapsed = (now - expires).TotalDays < 90
                    ? Ago.Since(expires, now)
                    : $"on {expires:d MMM yyyy}";

                return new ProbeResult(false, $"Expired {lapsed} — {issuer}.", elapsed, readings);
            }

            if (now < starts)
                return new ProbeResult(false,
                    $"Not valid until {starts:d MMM yyyy} — {issuer}.", elapsed, readings);

            var trust = trusted ? "" : " · self-signed or untrusted";
            return ProbeResult.Up(elapsed,
                $"{Days(daysLeft)} left — expires {expires:d MMM yyyy}, {issuer}{trust}.", readings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ProbeResult.Down(DateTimeOffset.UtcNow - started, ex.GetBaseException().Message);
        }
    }

    private static string Days(double days) => days switch
    {
        < 1 => "Less than a day",
        < 2 => "1 day",
        _ => $"{(int)days} days",
    };
}
