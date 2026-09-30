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
        // Noted for whoever asked for the probe — see Capture. Done here because this is
        // the one place nearly every provider already hands its exception to, so the
        // monitor learns what kind of failure it was without reading the sentence back.
        Note(Classify(ex, target));

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

            return $"Timed out — nothing answered{where}.{ContainerHint(target)} Check the address and port, " +
                   "and that a firewall is not silently dropping the connection.";
        }

        if (root is SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused =>
                    $"Connection refused{where}. Something is at that address but nothing is listening on that port.",
                SocketError.HostNotFound or SocketError.NoData when IsPublicName(target) =>
                    $"Could not resolve the host{where}. That is a public name, so this is not a LAN name the " +
                    "container cannot see: it has no working DNS at all. Give it a DNS server with \"dns:\" in " +
                    "docker-compose.yml — your router's address, or 1.1.1.1.",
                SocketError.HostNotFound or SocketError.NoData =>
                    $"Could not resolve the host{where}. Use an IP address if this container cannot see your DNS.",
                // EAI_AGAIN: the resolver did not answer, as opposed to answering "no such
                // name". It is the container's DNS failing, not the name — and when it
                // happens to everything at once, it is LabbyTwo that cannot see.
                SocketError.TryAgain =>
                    $"DNS lookup failed for now{where}: the resolver did not answer (\"{socket.Message}\"). If every " +
                    "connection says this at once, it is DNS inside LabbyTwo's container that is failing, not the service.",
                SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                    $"No route{where}. If LabbyTwo is in a container, it may not be able to reach that network.",
                SocketError.TimedOut =>
                    $"Timed out{where}. Nothing answered on that port.",
                _ => $"{socket.Message}{where}",
            };
        }

        // A typo in a form rather than a network fault, and the raw message — "Invalid URI:
        // Invalid port specified." — names neither the field it came from nor the value it
        // choked on, which leaves nothing to act on.
        if (root is UriFormatException)
        {
            return string.IsNullOrWhiteSpace(target)
                ? "That address could not be understood. It needs a scheme and a host, like " +
                  "http://192.168.1.50:8083."
                : $"\"{target}\" is not an address this can use. It wants http:// or https://, then the host, then " +
                  "one colon and the port — http://192.168.1.50:8083 — with no space, second colon or stray " +
                  "character anywhere in it.";
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

    /// <summary>
    /// Only ever true inside a container, and only for a LAN address. Reaching another
    /// container's *published* port through the host's IP has to be forwarded back in, and
    /// NAS firmware routinely refuses to do that between its bridge networks — so the
    /// service answers from a shell on the host and times out from in here. Native and
    /// host-networked services on the same box are fine, which is what makes it baffling.
    /// </summary>
    private static string ContainerHint(string? target)
    {
        if (!InContainer.Value || HostOf(target) is not null)
            return "";

        var host = target;
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
            host = uri.Host;

        if (!System.Net.IPAddress.TryParse(host?.Trim('[', ']'), out var address) || !IsPrivate(address))
            return "";

        return " If that is another container's published port, use its container name on a shared network; " +
               "if it is a service on the host, a container cannot always reach the host's own LAN address.";
    }

    private static readonly Lazy<bool> InContainer = new(() =>
        File.Exists("/.dockerenv") ||
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true",
            StringComparison.OrdinalIgnoreCase));

    private static bool IsPrivate(System.Net.IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
            return false;
        return bytes[0] switch
        {
            10 => true,
            172 => bytes[1] >= 16 && bytes[1] <= 31,
            192 => bytes[1] == 168,
            _ => false,
        };
    }

    /// <summary>The host part of a URL, or the target itself if it is already a bare name.</summary>
    public static string? HostOf(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return null;
        var host = Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri.Host : target.Trim();
        host = host.Trim('[', ']');
        return host.Length > 0 && !System.Net.IPAddress.TryParse(host, out _) ? host : null;
    }

    /// <summary>
    /// True for a name that only public DNS could answer — api.github.com rather than
    /// "nas" or "nas.lan". "Use an IP address" is sound advice for a LAN name the container
    /// cannot see, and useless for GitHub, whose address is not the user's to pin: when one
    /// of these fails, the container has no working resolver at all.
    /// </summary>
    public static bool IsPublicName(string? target)
    {
        var host = HostOf(target);
        if (host is null || !host.Contains('.'))
            return false;

        var suffix = host[(host.LastIndexOf('.') + 1)..].ToLowerInvariant();
        return suffix is not ("local" or "lan" or "home" or "internal" or "localdomain" or "arpa" or "intranet" or "corp");
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

    // ---- what kind of failure --------------------------------------------------------

    /// <summary>
    /// What kind of failure an exception is, by its type and error code rather than by its
    /// words. The distinction the monitor needs is between "that service did not answer"
    /// and "LabbyTwo could not have seen whether it answered" — a DNS resolver that is not
    /// replying, a probe the monitor gave up waiting for, the local Docker socket hanging,
    /// LabbyTwo's own database refusing a write. Many of the second kind at once mean
    /// LabbyTwo is blind, not that the lab is down (see <see cref="BlindnessRules"/>).
    /// </summary>
    /// <param name="target">What was being asked. A Docker socket path is what tells a hung
    /// daemon on this machine apart from a slow web server somewhere else.</param>
    public static ProbeFailure Classify(Exception ex, string? target = null)
    {
        var root = ex.GetBaseException();

        // SqliteException is one of these; naming the base keeps Core free of the driver.
        if (root is System.Data.Common.DbException)
            return ProbeFailure.Database;

        if (ex is OperationCanceledException || root is OperationCanceledException || root is TimeoutException)
            return IsLocalSocket(target) ? ProbeFailure.DockerSocket : ProbeFailure.Timeout;

        if (root is SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => ProbeFailure.Dns,
                SocketError.ConnectionRefused => ProbeFailure.Refused,
                SocketError.NetworkUnreachable or SocketError.HostUnreachable => ProbeFailure.NoRoute,
                SocketError.TimedOut => IsLocalSocket(target) ? ProbeFailure.DockerSocket : ProbeFailure.Timeout,
                _ => ProbeFailure.Other,
            };
        }

        return ProbeFailure.Other;
    }

    /// <summary>
    /// The same question for a message that did not come through <see cref="Describe"/> — a
    /// plugin that wrote its own sentence from <c>ex.Message</c>, say. Only a fallback: it
    /// knows the words this class, the monitor and the runtime use for the failures that
    /// matter here, and calls everything else <see cref="ProbeFailure.Other"/>.
    /// </summary>
    public static ProbeFailure ClassifyMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return ProbeFailure.Other;

        bool Has(string text) => message.Contains(text, StringComparison.OrdinalIgnoreCase);

        if (message.StartsWith(AbandonedPrefix, StringComparison.Ordinal))
            return ProbeFailure.Abandoned;
        if (Has("database is locked") || Has("SQLite Error"))
            return ProbeFailure.Database;
        if (Has("Resource temporarily unavailable") || Has("Temporary failure in name resolution") ||
            Has("Could not resolve the host") || Has("DNS lookup failed") || Has("Name or service not known") ||
            Has("No such host is known"))
            return ProbeFailure.Dns;
        if (Has("Timed out") && Has(".sock"))
            return ProbeFailure.DockerSocket;
        return ProbeFailure.Other;
    }

    /// <summary>How the monitor's own "gave up waiting" message starts, so it is recognised without guessing.</summary>
    public const string AbandonedPrefix = "No answer within ";

    /// <summary>A Unix socket or a Windows pipe: something on this machine, which no network can be blamed for.</summary>
    private static bool IsLocalSocket(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return false;
        var t = target.Trim();
        return t.StartsWith("unix://", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("npipe:", StringComparison.OrdinalIgnoreCase) ||
               t.EndsWith(".sock", StringComparison.OrdinalIgnoreCase) ||
               (t.StartsWith('/') && !t.Contains("://", StringComparison.Ordinal));
    }

    private static readonly AsyncLocal<FailureCapture?> Current = new();

    /// <summary>
    /// Starts listening for the kind of failure <see cref="Describe"/> sees on this async
    /// flow — the probe the monitor is about to make and everything it awaits. Dispose it
    /// when the probe is done. Providers need no change: they already describe their
    /// exceptions here, and that is all it takes to be heard.
    /// </summary>
    public static FailureCapture Capture()
    {
        var capture = new FailureCapture(Current.Value);
        Current.Value = capture;
        return capture;
    }

    private static void Note(ProbeFailure kind)
    {
        if (Current.Value is { } capture)
            capture.Kind = kind;
    }

    /// <summary>What <see cref="Capture"/> hands back: the last failure described while it was listening.</summary>
    public sealed class FailureCapture : IDisposable
    {
        private readonly FailureCapture? _outer;

        internal FailureCapture(FailureCapture? outer) => _outer = outer;

        /// <summary><see cref="ProbeFailure.None"/> until something is described.</summary>
        public ProbeFailure Kind { get; internal set; }

        public void Dispose() => Current.Value = _outer;
    }
}

