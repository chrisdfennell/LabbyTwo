using System.Diagnostics;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Services;

namespace LabbyTwo.Providers;

/// <summary>
/// The Docker Engine API over a unix socket, a Windows named pipe, or TCP. Reports how
/// many containers are running and lists them for the container widget.
/// </summary>
public sealed class DockerProvider : IConnectionProvider
{
    public string Type => "docker";
    public string DisplayName => "Docker";
    public string Icon => "🐳";
    public string Category => "Infrastructure";
    public string Description => "Container counts and a live list from the Docker Engine API. Needs the socket mounted into LabbyTwo's container.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("endpoint", "Endpoint", FieldKind.Text, "/var/run/docker.sock", Default: "/var/run/docker.sock", Required: true,
            Help: "A unix socket path, a Windows named pipe (npipe://./pipe/docker_engine), or a TCP address (tcp://192.168.1.50:2375). " +
                  "In Docker, mount the socket: -v /var/run/docker.sock:/var/run/docker.sock"),
        new("timeout", "Timeout (seconds)", FieldKind.Number, Default: "10"),
    ];

    public sealed record ContainerInfo(string Name, string Image, string State, string Status);

    /// <summary>
    /// "No such file or directory" is a true but useless thing to show someone. By far the
    /// most common cause is that LabbyTwo is in a container and nobody mounted the socket,
    /// so say that, with the line to add.
    /// </summary>
    private static string Explain(Connection connection, Exception ex)
    {
        var endpoint = connection.Settings.Get("endpoint", "/var/run/docker.sock");
        var message = ex.GetBaseException().Message;

        // A path endpoint that is not there at all: either not mounted, or the wrong path.
        if (endpoint.StartsWith('/') && !File.Exists(endpoint) && !Directory.Exists(endpoint))
        {
            // Deliberately the override file, not docker-compose.yml: an update overwrites
            // that one, and a mount that disappears on upgrade is worse than no mount.
            return $"{endpoint} does not exist inside LabbyTwo's container — the socket is not mounted. " +
                   "Put this in docker-compose.override.yml, beside docker-compose.yml, then run " +
                   "`docker compose up -d`:\n" +
                   "  services:\n    labbytwo:\n      volumes:\n" +
                   "        - /var/run/docker.sock:/var/run/docker.sock:ro\n" +
                   "If your host keeps its socket somewhere else, change the left half of that line only.";
        }

        if (ex is UnauthorizedAccessException || message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return $"Permission denied on {endpoint}. The socket is mounted but LabbyTwo's user cannot read it — " +
                   "on most hosts it is owned by the docker group.";
        }

        return ProbeError.Describe(ex, endpoint);
    }

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("container_count", "Containers running"),
        new("container_total", "Containers defined"),
        new("latency_ms", "Response time", " ms"),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var containers = await ContainersAsync(connection, ct);
            stopwatch.Stop();

            var running = containers.Count(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase));
            return ProbeResult.Up(stopwatch.Elapsed,
                $"{running} of {containers.Count} containers running",
                new Dictionary<string, double>
                {
                    ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
                    ["container_count"] = running,
                    ["container_total"] = containers.Count,
                });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, Explain(connection, ex));
        }
    }

    public async Task<IReadOnlyList<ContainerInfo>> ContainersAsync(Connection connection, CancellationToken ct)
    {
        var payload = await GetAsync(connection, "/containers/json?all=1", ct);
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The Docker API returned an unexpected response.");

        var containers = new List<ContainerInfo>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            // Names come back as "/foo"; the leading slash is an artefact of the API.
            var name = entry.TryGetProperty("Names", out var names) && names.GetArrayLength() > 0
                ? names[0].GetString()?.TrimStart('/') ?? "?"
                : entry.TryGetProperty("Id", out var id) ? id.GetString()?[..12] ?? "?" : "?";

            containers.Add(new ContainerInfo(
                name,
                entry.TryGetProperty("Image", out var image) ? image.GetString() ?? "" : "",
                entry.TryGetProperty("State", out var state) ? state.GetString() ?? "" : "",
                entry.TryGetProperty("Status", out var status) ? status.GetString() ?? "" : ""));
        }
        return [.. containers.OrderByDescending(c => c.State == "running").ThenBy(c => c.Name)];
    }

    private static Task<string> GetAsync(Connection connection, string path, CancellationToken ct) =>
        DockerSocket.GetAsync(
            connection.Settings.Get("endpoint", DockerSocket.DefaultEndpoint),
            TimeSpan.FromSeconds(Math.Clamp(connection.Settings.GetInt("timeout", 10), 1, 120)),
            path, ct);
}
