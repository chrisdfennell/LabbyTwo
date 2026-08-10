using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

namespace LabbyTwo.Services;

/// <summary>
/// Speaks HTTP to the Docker Engine over whichever transport an endpoint names — a unix
/// socket, a Windows named pipe, or TCP.
///
/// Shared by the Docker provider, which only reads, and the self-updater, which creates a
/// container. Worth being blunt about the second one: anything that can reach this can
/// start a privileged container, which is root on the host. Mounting the socket read-only
/// does not change that — <c>:ro</c> protects the socket file, not the API behind it.
/// </summary>
public static class DockerSocket
{
    /// <summary>Where the socket lives on a normal Linux host, and inside a container that mounted it.</summary>
    public const string DefaultEndpoint = "/var/run/docker.sock";

    // Old enough for anything still running, new enough for everything used here.
    private const string ApiVersion = "/v1.41";

    public static async Task<string> GetAsync(string endpoint, TimeSpan timeout, string path, CancellationToken ct)
    {
        using var http = Client(endpoint, timeout);
        using var response = await http.GetAsync(ApiVersion + path, ct);
        return await ReadAsync(response, ct);
    }

    /// <param name="json">A JSON body, or null for the endpoints that take none.</param>
    public static async Task<string> PostAsync(
        string endpoint, TimeSpan timeout, string path, string? json, CancellationToken ct)
    {
        using var http = Client(endpoint, timeout);
        using var content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(ApiVersion + path, content, ct);
        return await ReadAsync(response, ct);
    }

    private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
            return body;

        // Docker puts a readable reason in {"message": "..."}, which beats the status code
        // on its own — "No such image" rather than "Docker answered HTTP 404".
        var reason = body;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var message))
                reason = message.GetString() ?? body;
        }
        catch (System.Text.Json.JsonException)
        {
        }

        throw new InvalidOperationException(
            reason.Length > 0 ? $"Docker: {reason.Trim()}" : $"Docker answered HTTP {(int)response.StatusCode}.");
    }

    /// <summary>
    /// A client per call is deliberate — these are cheap local connections, and pooling one
    /// per endpoint would mean tracking connection lifetimes for no real gain.
    /// </summary>
    private static HttpClient Client(string endpoint, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = timeout };
        string baseAddress;

        if (endpoint.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            baseAddress = "http://" + endpoint["tcp://".Length..].TrimEnd('/');
        }
        else if (endpoint.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase) ||
                 endpoint.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            var pipeName = endpoint
                .Replace("npipe://./pipe/", "", StringComparison.OrdinalIgnoreCase)
                .Replace(@"\\.\pipe\", "", StringComparison.OrdinalIgnoreCase);

            handler.ConnectCallback = async (_, token) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync((int)timeout.TotalMilliseconds, token);
                return pipe;
            };

            // The host is ignored once ConnectCallback takes over, but HttpClient still
            // needs a syntactically valid absolute URI to build the request line.
            baseAddress = "http://localhost";
        }
        else
        {
            handler.ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), token);
                return new NetworkStream(socket, ownsSocket: true);
            };
            baseAddress = "http://localhost";
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = timeout,
        };
    }
}
