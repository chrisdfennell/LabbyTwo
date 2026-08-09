using System.Net.Sockets;

namespace LabbyTwo.Core;

/// <summary>
/// Turns the exception a failed request throws into something the person reading the tile
/// can act on. ".NET says "A task was canceled." for an HTTP timeout, which is true, gives
/// no clue what to do, and is what every provider used to display.
///
/// The distinctions matter because they have different fixes: a timeout means nothing
/// answered at all, a refusal means something is there but not listening on that port, and
/// a resolution failure means the name is wrong or DNS cannot see it.
/// </summary>
public static class ProbeError
{
    /// <summary>
    /// <paramref name="target"/> is the host or URL being probed, quoted back so a tile
    /// showing several connections says which one is broken.
    /// </summary>
    public static string Describe(Exception ex, string? target = null)
    {
        var where = string.IsNullOrWhiteSpace(target) ? "" : $" at {target}";
        var root = ex.GetBaseException();

        // HttpClient reports its own timeout as a cancellation, so this is the common case
        // rather than an exotic one.
        if (ex is OperationCanceledException || root is OperationCanceledException || root is TimeoutException)
        {
            // A name that only the host can resolve — /etc/hosts, NetBIOS, mDNS — makes the
            // DNS query hang rather than fail, so the request times out and this looks like
            // a firewall. Containers inherit none of those, so it is worth naming here: it
            // is the single most common reason a NAS install cannot see its own services.
            if (LooksLikeHostname(target))
                return $"Timed out — nothing answered{where}.{DescribeResolution(target)} Otherwise check the " +
                       "address and port, and that a firewall is not silently dropping the connection.";

            return $"Timed out — nothing answered{where}. Check the address and port, and that a " +
                   "firewall is not silently dropping the connection.";
        }

        if (root is SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused =>
                    $"Connection refused{where}. Something is at that address but nothing is listening on that port.",
                SocketError.HostNotFound or SocketError.NoData =>
                    $"Could not resolve the host{where}. Use an IP address if this container cannot see your DNS.",
                SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                    $"No route{where}. If LabbyTwo is in a container, it may not be able to reach that network.",
                SocketError.TimedOut =>
                    $"Timed out{where}. Nothing answered on that port.",
                _ => $"{socket.Message}{where}",
            };
        }

        // A TLS failure against a plain-HTTP port is a very common misconfiguration, and
        // the raw message ("The SSL connection could not be established") does not say so.
        if (root is System.Security.Authentication.AuthenticationException)
            return $"TLS handshake failed{where}. If that port serves plain HTTP, use http:// rather than https://.";

        return root.Message;
    }

    /// <summary>
    /// Looks the name up and says what came back. Worth a DNS query because this only runs
    /// after something has already failed, and because the answer is usually the whole
    /// story: a NAS with a Container Station bridge per stack maps its own hostname to
    /// every one of them, the addresses are tried in order, and the real LAN address can
    /// be twentieth. That times out looking exactly like a firewall.
    /// </summary>
    private static string DescribeResolution(string? target)
    {
        var host = HostOf(target);
        if (host is null)
            return "";

        try
        {
            var lookup = System.Net.Dns.GetHostAddressesAsync(host);
            // Bounded: this is an error path, but it must not add a second stall to one.
            if (!lookup.Wait(TimeSpan.FromSeconds(2)))
                return $" The name \"{host}\" did not resolve quickly, which alone can cause this — try the IP address.";

            var addresses = lookup.Result;
            if (addresses.Length == 0)
                return $" \"{host}\" resolves to nothing here — try the IP address.";

            if (addresses.Length == 1)
                return $" \"{host}\" resolves to {addresses[0]} here, so check that something is listening there.";

            // No trailing ellipsis when the sample already is every address.
            var sample = string.Join(", ", addresses.Take(3).Select(a => a.ToString()))
                         + (addresses.Length > 3 ? ", …" : "");
            return $" \"{host}\" resolves to {addresses.Length} addresses in this container ({sample}) and they " +
                   "are tried in order, so if the one that serves this is not near the front the attempt times out " +
                   "before reaching it. Use the address directly.";
        }
        catch
        {
            return $" \"{host}\" could not be resolved in this container — containers do not inherit the host's " +
                   "/etc/hosts, NetBIOS or mDNS. Use the IP address.";
        }
    }

    /// <summary>The host part of a URL, or the target itself if it is already a bare name.</summary>
    private static string? HostOf(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return null;
        var host = Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri.Host : target.Trim();
        host = host.Trim('[', ']');
        return host.Length > 0 && !System.Net.IPAddress.TryParse(host, out _) ? host : null;
    }

    /// <summary>
    /// True when the target is addressed by name rather than by IP. An IP literal cannot
    /// have a DNS problem, so the hint above would only be noise for one.
    /// </summary>
    private static bool LooksLikeHostname(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return false;

        var host = target;
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
            host = uri.Host;

        host = host.Trim('[', ']');   // an IPv6 literal in a URL is bracketed
        return host.Length > 0 && !System.Net.IPAddress.TryParse(host, out _);
    }
}
