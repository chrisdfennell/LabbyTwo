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
            return $"Timed out — nothing answered{where}. Check the address and port, and that a " +
                   "firewall is not silently dropping the connection.";

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
}
