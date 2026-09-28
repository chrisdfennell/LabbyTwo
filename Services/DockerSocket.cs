using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace LabbyTwo.Services;

/// <summary>
/// Speaks HTTP to the Docker Engine over whichever transport an endpoint names — a unix
/// socket, a Windows named pipe, or TCP.
///
/// Shared by the Docker provider, which reads and restarts, and the self-updater, which may
/// create a container. Worth being blunt about the second one: anything that can reach the
/// raw socket can start a privileged container, which is root on the host. Mounting the
/// socket read-only does not change that — <c>:ro</c> protects the socket file, not the API
/// behind it. TCP matters mostly because it lets the endpoint be a filtering proxy
/// (linuxserver/socket-proxy, tecnativa/docker-socket-proxy) that passes only the calls
/// LabbyTwo needs.
/// </summary>
public static class DockerSocket
{
    /// <summary>Where the socket lives on a normal Linux host, and inside a container that mounted it.</summary>
    public const string DefaultEndpoint = "/var/run/docker.sock";

    /// <summary>
    /// The Docker API version every call here asks for. Old enough for anything still
    /// running, new enough for everything used here — and, since Docker 29 stopped accepting
    /// the 1.25 that Watchtower's client defaults to, also what a helper Watchtower is told to
    /// use, so it works wherever these calls do.
    /// </summary>
    public const string ApiVersionNumber = "1.41";

    private const string ApiVersion = "/v" + ApiVersionNumber;

    /// <summary>
    /// The endpoint to use when nothing more specific was configured: <c>DOCKER_HOST</c> if
    /// it is set, because that is what every other Docker client honours and what a compose
    /// file pointing at a proxy will already have, and the standard socket path otherwise.
    /// </summary>
    public static string EnvironmentEndpoint =>
        Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 } host ? host.Trim() : DefaultEndpoint;

    public static async Task<string> GetAsync(string endpoint, TimeSpan timeout, string path, CancellationToken ct)
    {
        using var http = Client(endpoint, timeout);
        using var response = await http.GetAsync(ApiVersion + path, ct);
        return await ReadAsync(response, "GET", path, ct);
    }

    /// <param name="json">A JSON body, or null for the endpoints that take none.</param>
    public static async Task<string> PostAsync(
        string endpoint, TimeSpan timeout, string path, string? json, CancellationToken ct)
    {
        using var http = Client(endpoint, timeout);
        using var content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(ApiVersion + path, content, ct);
        return await ReadAsync(response, "POST", path, ct);
    }

    private static async Task<string> ReadAsync(
        HttpResponseMessage response, string method, string path, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
            return body;

        // Docker puts a readable reason in {"message": "..."}, which beats the status code
        // on its own — "No such image" rather than "Docker answered HTTP 404".
        string? reason = null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var message))
                reason = message.GetString();
        }
        catch (System.Text.Json.JsonException)
        {
        }

        // The daemon itself answers 403 only through an authorization plugin, and then in
        // its usual JSON. A 403 without that JSON is HAProxy's error page — which is what both
        // socket proxies are — refusing a call its flags do not allow. Naming the flag turns
        // "Docker answered HTTP 403" into a one-line fix.
        if (response.StatusCode == HttpStatusCode.Forbidden && reason is null)
            throw new DockerProxyDeniedException(method, path);

        reason ??= body;
        throw new InvalidOperationException(
            reason.Trim().Length > 0 ? $"Docker: {reason.Trim()}" : $"Docker answered HTTP {(int)response.StatusCode}.");
    }

    /// <summary>
    /// A client per call is deliberate — these are cheap local connections, and pooling one
    /// per endpoint would mean tracking connection lifetimes for no real gain.
    /// </summary>
    private static HttpClient Client(string endpoint, TimeSpan timeout)
    {
        var parsed = DockerEndpoint.Parse(endpoint);
        var handler = new SocketsHttpHandler { ConnectTimeout = timeout };
        string baseAddress;

        switch (parsed.Kind)
        {
            case DockerEndpointKind.Tcp:
                baseAddress = "http://" + parsed.Address;
                break;

            case DockerEndpointKind.NamedPipe:
                handler.ConnectCallback = async (_, token) =>
                {
                    var pipe = new NamedPipeClientStream(".", parsed.Address, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync((int)timeout.TotalMilliseconds, token);
                    return pipe;
                };

                // The host is ignored once ConnectCallback takes over, but HttpClient still
                // needs a syntactically valid absolute URI to build the request line.
                baseAddress = "http://localhost";
                break;

            default:
                handler.ConnectCallback = async (_, token) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(parsed.Address), token);
                    return new NetworkStream(socket, ownsSocket: true);
                };
                baseAddress = "http://localhost";
                break;
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = timeout,
        };
    }
}

