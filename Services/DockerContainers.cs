using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>What the Containers tab can do to one container.</summary>
public enum ContainerAction
{
    Start,
    Stop,
    Restart,
    Pause,
    Unpause,
    Remove,
}

public enum ContainerHealth
{
    None,
    Starting,
    Healthy,
    Unhealthy,
}

/// <param name="PublicPort">Null for a port the image exposes but nothing publishes.</param>
public sealed record DockerPort(string? Ip, int PrivatePort, int? PublicPort, string Type)
{
    public override string ToString() => PublicPort is { } published
        ? $"{published}→{PrivatePort}{(Type == "tcp" ? "" : "/" + Type)}"
        : $"{PrivatePort}{(Type == "tcp" ? "" : "/" + Type)}";
}

/// <summary>
/// One line of <c>GET /containers/json?all=1</c>, which is everything the list needs without
/// inspecting forty containers one at a time.
/// </summary>
/// <param name="Image">The reference it was created from — or, when that tag has since been
/// pulled again and now points elsewhere, the bare image id, which is how Docker says so.</param>
/// <param name="ImageId">The image actually running.</param>
/// <param name="Status">Docker's own sentence: "Up 3 hours (healthy)", "Exited (137) 2 days ago".</param>
public sealed record ContainerRow(
    string Id,
    string Name,
    string Image,
    string ImageId,
    string State,
    string Status,
    DateTimeOffset Created,
    IReadOnlyList<DockerPort> Ports,
    IReadOnlyDictionary<string, string> Labels)
{
    public const string ProjectLabel = "com.docker.compose.project";
    public const string ServiceLabel = "com.docker.compose.service";

    public string ShortId => Id.Length > 12 ? Id[..12] : Id;
    public string? Project => Labels.TryGetValue(ProjectLabel, out var project) && project.Length > 0 ? project : null;
    public string? Service => Labels.TryGetValue(ServiceLabel, out var service) && service.Length > 0 ? service : null;

    public bool IsRunning => State == "running";
    public bool IsPaused => State == "paused";
    public bool IsRestarting => State == "restarting";

    /// <summary>Exited, created or dead: not running, and nothing is trying to make it run.</summary>
    public bool IsStopped => State is "exited" or "created" or "dead";

    /// <summary>
    /// The health check's verdict. The list does not carry it as a field, only inside
    /// <see cref="Status"/>, and reading it from there saves an inspect per container.
    /// </summary>
    public ContainerHealth Health =>
        Status.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase) ? ContainerHealth.Unhealthy
        : Status.Contains("(healthy)", StringComparison.OrdinalIgnoreCase) ? ContainerHealth.Healthy
        : Status.Contains("health: starting", StringComparison.OrdinalIgnoreCase) ? ContainerHealth.Starting
        : ContainerHealth.None;

    private static readonly Regex ExitPattern = new(@"^Exited \((-?\d+)\)", RegexOptions.CultureInvariant);

    public int? ExitCode => ExitPattern.Match(Status) is { Success: true } match &&
                            int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var code)
        ? code
        : null;

    /// <summary>"Up 3 hours" without the health note, which the row shows as a badge of its own.</summary>
    public string Uptime => Regex.Replace(Status, @"\s*\((healthy|unhealthy|health: starting)\)", "",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();

    /// <summary>Whether the image reference is a bare id — the tag it was created from has moved on.</summary>
    public bool ImageIsId => Image.StartsWith("sha256:", StringComparison.Ordinal) ||
                             Regex.IsMatch(Image, "^[0-9a-f]{12,64}$", RegexOptions.CultureInvariant);
}

/// <summary>Containers in one Compose project, or the ones in none.</summary>
public sealed record ContainerGroup(string? Project, IReadOnlyList<ContainerRow> Containers)
{
    public string Key => Project ?? "";
    public string Title => Project ?? "Not in a Compose project";
    public int Running => Containers.Count(c => c.IsRunning);
}

/// <summary>One reading of a container's resources, as the row shows it.</summary>
/// <param name="CpuPercent">Null until there are two samples to difference.</param>
/// <param name="RxPerSecond">Null on the first sample, for the same reason.</param>
public sealed record ContainerStats(
    double? CpuPercent,
    ulong MemoryUsed,
    ulong MemoryLimit,
    ulong RxBytes,
    ulong TxBytes,
    DateTimeOffset At,
    double? RxPerSecond = null,
    double? TxPerSecond = null);