/// <summary>
/// The kinds of failure a probe can end in, as far as telling "it is down" from "LabbyTwo
/// could not see" goes. Stored nowhere: it is worked out again for every probe.
/// </summary>
public enum ProbeFailure
{
    /// <summary>Nothing was described: the probe worked, or failed without an exception.</summary>
    None,

    /// <summary>Anything not below: an HTTP error, a bad password, an answer that made no sense.</summary>
    Other,

    /// <summary>Nothing answered in time, somewhere on the network.</summary>
    Timeout,

    Refused,

    NoRoute,

    /// <summary>A name could not be resolved — including EAI_AGAIN, the resolver not answering at all.</summary>
    Dns,

    /// <summary>The monitor gave up waiting for the probe (see <c>HealthMonitor.ProbeDeadline</c>).</summary>
    Abandoned,

    /// <summary>The Docker socket on this machine did not answer.</summary>
    DockerSocket,

    /// <summary>LabbyTwo's own database refused — "database is locked".</summary>
    Database,
}

public static class ProbeFailures
{
    /// <summary>
    /// A failure that says more about LabbyTwo's own view than about the service: its DNS,
    /// its patience, its Docker socket, its database. One of these alone is still reported —
    /// a single name that does not resolve is a real misconfiguration — but many at once are
    /// what <see cref="BlindnessRules"/> looks for.
    /// </summary>
    public static bool IsBlind(this ProbeFailure failure) =>
        failure is ProbeFailure.Dns or ProbeFailure.Abandoned or ProbeFailure.DockerSocket or ProbeFailure.Database;
}