public enum DockerEndpointKind
{
    Unix,
    NamedPipe,
    Tcp,
}

/// <summary>
/// One Docker endpoint, in any of the spellings people paste: a bare socket path or
/// <c>unix:///path</c>; <c>npipe:////./pipe/name</c> (DOCKER_HOST's form),
/// <c>npipe://./pipe/name</c> or <c>\\.\pipe\name</c>; and <c>tcp://host:port</c>, or
/// <c>http://host:port</c> or a bare <c>host:port</c>, which people write for a proxy and
/// mean the same thing by.
/// </summary>
/// <param name="Address">The socket path, the pipe's name, or <c>host:port</c>.</param>
public sealed record DockerEndpoint(DockerEndpointKind Kind, string Address)
{
    /// <summary>Docker's own port for the unencrypted API, and the one both socket proxies listen on.</summary>
    public const int DefaultTcpPort = 2375;

    private static readonly Regex HostAndPort = new(@"^[A-Za-z0-9_.\-]+:\d+$", RegexOptions.CultureInvariant);

    public bool IsTcp => Kind == DockerEndpointKind.Tcp;

    /// <summary>How another Docker client would write it — Watchtower's DOCKER_HOST, say.</summary>
    public override string ToString() => Kind switch
    {
        DockerEndpointKind.Tcp => "tcp://" + Address,
        DockerEndpointKind.NamedPipe => "npipe:////./pipe/" + Address,
        _ => Address,
    };

    public static DockerEndpoint Parse(string? endpoint)
    {
        var value = (endpoint ?? "").Trim();
        if (value.Length == 0)
            value = DockerSocket.DefaultEndpoint;

        if (TryStrip(value, "tcp://", out var rest) || TryStrip(value, "http://", out rest))
            return new DockerEndpoint(DockerEndpointKind.Tcp, WithPort(rest.TrimEnd('/')));

        // Refused by name rather than attempted: TLS needs client certificates this does not
        // load, and ssh:// needs an ssh client. Trying either would fail with a message about
        // something else entirely.
        if (TryStrip(value, "https://", out _) || TryStrip(value, "ssh://", out _))
            throw new InvalidOperationException(
                $"\"{value}\" needs TLS or SSH, which LabbyTwo does not speak to Docker. Use a unix socket, " +
                "or a socket proxy on the same Docker network at tcp://name:2375.");

        if (value.StartsWith("npipe:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            // Everything after the last "/pipe/" is the name, however many slashes the
            // spelling put in front of it.
            var normalised = value.Replace('\\', '/');
            var marker = normalised.LastIndexOf("/pipe/", StringComparison.OrdinalIgnoreCase);
            var name = marker >= 0
                ? normalised[(marker + "/pipe/".Length)..]
                : normalised["npipe:".Length..].TrimStart('/');
            return new DockerEndpoint(DockerEndpointKind.NamedPipe, name);
        }

        if (TryStrip(value, "unix://", out rest))
            return new DockerEndpoint(DockerEndpointKind.Unix, rest);

        if (HostAndPort.IsMatch(value))
            return new DockerEndpoint(DockerEndpointKind.Tcp, value);

        return new DockerEndpoint(DockerEndpointKind.Unix, value);
    }

    private static string WithPort(string hostPort)
    {
        if (hostPort.Length == 0)
            throw new InvalidOperationException("A tcp:// Docker endpoint needs a host, like tcp://socket-proxy:2375.");

        // A bracketed IPv6 address has colons of its own; only one after the bracket is a port.
        var hasPort = hostPort.StartsWith('[')
            ? hostPort.Contains("]:", StringComparison.Ordinal)
            : hostPort.Contains(':');
        return hasPort ? hostPort : $"{hostPort}:{DefaultTcpPort}";
    }

    private static bool TryStrip(string value, string prefix, out string rest)
    {
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = value[prefix.Length..];
            return true;
        }

        rest = "";
        return false;
    }
}