/// <summary>The parts of <c>GET /containers/{id}/json</c> the inspect panel shows.</summary>
public sealed record ContainerDetails(
    string Id,
    string Name,
    string Image,
    string ImageId,
    bool Tty,
    DateTimeOffset? Created,
    DateTimeOffset? Started,
    DateTimeOffset? Finished,
    string RestartPolicy,
    int RestartRetries,
    IReadOnlyList<string> Environment,
    IReadOnlyList<ContainerMount> Mounts,
    IReadOnlyList<ContainerNetwork> Networks,
    IReadOnlyDictionary<string, string> Labels,
    string Command);

public sealed record ContainerMount(string Type, string Source, string Destination, bool ReadWrite);

public sealed record ContainerNetwork(string Name, string IpAddress, IReadOnlyList<string> Aliases);

/// <summary>
/// What an endpoint lets the Containers tab do. Worked out once, by asking, rather than
/// discovered one refused button at a time: a socket proxy that forbids stopping will
/// forbid it for every container, so greying the buttons out beats letting each fail.
/// </summary>
public sealed class DockerCapabilities
{
    /// <summary>Why each refused feature is refused, keyed by feature — the proxy's message, flag and all.</summary>
    public IReadOnlyDictionary<string, string> Denied { get; init; } = new Dictionary<string, string>();

    public DateTimeOffset CheckedAt { get; init; }

    public const string Logs = "logs";
    public const string Stats = "stats";
    public const string Inspect = "inspect";
    public const string Images = "images";

    public static string KeyFor(ContainerAction action) => action.ToString().ToLowerInvariant();

    public bool Allows(string feature) => !Denied.ContainsKey(feature);
    public bool Allows(ContainerAction action) => Allows(KeyFor(action));
    public string? Why(string feature) => Denied.TryGetValue(feature, out var why) ? why : null;
    public string? Why(ContainerAction action) => Why(KeyFor(action));

    /// <summary>Everything allowed — the raw socket, or before anything has been checked.</summary>
    public static readonly DockerCapabilities All = new();
}

/// <summary>
/// The Docker API calls behind the Containers tab, and the arithmetic on what comes back.
///
/// Static, like <see cref="DockerSocket"/>, with two pieces of shared state: the capability
/// answers and the previous stats sample per container. Both are per endpoint rather than
/// per page, so two people with the tab open do not each probe the proxy or each ask for the
/// same forty stats — and a stats cycle is gated to a few requests at a time per endpoint,
/// because a NAS answering forty at once is the thing being monitored falling over.
/// </summary>
public static class DockerContainers
{
    /// <summary>
    /// A name Docker cannot give a container — names start with a letter or digit — but that
    /// a socket proxy's path patterns still match. Asking to stop it is harmless everywhere:
    /// the proxy answers 403 if it would refuse the real thing, and Docker answers 404.
    /// </summary>
    public const string ProbeName = "-labbytwo-capability-probe";

    /// <summary>How many stats requests one endpoint gets at once.</summary>
    public const int StatsConcurrency = 4;

    private static readonly TimeSpan CapabilityLifetime = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, DockerCapabilities> CapabilityCache = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StatsGates = new();
    private static readonly ConcurrentDictionary<string, RawSample> Previous = new();
    private static readonly ConcurrentDictionary<string, ContainerStats> Recent = new();

    /// <summary>
    /// How long a stats reading is reused. A little under the tab's polling interval, so a
    /// second viewer is served from the first viewer's reading instead of doubling the load.
    /// </summary>
    private static readonly TimeSpan StatsReuse = TimeSpan.FromSeconds(8);

    /// <summary>Stopping a container waits for it to exit, and ten seconds' grace is Docker's default.</summary>
    public static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(60);

    // ---- listing ------------------------------------------------------------------

    public static async Task<IReadOnlyList<ContainerRow>> ListAsync(string endpoint, TimeSpan timeout, CancellationToken ct)
    {
        var payload = await DockerSocket.GetAsync(endpoint, timeout, "/containers/json?all=1", ct);
        return ParseList(payload);
    }

    /// <summary>
    /// How long one list answers every other reader of the same endpoint. The Containers tab
    /// polls on its own, but a runbook's <c>{{containers}}</c> redraws on every sweep, on
    /// every open page; ten pages must not mean ten requests to the NAS every thirty seconds.
    /// </summary>
    public static readonly TimeSpan SharedListReuse = TimeSpan.FromSeconds(10);

    // A plain dictionary under a lock rather than a concurrent one: deciding to start a
    // request is a side effect, and ConcurrentDictionary may run a factory twice for two
    // callers arriving together — which would be two requests, the thing this prevents.
    private static readonly Dictionary<string, (DateTimeOffset At, Task<IReadOnlyList<ContainerRow>> List)> SharedLists = new(StringComparer.Ordinal);

