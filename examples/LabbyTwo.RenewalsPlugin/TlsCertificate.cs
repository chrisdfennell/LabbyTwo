using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace LabbyTwo.RenewalsPlugin;

/// <summary>
/// Reads the certificate a host is actually presenting.
///
/// Deliberately from the outside, over the network, rather than from a file on disk —
/// because the failure worth catching is not "the certificate expired", which every ACME
/// client already handles, but "it renewed and nothing reloaded". A new file sitting in
/// /etc/letsencrypt while the process still serves last quarter's certificate looks fine
/// to anything that checks the disk, and looks exactly like an expiring certificate to
/// anything that connects.
/// </summary>
public static class TlsCertificate
{
    public sealed record Result(DateTimeOffset NotBefore, DateTimeOffset NotAfter, string Issuer, string Subject)
    {
        public int DaysLeft => (int)Math.Floor((NotAfter - DateTimeOffset.Now).TotalDays);
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Connects, completes the handshake, and reports what came back. Throws with a
    /// readable message — the caller records that against the row rather than swallowing
    /// it, because a host that stopped answering is itself worth seeing.
    /// </summary>
    public static async Task<Result> ReadAsync(string hostAndPort, CancellationToken ct)
    {
        var (host, port) = Split(hostAndPort);
        if (host.Length == 0)
            throw new InvalidOperationException("No host to check.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);

        DateTimeOffset? notBefore = null, notAfter = null;
        string issuer = "", subject = "";

        using var client = new TcpClient();

        try
        {
            await client.ConnectAsync(host, port, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{host}:{port} did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"Could not reach {host}:{port} — {ex.Message}");
        }

        // Accepts anything, on purpose: this reads a certificate rather than trusting one.
        // A self-signed or already-expired certificate is exactly the case that has to be
        // readable, and refusing it here would report the interesting rows as unreachable.
        await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
            (_, certificate, _, _) =>
            {
                if (Read(certificate) is { } details)
                    (notBefore, notAfter, issuer, subject) = details;
                return true;
            });

        try
        {
            // TargetHost is the SNI name, so a host serving several certificates presents
            // the right one — without it you get whatever its default is.
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{host}:{port} accepted the connection but never finished the TLS handshake.");
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            // A handshake can fail after the certificate has already been seen — a protocol
            // mismatch, say — and the certificate is what was wanted, so keep it.
            if (notAfter is null)
                throw new InvalidOperationException($"TLS handshake with {host}:{port} failed: {ex.GetBaseException().Message}");
        }

        if (notAfter is not { } expires || notBefore is not { } starts)
            throw new InvalidOperationException($"{host}:{port} completed a handshake but presented no certificate.");

        return new Result(starts, expires, Friendly(issuer), Friendly(subject));
    }

    private static (DateTimeOffset NotBefore, DateTimeOffset NotAfter, string Issuer, string Subject)? Read(
        X509Certificate? certificate)
    {
        if (certificate is null)
            return null;

        // SslStream hands over an X509Certificate2 in practice; the export path is there
        // for the case where it does not, rather than as the normal route.
        if (certificate is X509Certificate2 parsed)
            return (parsed.NotBefore, parsed.NotAfter, parsed.Issuer, parsed.Subject);

        using var loaded = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        return (loaded.NotBefore, loaded.NotAfter, loaded.Issuer, loaded.Subject);
    }

    /// <summary>
    /// A distinguished name is "CN=R11, O=Let's Encrypt, C=US". The organisation is what
    /// somebody wants to read on a row; the common name is the fallback.
    /// </summary>
    private static string Friendly(string distinguishedName)
    {
        var parts = distinguishedName.Split(',', StringSplitOptions.TrimEntries);

        foreach (var prefix in (string[])["O=", "CN="])
        {
            if (parts.FirstOrDefault(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } match)
                return match[prefix.Length..].Trim('"');
        }

        return distinguishedName;
    }

    /// <summary>Accepts "example.com", "example.com:8443", and a pasted "https://example.com/".</summary>
    private static (string Host, int Port) Split(string hostAndPort)
    {
        var text = hostAndPort.Trim();

        if (text.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(text, UriKind.Absolute, out var url))
            return (url.Host, url.IsDefaultPort ? 443 : url.Port);

        text = text.TrimEnd('/');

        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var port))
            return (text[..colon], port);

        return (text, 443);
    }
}