/// <summary>
/// A socket proxy refused a call. Its own type, but still an
/// <see cref="InvalidOperationException"/>, so the code that treats "Docker said no" as "try
/// the next way" can let this one through: a proxy that forbids inspecting containers is a
/// setting to change, not a container that does not exist.
/// </summary>
public sealed class DockerProxyDeniedException(string method, string path)
    : InvalidOperationException(Explain(method, path))
{
    public string Method { get; } = method;
    public string Path { get; } = path;

    /// <summary>What a call is for, and which flags on the proxy let it through.</summary>
    /// <param name="What">In words, for the message.</param>
    /// <param name="Flags">The environment variables to set on the proxy.</param>
    public sealed record Rule(string What, string Flags);

    /// <summary>
    /// The flags are the ones linuxserver/socket-proxy and tecnativa/docker-socket-proxy share.
    /// They differ in one place that matters, and the message says so: linuxserver lets
    /// ALLOW_RESTARTS through with POST=0, while tecnativa refuses every non-GET until POST=1
    /// — and POST=1 beside CONTAINERS=1 allows creating containers too.
    /// </summary>
    public static Rule RuleFor(string method, string path)
    {
        var bare = Regex.Replace(path.Split('?', 2)[0], @"^/v[\d.]+(?=/)", "");
        var segments = bare.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var section = segments.Length > 0 ? segments[0].ToLowerInvariant() : "";
        var action = segments.Length > 2 ? segments[2].ToLowerInvariant() : "";
        var isGet = method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
                    method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

        if (section == "containers" && !isGet && action is "restart" or "stop" or "kill")
            return new Rule("restarting a container",
                "ALLOW_RESTARTS=1 (tecnativa/docker-socket-proxy also needs POST=1, which lets creating containers " +
                "through as well; linuxserver/socket-proxy does not)");

        if (section == "containers" && !isGet && action == "start")
            return new Rule("starting a container", "ALLOW_START=1 (tecnativa/docker-socket-proxy also needs POST=1)");

        if (section == "exec" || action == "exec")
            return new Rule("running a command in a container", "CONTAINERS=1, EXEC=1 and POST=1");

        if (section == "containers" && segments.Length > 1 && segments[1] == "create")
            return new Rule("creating a container",
                "CONTAINERS=1 and POST=1 — though that is as much power as the raw socket, so think twice");

        if (section == "images" && !isGet)
            return new Rule("pulling an image", "IMAGES=1 and POST=1");

        if (section == "containers" && isGet)
            return new Rule("reading containers", "CONTAINERS=1");

        if (section == "images" && isGet)
            return new Rule("reading image details", "IMAGES=1");

        var flag = section.Length > 0 ? section.ToUpperInvariant() + "=1" : "the matching section";
        return isGet
            ? new Rule($"reading /{section}", flag)
            : new Rule($"a {method.ToUpperInvariant()} to /{section}", $"{flag} and POST=1");
    }

    private static string Explain(string method, string path)
    {
        var rule = RuleFor(method, path);
        return $"The Docker socket proxy refused {method} {path.Split('?', 2)[0]}, which is {rule.What}. " +
               $"Enable {rule.Flags} on the proxy container, then recreate it.";
    }
}