    /// <summary>
    /// <see cref="ListAsync"/>, shared: a request already running, or one that finished less
    /// than <see cref="SharedListReuse"/> ago, answers instead of a new one. A failure is shared
    /// for the same few seconds, so an endpoint that is down is not asked again by every page
    /// the moment it said no. Runs on the pool whatever thread asks, and cancelling stops only
    /// this caller waiting.
    /// </summary>
    public static Task<IReadOnlyList<ContainerRow>> SharedListAsync(string endpoint, TimeSpan timeout, CancellationToken ct = default)
    {
        Task<IReadOnlyList<ContainerRow>> list;
        lock (SharedLists)
        {
            var now = DateTimeOffset.UtcNow;
            if (SharedLists.TryGetValue(endpoint, out var existing) && (!existing.List.IsCompleted || now - existing.At < SharedListReuse))
            {
                list = existing.List;
            }
            else
            {
                list = Task.Run(() => ListAsync(endpoint, timeout, CancellationToken.None));
                SharedLists[endpoint] = (now, list);
            }
        }
        return list.WaitAsync(ct);
    }

    /// <summary>Forgets shared lists, so the next reader asks again. For tests, and after an action.</summary>
    public static void ForgetSharedLists()
    {
        lock (SharedLists)
            SharedLists.Clear();
    }

    /// <summary>
    /// Why a Docker call failed, as the Containers tab says it: a socket proxy's refusal with
    /// the flag that fixes it, or the connection error in words.
    /// </summary>
    public static string Explain(Exception ex, string endpoint) =>
        ex.GetBaseException() is DockerProxyDeniedException denied
            ? denied.Message
            : ProbeError.Describe(ex, endpoint);

