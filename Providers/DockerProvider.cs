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
    public string Description => "Container counts and a live list from the Docker Engine API. Needs the socket mounted into LabbyTwo's container, or a socket proxy it can reach.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("endpoint", "Endpoint", FieldKind.Text, "/var/run/docker.sock", Default: "/var/run/docker.sock", Required: true,
            Help: "A unix socket path, a Windows named pipe (npipe://./pipe/docker_engine), or a TCP address (tcp://socket-proxy:2375). " +
                  "In Docker, either mount the socket (-v /var/run/docker.sock:/var/run/docker.sock) or, safer, run a socket " +
                  "proxy with CONTAINERS=1 (and ALLOW_RESTARTS=1 for the restart buttons) and point this at it."),
        new("timeout", "Timeout (seconds)", FieldKind.Number, Default: "10") { Advanced = true },
        new("stats_seconds", "Container resources every (seconds)", FieldKind.Number, Default: "60",
            Help: "How often each running container's CPU, memory, disk and network are read — one quick request per " +
                  "container, four at a time. 30 to 300; 0 turns it off. Through a socket proxy this needs CONTAINERS=1.")
        { Advanced = true },
        new("stats_top", "Containers to keep a history of", FieldKind.Number, Default: "10",
            Help: "Every running container is shown live, but only this many of the busiest (by CPU or by disk) are " +
                  "written to history, so forty containers do not become a hundred and sixty series. Idle ones never count. Up to 25.")
        { Advanced = true },
        new("stats_pinned", "Always keep a history of", FieldKind.Text, "tdarr, plex",
            Help: "Containers recorded whatever they are doing, on top of the busiest. Names, separated by commas.")
        { Advanced = true },
    ];

    /// <param name="Id">The full container id. Carried so a row can tell whether it is
    /// LabbyTwo's own container, and so a restart can name the one thing that is unambiguous
    /// — two stacks can each have a container called "app".</param>
    public sealed record ContainerInfo(string Name, string Image, string State, string Status, string Id = "");

    /// <summary>
    /// "No such file or directory" is a true but useless thing to show someone. By far the
    /// most common cause is that LabbyTwo is in a container and nobody mounted the socket,
    /// so say that, with the line to add.
    /// </summary>
    private static string Explain(Connection connection, Exception ex)
    {
        var endpoint = connection.Settings.Get("endpoint", "/var/run/docker.sock");
        var message = ex.GetBaseException().Message;

        // Already says which proxy flag to set, which is the whole fix.
        if (ex.GetBaseException() is DockerProxyDeniedException)
            return message;

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

        // The difference, reported rather than left to arithmetic: "how many are stopped"
        // is the number worth an alert, and a rule cannot subtract one metric from another.
        new("container_stopped", "Containers stopped"),

        new("latency_ms", "Response time", " ms"),

        // The measured metrics behind each container's own series (container_cpu:tdarr…),
        // which inherit these units through VolumeMetric's naming.
        new(ContainerUsage.CpuMetric, "Container CPU", "%", 1),
        new(ContainerUsage.MemoryMetric, "Container memory", " MiB"),
        new(ContainerUsage.ReadMetric, "Container disk reads", " MB/s", 1),
        new(ContainerUsage.WriteMetric, "Container disk writes", " MB/s", 1),
        new(ContainerUsage.BusiestCpuMetric, "Busiest container's CPU", "%", 1),
        new(ContainerUsage.BusiestReadMetric, "Busiest container's disk reads", " MB/s", 1),
        new(ContainerUsage.FullestMemoryMetric, "Container closest to its memory limit", "%", 1),
    ];

    /// <summary>
    /// The declared metrics plus one spec per container currently in the live readings, so
    /// pickers and alert messages say "tdarr: CPU" rather than a key, and the three "busiest"
    /// numbers name the container they are about right now — "Busiest container's CPU (tdarr)"
    /// is what an alert on any container should say when it fires.
    /// </summary>
    public IReadOnlyList<MetricSpec> MetricsFor(Connection connection)
    {
        if (ContainerResourceSnapshots.Get(connection.Id) is not { } snapshot || !snapshot.IsFresh(DateTimeOffset.UtcNow))
            return Metrics;

        var specs = Metrics.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
        void Name(string key, ContainerReading? who)
        {
            if (who is not null && specs.TryGetValue(key, out var spec))
                specs[key] = spec with { Label = $"{spec.Label} ({who.Name})" };
        }
        Name(ContainerUsage.BusiestCpuMetric, ContainerUsage.BusiestByCpu(snapshot.Readings));
        Name(ContainerUsage.BusiestReadMetric, snapshot.Readings.Where(r => r.IsRunning && r.ReadPerSecond is not null)
            .OrderByDescending(r => r.ReadPerSecond).FirstOrDefault());
        Name(ContainerUsage.FullestMemoryMetric, ContainerUsage.FullestMemory(snapshot.Readings, snapshot.HostMemory));

        var words = new Dictionary<string, string>
        {
            [ContainerUsage.CpuMetric] = "CPU",
            [ContainerUsage.MemoryMetric] = "memory",
            [ContainerUsage.ReadMetric] = "disk reads",
            [ContainerUsage.WriteMetric] = "disk writes",
        };
        foreach (var key in snapshot.Metrics.Keys)
        {
            if (!ContainerUsage.TryParse(key, out var metric, out var slug) || !specs.TryGetValue(metric, out var measured))
                continue;
            var name = snapshot.Readings.FirstOrDefault(r => VolumeMetric.Slug(r.Name) == slug)?.Name ?? slug;
            specs[key] = measured with { Key = key, Label = $"{name}: {words[metric]}" };
        }
        return [.. specs.Values];
    }

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Something has stopped", "container_stopped", Comparison.Above, 0, ForMinutes: 5,
            Why: "A container that exited and did not come back. Five minutes' grace, so an " +
                 "update recreating one does not count."),
        new("A container is eating the CPU", ContainerUsage.BusiestCpuMetric, Comparison.Above, 150, ClearThreshold: 100, ForMinutes: 30,
            Why: "One container using more than one and a half cores for half an hour — a transcoder or an indexer " +
                 "that will starve everything else on a small NAS. The alert names the container. Pair it with " +
                 "self-healing to pause “@busiest-cpu” if you want it dealt with."),
        new("A container is hammering the disks", ContainerUsage.BusiestReadMetric, Comparison.Above, 50, ClearThreshold: 30, ForMinutes: 30,
            Why: "One container reading more than 50 MB/s for half an hour. On spinning disks that is enough to make " +
                 "file shares stall. Lower it for a slower NAS."),
        new("A container is nearly out of memory", ContainerUsage.FullestMemoryMetric, Comparison.Above, 90, ClearThreshold: 85, ForMinutes: 10,
            Why: "A container above 90% of its own memory limit for ten minutes is about to be killed by the kernel. " +
                 "Only containers with a limit set count."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // The full list rather than ContainersAsync's short form, because it is shared:
            // the change watcher diffs it after the sweep, and a runbook's {{containers}}
            // draws from it, and neither then has to ask the host again.
            var endpoint = connection.Settings.Get("endpoint", DockerSocket.DefaultEndpoint);
            var containers = DockerContainers.ParseList(await GetAsync(connection, "/containers/json?all=1", ct));
            stopwatch.Stop();
            DockerContainers.Remember(endpoint, containers);

            var running = containers.Count(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase));
            var metrics = new Dictionary<string, double>
            {
                ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
                ["container_count"] = running,
                ["container_total"] = containers.Count,
                ["container_stopped"] = containers.Count - running,
            };
            var notRecorded = WithContainerResources(connection, metrics, DateTimeOffset.UtcNow);
            return ProbeResult.Up(stopwatch.Elapsed, $"{running} of {containers.Count} containers running", metrics)
                with { NotRecorded = notRecorded };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, Explain(connection, ex));
        }
    }

    /// <summary>
    /// Adds the resource poller's last round to a probe's metrics — memory only, no request —
    /// and says which of them not to write. The first probe after a round records the round's
    /// chosen containers and the three "busiest" numbers; every later probe before the next
    /// round passes the same values on as live state only, as does every lingering
    /// container that is no longer among the busiest. A round too old to be current is left
    /// out altogether, so a poller that stopped cannot keep an alert firing.
    /// </summary>
    /// <returns>The keys not to record, or null when everything is to be recorded.</returns>
    public static IReadOnlySet<string>? WithContainerResources(Connection connection, Dictionary<string, double> metrics, DateTimeOffset now)
    {
        if (ContainerResourceSnapshots.Get(connection.Id) is not { Error: null } snapshot || !snapshot.IsFresh(now))
            return null;

        foreach (var (key, value) in snapshot.Metrics)
            metrics[key] = value;

        var fresh = ContainerResourceSnapshots.TakeRound(connection.Id, snapshot.Round);
        var skip = snapshot.Metrics.Keys.Where(k => !fresh || !snapshot.RecordedKeys.Contains(k)).ToHashSet(StringComparer.Ordinal);
        return skip.Count == 0 ? null : skip;
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
                entry.TryGetProperty("Status", out var status) ? status.GetString() ?? "" : "",
                entry.TryGetProperty("Id", out var full) ? full.GetString() ?? "" : ""));
        }
        return [.. containers.OrderByDescending(c => c.State == "running").ThenBy(c => c.Name)];
    }

    /// <summary>
    /// Restarts one container.
    ///
    /// Restart rather than stop and start, and deliberately the only thing offered: every
    /// outcome of a restart is recoverable on its own, and a container this stopped would
    /// stay stopped until somebody found a terminal. Given the socket is root on the host,
    /// the smallest verb that does the job is the right one. It is also the one verb a socket
    /// proxy can grant on its own: linuxserver/socket-proxy's ALLOW_RESTARTS lets restart,
    /// stop and kill through while POST=0 keeps container creation shut.
    ///
    /// Addressed by id where there is one — two stacks can each own a container called
    /// "app", and a name that matches twice is the kind of ambiguity you find out about by
    /// restarting the wrong thing.
    /// </summary>
    public static async Task RestartAsync(Connection connection, ContainerInfo container, CancellationToken ct)
    {
        var target = container.Id is { Length: > 0 } id ? id : container.Name;

        await DockerSocket.PostAsync(
            connection.Settings.Get("endpoint", DockerSocket.DefaultEndpoint),
            // Longer than a probe: Docker holds the request open while the container stops,
            // and a slow one taking ten seconds to shut down is normal rather than a fault.
            TimeSpan.FromSeconds(60),
            $"/containers/{target}/restart", null, ct);
    }

    private static Task<string> GetAsync(Connection connection, string path, CancellationToken ct) =>
        DockerSocket.GetAsync(
            connection.Settings.Get("endpoint", DockerSocket.DefaultEndpoint),
            TimeSpan.FromSeconds(Math.Clamp(connection.Settings.GetInt("timeout", 10), 1, 120)),
            path, ct);
}