    public static IReadOnlyList<ContainerRow> ParseList(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The Docker API returned an unexpected response.");

        var rows = new List<ContainerRow>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var id = Str(entry, "Id");
            var name = entry.TryGetProperty("Names", out var names) && names.ValueKind == JsonValueKind.Array &&
                       names.GetArrayLength() > 0
                ? names[0].GetString()?.TrimStart('/') ?? id
                : id.Length > 12 ? id[..12] : id;

            var ports = new List<DockerPort>();
            if (entry.TryGetProperty("Ports", out var portList) && portList.ValueKind == JsonValueKind.Array)
            {
                foreach (var port in portList.EnumerateArray())
                {
                    ports.Add(new DockerPort(
                        port.TryGetProperty("IP", out var ip) ? ip.GetString() : null,
                        port.TryGetProperty("PrivatePort", out var inside) && inside.TryGetInt32(out var p) ? p : 0,
                        port.TryGetProperty("PublicPort", out var outside) && outside.TryGetInt32(out var q) ? q : null,
                        port.TryGetProperty("Type", out var type) ? type.GetString() ?? "tcp" : "tcp"));
                }
            }

            // Docker lists a published port once per address family, so 0.0.0.0 and :: both
            // appear. The same mapping twice is noise in a narrow column.
            var distinct = ports
                .GroupBy(p => (p.PrivatePort, p.PublicPort, p.Type))
                .Select(g => g.First())
                .OrderBy(p => p.PublicPort is null)
                .ThenBy(p => p.PublicPort ?? p.PrivatePort)
                .ToList();

            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (entry.TryGetProperty("Labels", out var labelMap) && labelMap.ValueKind == JsonValueKind.Object)
                foreach (var label in labelMap.EnumerateObject())
                    labels[label.Name] = label.Value.GetString() ?? "";

            var created = entry.TryGetProperty("Created", out var when) && when.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : DateTimeOffset.MinValue;

            rows.Add(new ContainerRow(id, name, Str(entry, "Image"), Str(entry, "ImageID"), Str(entry, "State"),
                Str(entry, "Status"), created, distinct, labels));
        }
        return rows;
    }

    // ---- grouping, filtering, sorting ---------------------------------------------

    public enum SortBy
    {
        Name,
        State,
        Cpu,
        Memory,
        Created,
    }

    /// <summary>
    /// Compose projects in name order, then everything that belongs to none. A project is
    /// how people think about a stack — "the media stack", "git" — so that is the unit the
    /// page is arranged in, and the unit the bulk buttons act on.
    /// </summary>
    public static IReadOnlyList<ContainerGroup> Group(
        IEnumerable<ContainerRow> rows, SortBy sort = SortBy.Name, Func<string, ContainerStats?>? stats = null)
    {
        return
        [
            .. rows
                .GroupBy(r => r.Project ?? "", StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key.Length == 0)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ContainerGroup(g.Key.Length == 0 ? null : g.First().Project, Sort(g, sort, stats))),
        ];
    }

    public static IReadOnlyList<ContainerRow> Sort(
        IEnumerable<ContainerRow> rows, SortBy sort, Func<string, ContainerStats?>? stats = null)
    {
        stats ??= _ => null;
        var byName = StringComparer.OrdinalIgnoreCase;
        return sort switch
        {
            // Trouble first: unhealthy, restarting, then what is running, then what is not.
            SortBy.State => [.. rows.OrderBy(StateRank).ThenBy(r => r.Name, byName)],
            SortBy.Cpu => [.. rows.OrderByDescending(r => stats(r.Id)?.CpuPercent ?? -1).ThenBy(r => r.Name, byName)],
            SortBy.Memory => [.. rows.OrderByDescending(r => stats(r.Id)?.MemoryUsed ?? 0).ThenBy(r => r.Name, byName)],
            SortBy.Created => [.. rows.OrderByDescending(r => r.Created).ThenBy(r => r.Name, byName)],
            _ => [.. rows.OrderBy(r => r.Name, byName)],
        };
    }

    private static int StateRank(ContainerRow row) =>
        row.Health == ContainerHealth.Unhealthy ? 0
        : row.IsRestarting ? 1
        : row.IsRunning ? 2
        : row.IsPaused ? 3
        : 4;

    /// <summary>
    /// The search box and the state filter. Text matches the name, the image, the Compose
    /// project and service — whatever somebody is likely to remember a container by.
    /// </summary>
    /// <param name="state">all, running, stopped, paused, restarting or unhealthy.</param>
    public static bool Matches(ContainerRow row, string? text, string? state)
    {
        var passesState = (state ?? "all") switch
        {
            "running" => row.IsRunning,
            "stopped" => row.IsStopped,
            "paused" => row.IsPaused,
            "restarting" => row.IsRestarting,
            "unhealthy" => row.Health == ContainerHealth.Unhealthy,
            _ => true,
        };
        if (!passesState)
            return false;

        if (string.IsNullOrWhiteSpace(text))
            return true;

        var needle = text.Trim();
        return Contains(row.Name) || Contains(row.Image) || Contains(row.Project) || Contains(row.Service);

        bool Contains(string? haystack) =>
            haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    // ---- actions ------------------------------------------------------------------

    /// <summary>The Docker API call behind each action. Kept as data so a test can pin it.</summary>
    public static (HttpMethod Method, string Path) Request(ContainerAction action, string id)
    {
        var target = Uri.EscapeDataString(id);
        return action switch
        {
            ContainerAction.Start => (HttpMethod.Post, $"/containers/{target}/start"),
            ContainerAction.Stop => (HttpMethod.Post, $"/containers/{target}/stop"),
            ContainerAction.Restart => (HttpMethod.Post, $"/containers/{target}/restart"),
            ContainerAction.Pause => (HttpMethod.Post, $"/containers/{target}/pause"),
            ContainerAction.Unpause => (HttpMethod.Post, $"/containers/{target}/unpause"),

            // No force and no v=1: a running container is refused rather than killed, and
            // its anonymous volumes are left for somebody to decide about deliberately.
            ContainerAction.Remove => (HttpMethod.Delete, $"/containers/{target}"),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    /// <summary>Whether the action makes sense for the container's current state.</summary>
    public static bool Applies(ContainerAction action, ContainerRow row) => action switch
    {
        ContainerAction.Start => row.IsStopped && row.State != "dead",
        ContainerAction.Stop => row.IsRunning || row.IsRestarting || row.IsPaused,
        ContainerAction.Restart => row.IsRunning || row.IsRestarting,
        ContainerAction.Pause => row.IsRunning,
        ContainerAction.Unpause => row.IsPaused,
        ContainerAction.Remove => row.IsStopped,
        _ => false,
    };

    public static string Verb(ContainerAction action) => action switch
    {
        ContainerAction.Start => "Start",
        ContainerAction.Stop => "Stop",
        ContainerAction.Restart => "Restart",
        ContainerAction.Pause => "Pause",
        ContainerAction.Unpause => "Unpause",
        ContainerAction.Remove => "Remove",
        _ => action.ToString(),
    };

    public static string PastTense(ContainerAction action) => action switch
    {
        ContainerAction.Start => "Started",
        ContainerAction.Stop => "Stopped",
        ContainerAction.Restart => "Restarted",
        ContainerAction.Pause => "Paused",
        ContainerAction.Unpause => "Unpaused",
        ContainerAction.Remove => "Removed",
        _ => action.ToString(),
    };

    public static async Task RunAsync(string endpoint, ContainerAction action, string id, CancellationToken ct)
    {
        var (method, path) = Request(action, id);
        try
        {
            await DockerSocket.SendAsync(endpoint, ActionTimeout, method, path, null, ct);
        }
        catch (DockerProxyDeniedException ex)
        {
            // The probe said yes and the real call said no — the proxy was changed in
            // between. Remember the new answer so the button greys out instead of failing again.
            Remember(endpoint, KeyFor(action), ex.Message);
            throw;
        }
    }

    private static string KeyFor(ContainerAction action) => DockerCapabilities.KeyFor(action);

    // ---- capabilities -------------------------------------------------------------

    /// <summary>The probes, as (feature, method, path). Each targets <see cref="ProbeName"/>.</summary>
    public static IReadOnlyList<(string Feature, HttpMethod Method, string Path)> Probes()
    {
        var probes = new List<(string, HttpMethod, string)>();
        foreach (var action in Enum.GetValues<ContainerAction>())
        {
            var (method, path) = Request(action, ProbeName);
            probes.Add((KeyFor(action), method, path));
        }

        probes.Add((DockerCapabilities.Inspect, HttpMethod.Get, $"/containers/{ProbeName}/json"));
        probes.Add((DockerCapabilities.Logs, HttpMethod.Get, $"/containers/{ProbeName}/logs?stdout=1&tail=1"));
        probes.Add((DockerCapabilities.Stats, HttpMethod.Get, $"/containers/{ProbeName}/stats?stream=false&one-shot=true"));
        probes.Add((DockerCapabilities.Images, HttpMethod.Get, $"/images/{ProbeName}/json"));
        return probes;
    }

    /// <summary>
    /// What this endpoint allows, asked once and remembered for ten minutes. Changing a
    /// proxy's flags means recreating it, which is rare enough that re-asking on every page
    /// load would be ten requests for nothing; <paramref name="fresh"/> is the button that
    /// asks again after you have.
    ///
    /// Only a proxy's refusal counts as "no". Docker's 404 for the made-up name means the
    /// call got through, and a connection that failed outright means nothing was learned,
    /// so that answer is not cached.
    /// </summary>
    public static async Task<DockerCapabilities> CapabilitiesAsync(
        string endpoint, TimeSpan timeout, bool fresh, CancellationToken ct)
    {
        if (!fresh && CapabilityCache.TryGetValue(endpoint, out var cached) &&
            DateTimeOffset.UtcNow - cached.CheckedAt < CapabilityLifetime)
            return cached;

        var denied = new ConcurrentDictionary<string, string>();
        var reachable = true;

        await Task.WhenAll(Probes().Select(async probe =>
        {
            try
            {
                await DockerSocket.SendAsync(endpoint, timeout, probe.Method, probe.Path, null, ct);
            }
            catch (DockerProxyDeniedException)
            {
                denied[probe.Feature] = DockerProxyDeniedException.Advise(probe.Method.Method, probe.Path);
            }
            catch (InvalidOperationException)
            {
                // Docker's own answer — "No such container" — which is the call arriving.
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                reachable = false;
            }
        }));

        var answer = new DockerCapabilities
        {
            Denied = new Dictionary<string, string>(denied),
            CheckedAt = DateTimeOffset.UtcNow,
        };

        if (reachable)
            CapabilityCache[endpoint] = answer;
        return answer;
    }

    /// <summary>What is known about an endpoint without asking — everything allowed if nothing is.</summary>
    public static DockerCapabilities CachedCapabilities(string endpoint) =>
        CapabilityCache.TryGetValue(endpoint, out var known) ? known : DockerCapabilities.All;

    private static void Remember(string endpoint, string feature, string why)
    {
        CapabilityCache.AddOrUpdate(endpoint,
            _ => new DockerCapabilities
            {
                Denied = new Dictionary<string, string> { [feature] = why },
                CheckedAt = DateTimeOffset.UtcNow,
            },
            (_, existing) => new DockerCapabilities
            {
                Denied = new Dictionary<string, string>(existing.Denied) { [feature] = why },
                CheckedAt = existing.CheckedAt,
            });
    }

    // ---- inspect ------------------------------------------------------------------

    public static async Task<ContainerDetails> InspectAsync(string endpoint, TimeSpan timeout, string id, CancellationToken ct)
    {
        var payload = await DockerSocket.GetAsync(endpoint, timeout, $"/containers/{Uri.EscapeDataString(id)}/json", ct);
        return ParseDetails(payload);
    }

    public static ContainerDetails ParseDetails(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var config = root.TryGetProperty("Config", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        var host = root.TryGetProperty("HostConfig", out var h) && h.ValueKind == JsonValueKind.Object ? h : default;
        var state = root.TryGetProperty("State", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;

        var env = new List<string>();
        if (config.ValueKind == JsonValueKind.Object && config.TryGetProperty("Env", out var envList) &&
            envList.ValueKind == JsonValueKind.Array)
            env.AddRange(envList.EnumerateArray().Select(e => e.GetString() ?? "").Where(e => e.Length > 0));

        var mounts = new List<ContainerMount>();
        if (root.TryGetProperty("Mounts", out var mountList) && mountList.ValueKind == JsonValueKind.Array)
        {
            foreach (var mount in mountList.EnumerateArray())
            {
                var type = Str(mount, "Type");
                var source = type == "volume" && Str(mount, "Name") is { Length: > 0 } volume ? volume : Str(mount, "Source");
                mounts.Add(new ContainerMount(type, source, Str(mount, "Destination"),
                    !mount.TryGetProperty("RW", out var rw) || rw.ValueKind != JsonValueKind.False));
            }
        }

        var networks = new List<ContainerNetwork>();
        if (root.TryGetProperty("NetworkSettings", out var settings) && settings.ValueKind == JsonValueKind.Object &&
            settings.TryGetProperty("Networks", out var networkMap) && networkMap.ValueKind == JsonValueKind.Object)
        {
            foreach (var network in networkMap.EnumerateObject())
            {
                var aliases = network.Value.TryGetProperty("Aliases", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                    : [];
                networks.Add(new ContainerNetwork(network.Name, Str(network.Value, "IPAddress"), aliases));
            }
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (config.ValueKind == JsonValueKind.Object && config.TryGetProperty("Labels", out var labelMap) &&
            labelMap.ValueKind == JsonValueKind.Object)
            foreach (var label in labelMap.EnumerateObject())
                labels[label.Name] = label.Value.GetString() ?? "";

        var policy = host.ValueKind == JsonValueKind.Object && host.TryGetProperty("RestartPolicy", out var p) &&
                     p.ValueKind == JsonValueKind.Object
            ? p
            : default;

        var command = new List<string>();
        if (config.ValueKind == JsonValueKind.Object)
        {
            foreach (var part in new[] { "Entrypoint", "Cmd" })
                if (config.TryGetProperty(part, out var list) && list.ValueKind == JsonValueKind.Array)
                    command.AddRange(list.EnumerateArray().Select(x => x.GetString() ?? ""));
        }

        return new ContainerDetails(
            Str(root, "Id"),
            Str(root, "Name").TrimStart('/'),
            config.ValueKind == JsonValueKind.Object ? Str(config, "Image") : "",
            Str(root, "Image"),
            config.ValueKind == JsonValueKind.Object && config.TryGetProperty("Tty", out var tty) &&
            tty.ValueKind == JsonValueKind.True,
            Time(root, "Created"),
            state.ValueKind == JsonValueKind.Object ? Time(state, "StartedAt") : null,
            state.ValueKind == JsonValueKind.Object ? Time(state, "FinishedAt") : null,
            policy.ValueKind == JsonValueKind.Object ? Str(policy, "Name") : "",
            policy.ValueKind == JsonValueKind.Object && policy.TryGetProperty("MaximumRetryCount", out var retries) &&
            retries.TryGetInt32(out var r) ? r : 0,
            env,
            mounts,
            networks,
            labels,
            string.Join(' ', command.Where(x => x.Length > 0)));
    }

    /// <summary>
    /// Docker writes "0001-01-01T00:00:00Z" for a time that has not happened — a container
    /// never started, or never stopped. That is not a date to show anybody.
    /// </summary>
    private static DateTimeOffset? Time(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var parsed = DockerTime.Parse(value.GetString());
        return parsed is { Year: > 1 } ? parsed : null;
    }

    // ---- stats --------------------------------------------------------------------

    /// <summary>
    /// CPU use as <c>docker stats</c> reports it: the container's share of the time every CPU
    /// on the host spent, scaled so one busy core is 100%. Null when there is nothing to
    /// difference — the first sample, or counters that went backwards because the container
    /// restarted between them.
    /// </summary>
    public static double? CpuPercent(ulong cpuTotal, ulong previousCpuTotal, ulong system, ulong previousSystem, int onlineCpus)
    {
        if (system <= previousSystem || cpuTotal < previousCpuTotal || onlineCpus <= 0)
            return null;

        var cpuDelta = (double)(cpuTotal - previousCpuTotal);
        var systemDelta = (double)(system - previousSystem);
        return cpuDelta / systemDelta * onlineCpus * 100.0;
    }

    /// <summary>The counters one stats response carries, before anything is differenced.</summary>
    public sealed record RawSample(
        ulong CpuTotal,
        ulong SystemCpu,
        int OnlineCpus,
        ulong PreCpuTotal,
        ulong PreSystemCpu,
        ulong MemoryUsed,
        ulong MemoryLimit,
        ulong RxBytes,
        ulong TxBytes,
        DateTimeOffset Read);

    public static RawSample ParseSample(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var (cpuTotal, system, online) = Cpu(root, "cpu_stats");
        var (preTotal, preSystem, _) = Cpu(root, "precpu_stats");

        ulong used = 0, limit = 0;
        if (root.TryGetProperty("memory_stats", out var memory) && memory.ValueKind == JsonValueKind.Object)
        {
            used = U64(memory, "usage");
            limit = U64(memory, "limit");

            // What `docker stats` subtracts: the page cache the kernel can drop at will, which
            // would otherwise make every container that reads files look like it is leaking.
            // cgroup v1 calls it total_inactive_file, v2 inactive_file.
            if (memory.TryGetProperty("stats", out var detail) && detail.ValueKind == JsonValueKind.Object)
            {
                var cache = U64(detail, "total_inactive_file") is > 0 and var v1 ? v1 : U64(detail, "inactive_file");
                if (cache < used)
                    used -= cache;
            }
        }

        ulong rx = 0, tx = 0;
        if (root.TryGetProperty("networks", out var networks) && networks.ValueKind == JsonValueKind.Object)
        {
            foreach (var network in networks.EnumerateObject())
            {
                rx += U64(network.Value, "rx_bytes");
                tx += U64(network.Value, "tx_bytes");
            }
        }

        var read = root.TryGetProperty("read", out var at) && at.ValueKind == JsonValueKind.String &&
                   DockerTime.Parse(at.GetString()) is { Year: > 1 } parsed
            ? parsed
            : DateTimeOffset.UtcNow;

        return new RawSample(cpuTotal, system, online, preTotal, preSystem, used, limit, rx, tx, read);
    }

    private static (ulong Total, ulong System, int Online) Cpu(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var cpu) || cpu.ValueKind != JsonValueKind.Object)
            return (0, 0, 0);

        ulong total = 0;
        var perCpu = 0;
        if (cpu.TryGetProperty("cpu_usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            total = U64(usage, "total_usage");
            if (usage.TryGetProperty("percpu_usage", out var list) && list.ValueKind == JsonValueKind.Array)
                perCpu = list.GetArrayLength();
        }

        // online_cpus is missing on old kernels; the per-CPU list is what the CLI falls back to.
        var online = cpu.TryGetProperty("online_cpus", out var o) && o.TryGetInt32(out var n) && n > 0 ? n
            : perCpu > 0 ? perCpu
            : 1;
        return (total, U64(cpu, "system_cpu_usage"), online);
    }

    /// <summary>
    /// A sample turned into what the row shows. Docker's own precpu block is used when it
    /// has one — the first request, which waits a second to fill it — and the previous
    /// sample this process took otherwise, which is what lets every later request be a
    /// one-shot that answers straight away.
    /// </summary>
    public static ContainerStats Compute(RawSample sample, RawSample? previous)
    {
        double? cpu = null;
        if (sample.PreSystemCpu > 0)
            cpu = CpuPercent(sample.CpuTotal, sample.PreCpuTotal, sample.SystemCpu, sample.PreSystemCpu, sample.OnlineCpus);
        if (cpu is null && previous is not null)
            cpu = CpuPercent(sample.CpuTotal, previous.CpuTotal, sample.SystemCpu, previous.SystemCpu, sample.OnlineCpus);

        double? rxRate = null, txRate = null;
        if (previous is not null && sample.Read > previous.Read &&
            sample.RxBytes >= previous.RxBytes && sample.TxBytes >= previous.TxBytes)
        {
            var seconds = (sample.Read - previous.Read).TotalSeconds;
            rxRate = (sample.RxBytes - previous.RxBytes) / seconds;
            txRate = (sample.TxBytes - previous.TxBytes) / seconds;
        }

        return new ContainerStats(cpu, sample.MemoryUsed, sample.MemoryLimit, sample.RxBytes, sample.TxBytes,
            sample.Read, rxRate, txRate);
    }

    /// <summary>
    /// Stats for the given running containers, a few at a time. A container whose stats
    /// cannot be read is left out rather than failing the lot — it probably stopped between
    /// the list and now.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, ContainerStats>> StatsAsync(
        string endpoint, TimeSpan timeout, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var gate = StatsGates.GetOrAdd(endpoint, _ => new SemaphoreSlim(StatsConcurrency, StatsConcurrency));
        var results = new ConcurrentDictionary<string, ContainerStats>();

        await Task.WhenAll(ids.Select(async id =>
        {
            var key = endpoint + "|" + id;
            if (Recent.TryGetValue(key, out var recent) && DateTimeOffset.UtcNow - recent.At < StatsReuse)
            {
                results[id] = recent;
                return;
            }

            await gate.WaitAsync(ct);
            try
            {
                var previous = Previous.TryGetValue(key, out var last) ? last : null;

                // The first ask waits a second for Docker's own two samples; after that this
                // process has a previous sample of its own and asks for one-shot, which
                // answers at once — forty containers every ten seconds is then forty quick
                // requests, not forty that each hold a connection for a second.
                var path = $"/containers/{Uri.EscapeDataString(id)}/stats?stream=false" +
                           (previous is null ? "" : "&one-shot=true");
                var payload = await DockerSocket.GetAsync(endpoint, timeout, path, ct);
                var sample = ParseSample(payload);
                var computed = Compute(sample, previous);

                Previous[key] = sample;
                Recent[key] = computed with { At = DateTimeOffset.UtcNow };
                results[id] = computed;
            }
            catch (DockerProxyDeniedException ex)
            {
                Remember(endpoint, DockerCapabilities.Stats, ex.Message);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException
                                           or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
            }
            finally
            {
                gate.Release();
            }
        }));

        return results;
    }

    // ---- images: the update hint ---------------------------------------------------

    /// <summary>
    /// Containers running an older image than the one their tag now names locally — pulled,
    /// but not recreated. Read-only and local: <c>GET /images/json</c> once, plus an inspect
    /// for any container whose tag has moved so far that Docker lists it by id, because
    /// only the inspect still says which tag it came from. No registry is asked anything.
    /// </summary>
    /// <returns>Container id → the tag with a newer local image.</returns>
    public static async Task<IReadOnlyDictionary<string, string>> StaleImagesAsync(
        string endpoint, TimeSpan timeout, IReadOnlyList<ContainerRow> rows, CancellationToken ct)
    {
        var payload = await DockerSocket.GetAsync(endpoint, timeout, "/images/json", ct);
        var tags = ParseImageTags(payload);

        var references = new Dictionary<string, string>();
        foreach (var row in rows)
        {
            if (!row.ImageIsId)
            {
                references[row.Id] = row.Image;
                continue;
            }

            try
            {
                var details = await InspectAsync(endpoint, timeout, row.Id, ct);
                if (details.Image.Length > 0)
                    references[row.Id] = details.Image;
            }
            catch (InvalidOperationException)
            {
            }
        }

        return StaleImages(rows, references, tags);
    }

    /// <summary>Tag → image id, from <c>GET /images/json</c>.</summary>
    public static IReadOnlyDictionary<string, string> ParseImageTags(string payload)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return tags;

        foreach (var image in document.RootElement.EnumerateArray())
        {
            var id = Str(image, "Id");
            if (image.TryGetProperty("RepoTags", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var tag in list.EnumerateArray())
                    if (tag.GetString() is { Length: > 0 } name && name != "<none>:<none>")
                        tags[name] = id;
        }
        return tags;
    }

    public static IReadOnlyDictionary<string, string> StaleImages(
        IEnumerable<ContainerRow> rows, IReadOnlyDictionary<string, string> references,
        IReadOnlyDictionary<string, string> tags)
    {
        var stale = new Dictionary<string, string>();
        foreach (var row in rows)
        {
            if (!references.TryGetValue(row.Id, out var reference) || row.ImageId.Length == 0)
                continue;

            // "nginx" in a compose file is "nginx:latest" in the image list.
            var candidates = new[] { reference, NormaliseTag(reference) };
            var local = candidates.Select(c => tags.TryGetValue(c, out var id) ? id : null).FirstOrDefault(id => id is not null);
            if (local is not null && local != row.ImageId)
                stale[row.Id] = reference;
        }
        return stale;
    }

    private static string NormaliseTag(string reference)
    {
        if (reference.Contains('@'))
            return reference;
        var lastSlash = reference.LastIndexOf('/');
        var hasTag = reference.IndexOf(':', lastSlash + 1) >= 0;
        var tagged = hasTag ? reference : reference + ":latest";
        return tagged.StartsWith("docker.io/library/", StringComparison.Ordinal) ? tagged["docker.io/library/".Length..]
            : tagged.StartsWith("docker.io/", StringComparison.Ordinal) ? tagged["docker.io/".Length..]
            : tagged;
    }

    // ---- helpers ------------------------------------------------------------------

    /// <summary>Binary units, as <c>docker stats</c> shows memory: "412 MiB", "1.9 GiB".</summary>
    public static string Bytes(double bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:0} B" : bytes >= 100 ? $"{bytes:0} {units[unit]}" : $"{bytes:0.#} {units[unit]}";
    }

    private static string Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static ulong U64(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number)
            ? number
            : 0;
}

/// <summary>
/// Docker's timestamps, which carry nine digits of fractional second where .NET parses
/// seven. Cut down rather than rejected — nanoseconds are not something a log line needs.
/// </summary>
public static class DockerTime
{
    public static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();
        var dot = text.IndexOf('.');
        if (dot > 0)
        {
            var end = dot + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
                end++;
            var digits = end - dot - 1;
            if (digits > 7)
                text = text[..(dot + 8)] + text[end..];
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }
}
