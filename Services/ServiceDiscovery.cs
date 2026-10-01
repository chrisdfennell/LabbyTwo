using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>How a proposed address was arrived at, which decides how much to trust it.</summary>
public enum AddressKind
{
    /// <summary>The container's own <c>labbytwo.url</c> label said so.</summary>
    Label,

    /// <summary>LabbyTwo and the container share a user-defined network, so its name resolves.</summary>
    SharedNetwork,

    /// <summary>The container uses the host's network, so it is on the host's address.</summary>
    HostNetwork,

    /// <summary>No shared network, but the port is published on the host.</summary>
    PublishedPort,

    /// <summary>Neither — the address shown is what would work once LabbyTwo joins its network.</summary>
    Unreachable,
}

/// <summary>A URL LabbyTwo could reach a service at, and the reason in a sentence a person can check.</summary>
public sealed record ProposedAddress(string Url, AddressKind Kind, string Why);

/// <summary>
/// One container LabbyTwo recognises, with everything the "Add" button needs.
/// </summary>
/// <param name="Key">Stable across restarts and recreates — Docker host, container name and
/// provider — so an "Ignore" outlives the container being updated.</param>
/// <param name="Owner">Whose network it uses: itself, or the gateway container it shares a
/// namespace with (<c>network_mode: service:gluetun</c>).</param>
/// <param name="MatchedBy">Why it was recognised, for the row's small print.</param>
/// <param name="Existing">Connections of the same provider that already reach it.</param>
/// <param name="Others">Connections of another provider that reach it by name — a plain web check
/// of Sonarr, say. Worth mentioning; not a reason to hide the proper integration.</param>
public sealed record DiscoveredService(
    string Key,
    Connection Docker,
    ContainerRow Container,
    ContainerRow Owner,
    KnownService Service,
    IConnectionProvider Provider,
    string MatchedBy,
    ProposedAddress Address,
    IReadOnlyList<Connection> Existing,
    IReadOnlyList<Connection> Others,
    bool Ignored)
{
    public bool AlreadyAdded => Existing.Count > 0;

    /// <summary>Neither added nor ignored: the ones worth a person's attention.</summary>
    public bool Pending => !AlreadyAdded && !Ignored;

    /// <summary>
    /// Where the credential comes from, straight from the provider's own field help — the
    /// person who wrote the integration already wrote this down once, and a second copy
    /// here would only drift from it.
    /// </summary>
    public string? KeyHint => ServiceDiscovery.KeyHint(Provider);
}

/// <summary>
/// Notices the services running on LabbyTwo's Docker hosts that it has an integration for,
/// and offers to add them. Nothing here ever saves a connection: "Add" opens the ordinary
/// editor pre-filled, and the person reviews it, tests it and saves it — or does not.
///
/// It costs nothing it does not have to. The Docker probe already fetches every host's
/// container list each sweep and the change watcher already reads it back; that watcher hands
/// the same list to <see cref="Observe"/>, which keeps a reference and compares container ids
/// with the last look — no request, no database. Matching against the table happens only
/// when somebody looks at the result. The "Look for services" button is the one thing that
/// asks Docker, and it goes through the shared list, so a probe a moment ago answers it.
///
/// Credentials are deliberately left alone. Reading an API key out of another container's
/// environment or config file would work often enough to be tempting, and would make
/// LabbyTwo the thing on the network that collects everybody's keys — so each suggestion
/// says where the key is found instead, and the person copies it across.
/// </summary>
public sealed class ServiceDiscovery(ConfigStore config, Registry registry, AppSettingsStore settings, ILogger<ServiceDiscovery> log)
{
    /// <summary>App setting: the keys of suggestions somebody said no to, one per line.</summary>
    public const string IgnoredKey = "discovery_ignored";

    /// <summary>App setting: whether suggestions appear on their own, without pressing "Look for services".</summary>
    public const string AutoKey = "discovery_auto";

    /// <summary>App setting: the suggestions the Containers tab's hint was dismissed for, one per line.</summary>
    public const string HintDismissedKey = "discovery_hint_dismissed";

    /// <summary>
    /// Networks whose members cannot find each other by name. Docker's embedded DNS answers
    /// only on user-defined networks, so two containers both on the default bridge share a
    /// network and still cannot use it the way a proposal would.
    /// </summary>
    private static readonly HashSet<string> NoDns = new(StringComparer.OrdinalIgnoreCase) { "bridge", "host", "none" };

    /// <param name="Fingerprint">What changed means: the container ids and names, in order.</param>
    public sealed record Look(Connection Docker, IReadOnlyList<ContainerRow> Rows, DateTimeOffset At, string Fingerprint, string? Error = null);

    private readonly Dictionary<string, Look> _looks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _settingsGate = new(1, 1);

    /// <summary>Raised when a host's containers change, a look finishes, or the ignore list does.</summary>
    public event Action? Changed;

    /// <summary>The latest list from each Docker host, newest first. In memory only.</summary>
    public IReadOnlyList<Look> Looks
    {
        get
        {
            lock (_looks)
                return [.. _looks.Values.OrderByDescending(l => l.At)];
        }
    }

    /// <summary>
    /// Takes a list somebody else already fetched. Called by the change watcher after every
    /// sweep with the list the Docker probe put in the shared cache; it compares ids and
    /// names with the last one and says something only when they differ.
    /// </summary>
    public void Observe(Connection docker, IReadOnlyList<ContainerRow> rows, DateTimeOffset at)
    {
        var fingerprint = Fingerprint(rows);
        bool changed;
        lock (_looks)
        {
            changed = !_looks.TryGetValue(docker.Id, out var last) || last.Fingerprint != fingerprint || last.Error is not null;
            _looks[docker.Id] = new Look(docker, rows, at, fingerprint);
        }
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Forgets a host's list, so a deleted Docker connection's containers stop being offered.</summary>
    public void Forget(string dockerId)
    {
        bool removed;
        lock (_looks)
            removed = _looks.Remove(dockerId);
        if (removed)
            Changed?.Invoke();
    }

    public static string Fingerprint(IReadOnlyList<ContainerRow> rows) =>
        string.Join('\n', rows.Select(r => r.Id + " " + r.Name).Order(StringComparer.Ordinal));

    /// <summary>
    /// "Look for services": asks every enabled Docker connection for its list now. Through
    /// the shared list, so a host the probe asked seconds ago is not asked again. A host
    /// that cannot be asked keeps what it last said, with the reason beside it.
    /// </summary>
    public async Task LookAsync(CancellationToken ct = default)
    {
        var dockers = (await config.ConnectionsAsync(ct))
            .Where(c => string.Equals(c.Provider, "docker", StringComparison.OrdinalIgnoreCase) && c.Enabled)
            .ToList();

        foreach (var docker in dockers)
        {
            var endpoint = docker.Settings.Get("endpoint", DockerSocket.DefaultEndpoint);
            var timeout = TimeSpan.FromSeconds(Math.Clamp(docker.Settings.GetInt("timeout", 10), 1, 120));
            try
            {
                var rows = await DockerContainers.SharedListAsync(endpoint, timeout, ct);
                Observe(docker, rows, DateTimeOffset.Now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogDebug(ex, "Could not list the containers on {Connection} to look for services", docker.Name);
                lock (_looks)
                {
                    var rows = _looks.TryGetValue(docker.Id, out var last) ? last.Rows : [];
                    _looks[docker.Id] = new Look(docker, rows, DateTimeOffset.Now, last?.Fingerprint ?? "",
                        DockerContainers.Explain(ex, endpoint));
                }
            }
        }

        // Hosts that were deleted or switched off since.
        lock (_looks)
        {
            foreach (var id in _looks.Keys.Where(id => dockers.All(d => d.Id != id)).ToList())
                _looks.Remove(id);
        }
        Changed?.Invoke();
    }

    /// <summary>What every host's latest list offers, given the connections that exist and what was ignored.</summary>
    public IReadOnlyList<DiscoveredService> Found(
        IReadOnlyList<Connection> connections, IReadOnlySet<string> ignored, string? selfHint = null)
    {
        // Only hosts that are still configured and switched on, and as they are now — a
        // Docker connection deleted a moment ago must not keep offering its containers.
        var hint = selfHint ?? SelfContainer.Hint;
        return
        [
            .. Looks.SelectMany(look =>
                connections.FirstOrDefault(c => c.Id == look.Docker.Id && c.Enabled) is { } docker
                    ? Discover(docker, look.Rows, connections, registry.Provider, ignored, hint)
                    : []),
        ];
    }

    // ---- settings ---------------------------------------------------------------------

    public async Task<IReadOnlySet<string>> IgnoredAsync(CancellationToken ct = default) =>
        ParseKeys((await settings.AllAsync(ct)).Get(IgnoredKey));

    public async Task<bool> AutoAsync(CancellationToken ct = default) =>
        (await settings.AllAsync(ct)).GetBool(AutoKey, true);

    public async Task SetAutoAsync(bool on, CancellationToken ct = default) =>
        await settings.SaveAsync(AutoKey, on ? "true" : "false", ct);

    /// <summary>Ignores a suggestion, or brings one back. Remembered in app settings, so it travels with a backup.</summary>
    public Task SetIgnoredAsync(string key, bool ignore, CancellationToken ct = default) =>
        UpdateKeysAsync(IgnoredKey, keys =>
        {
            if (ignore)
                keys.Add(key);
            else
                keys.Remove(key);
        }, ct);

    public async Task<IReadOnlySet<string>> HintDismissedAsync(CancellationToken ct = default) =>
        ParseKeys((await settings.AllAsync(ct)).Get(HintDismissedKey));

    /// <summary>
    /// Dismisses the Containers tab's hint for these suggestions. For these only: a service
    /// started next week brings the hint back, which is the point of having one.
    /// </summary>
    public Task DismissHintAsync(IEnumerable<string> keys, CancellationToken ct = default) =>
        UpdateKeysAsync(HintDismissedKey, set => set.UnionWith(keys), ct);

    private async Task UpdateKeysAsync(string setting, Action<HashSet<string>> change, CancellationToken ct)
    {
        await _settingsGate.WaitAsync(ct);
        try
        {
            var keys = new HashSet<string>(ParseKeys((await settings.AllAsync(ct)).Get(setting)), StringComparer.Ordinal);
            change(keys);
            await settings.SaveAsync(setting, SerialiseKeys(keys), ct);
        }
        finally
        {
            _settingsGate.Release();
        }
        Changed?.Invoke();
    }

    public static IReadOnlySet<string> ParseKeys(string? stored) =>
        (stored ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

    public static string SerialiseKeys(IEnumerable<string> keys) => string.Join('\n', keys.Order(StringComparer.Ordinal));

    // ---- the pure part ----------------------------------------------------------------

    /// <summary>
    /// Everything recognisable on one host. Pure, so every rule below is testable with a
    /// list and no Docker: LabbyTwo's own container is skipped, so is anything whose provider
    /// is not installed, and a container can opt out with <c>labbytwo.discover=false</c>.
    /// </summary>
    public static IReadOnlyList<DiscoveredService> Discover(
        Connection docker,
        IReadOnlyList<ContainerRow> rows,
        IReadOnlyList<Connection> connections,
        Func<string, IConnectionProvider?> providers,
        IReadOnlySet<string> ignored,
        string selfHint)
    {
        var self = rows.FirstOrDefault(r => ContainerSafety.IsSelf(r, selfHint));
        var endpoint = docker.Settings.Get("endpoint", DockerSocket.DefaultEndpoint);
        var found = new List<DiscoveredService>();

        foreach (var row in rows)
        {
            if (ReferenceEquals(row, self) || Match(row) is not { } match || providers(match.Service.Provider) is not { } provider)
                continue;
            // An alert channel is not something a container is: a label naming one is a typo.
            if (provider is IAlertChannel)
                continue;

            var owner = NetworkOwner(row, rows);
            var address = Propose(row, match.Service, rows, self, endpoint);
            var key = $"{docker.Id}|{row.Name}|{match.Service.Provider}";
            var existing = AlreadyReaching(row, owner, match.Service, address, connections);
            var others = ContainerSafety.ConnectionsReaching(row, connections)
                .Where(c => !string.Equals(c.Provider, match.Service.Provider, StringComparison.OrdinalIgnoreCase))
                .ToList();

            found.Add(new DiscoveredService(key, docker, row, owner, match.Service, provider, match.MatchedBy, address,
                existing, others, ignored.Contains(key)));
        }

        return [.. found.OrderBy(f => f.Provider.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Container.Name)];
    }

    /// <summary>
    /// Which service a container is, if any. Labels first — a container that says what it
    /// is is right even when its image is somebody's private build — then the image, and
    /// last the container's or Compose service's exact name. The name is what is left when
    /// Docker lists the image as a bare id (the tag has been pulled again since), and it is
    /// exact on purpose: "sonarr" is Sonarr, "sonarr-exporter" is not.
    /// </summary>
    public static (KnownService Service, string MatchedBy)? Match(ContainerRow row)
    {
        if (IsFalse(row, KnownServices.DiscoverLabel) || IsFalse(row, KnownServices.EnableLabel))
            return null;

        int? labelledPort = row.Labels.TryGetValue(KnownServices.PortLabel, out var portText) &&
                            int.TryParse(portText, out var parsed) && parsed is > 0 and < 65536
            ? parsed
            : null;

        if (row.Labels.TryGetValue(KnownServices.ProviderLabel, out var labelled) && labelled.Trim() is { Length: > 0 } type)
        {
            var known = KnownServices.ForProvider(type) ??
                        new KnownService(type.ToLowerInvariant(), row.Ports.FirstOrDefault(p => p.Type == "tcp")?.PrivatePort ?? 80,
                            "http", []);
            return (known with { Port = labelledPort ?? known.Port }, $"its {KnownServices.ProviderLabel} label");
        }

        KnownService? service = null;
        var matchedBy = "";
        if (!row.ImageIsId && KnownServices.ForImage(row.Image) is { } byImage)
        {
            service = byImage;
            matchedBy = $"its image, {KnownServices.Repository(row.Image)}";
        }
        else if ((KnownServices.ForName(row.Service) ?? KnownServices.ForName(row.Name)) is { } byName)
        {
            service = byName;
            matchedBy = row.ImageIsId ? "its name — Docker lists its image only by id" : "its name";
        }

        return service is null ? null : (service with { Port = labelledPort ?? service.Port }, matchedBy);

        static bool IsFalse(ContainerRow row, string label) =>
            row.Labels.TryGetValue(label, out var value) && value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The container whose network this one uses. Compose's <c>network_mode: service:gluetun</c>
    /// reaches Docker as <c>container:&lt;id&gt;</c>, and such a container has no address of its
    /// own — it answers at the gateway container's name, on its own port.
    /// </summary>
    public static ContainerRow NetworkOwner(ContainerRow row, IReadOnlyList<ContainerRow> rows)
    {
        if (!row.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase))
            return row;

        var reference = row.NetworkMode["container:".Length..];
        return rows.FirstOrDefault(r => !ReferenceEquals(r, row) &&
                                        ((reference.Length >= 12 && r.Id.StartsWith(reference, StringComparison.OrdinalIgnoreCase)) ||
                                         r.Name.Equals(reference, StringComparison.OrdinalIgnoreCase)))
               ?? row;
    }

    /// <summary>
    /// The URL to propose, best first. A shared user-defined network is best by a distance:
    /// the name survives the container getting a new address, nothing has to be published,
    /// and it works on NAS firmware that will not forward between its own bridges. A
    /// published port on the host is the fallback. Failing both, the name-based address is
    /// still what is proposed — it is the one that will work once LabbyTwo joins the
    /// network, and the reason says so.
    /// </summary>
    /// <param name="self">LabbyTwo's own row in this host's list, or null when it is not a container here.</param>
    /// <param name="endpoint">The Docker connection's endpoint, which names the host when it is remote.</param>
    public static ProposedAddress Propose(
        ContainerRow row, KnownService service, IReadOnlyList<ContainerRow> rows, ContainerRow? self, string endpoint)
    {
        if (row.Labels.TryGetValue(KnownServices.UrlLabel, out var labelled) && labelled.Trim() is { Length: > 0 } url)
            return new ProposedAddress(url, AddressKind.Label, $"From the container's own {KnownServices.UrlLabel} label.");

        var owner = NetworkOwner(row, rows);
        var through = ReferenceEquals(owner, row) ? "" : $" It shares {owner.Name}'s network, so it answers at {owner.Name}'s name.";
        var scheme = service.Scheme;
        var port = service.Port;

        var ownerOnHost = owner.NetworkMode.Equals("host", StringComparison.OrdinalIgnoreCase);
        if (self is not null && !ownerOnHost)
        {
            var mine = UsableNetworks(self);
            var shared = UsableNetworks(owner).Where(mine.Contains).ToList();
            if (shared.Count > 0)
            {
                return new ProposedAddress($"{scheme}://{owner.Name}:{port}", AddressKind.SharedNetwork,
                    $"LabbyTwo and {owner.Name} are both on the “{shared[0]}” network, so Docker answers to the container's " +
                    $"name — nothing has to be published, and it keeps working when the container gets a new address.{through}");
            }
        }

        if (ownerOnHost)
        {
            var (host, why) = HostAddress(self, endpoint, rows, null);
            return new ProposedAddress($"{scheme}://{host}:{port}", AddressKind.HostNetwork,
                $"{owner.Name} runs with network_mode: host, so it listens on the host's own address — {why}.{through}");
        }

        var published = owner.Ports.FirstOrDefault(p => p.PrivatePort == port && p.PublicPort is not null && p.Type == "tcp");
        if (published?.PublicPort is { } outside)
        {
            var (host, why) = HostAddress(self, endpoint, rows, published);
            var sharing = self is null
                ? ""
                : " LabbyTwo shares no network with it, and reaching a published port from inside another container is refused " +
                  "by some NAS firmware — if Test times out, join its network instead (see “Use the container's name” in the README).";
            return new ProposedAddress($"{scheme}://{host}:{outside}", AddressKind.PublishedPort,
                $"{owner.Name} publishes port {port} as {outside} on the host; {host} is {why}.{sharing}{through}");
        }

        var networks = UsableNetworks(owner);
        var join = networks.Count > 0
            ? $"Add LabbyTwo to the “{networks[0]}” network and this address will work"
            : $"Put {owner.Name} and LabbyTwo on one user-defined network and this address will work";
        return new ProposedAddress($"{scheme}://{owner.Name}:{port}", AddressKind.Unreachable,
            $"LabbyTwo shares no network with {owner.Name} and port {port} is not published. {join}, or publish the port.{through}");
    }

    /// <summary>The networks a container can be found by name on: user-defined ones, not the default bridge.</summary>
    private static List<string> UsableNetworks(ContainerRow row) =>
        [.. row.Networks.Select(n => n.Name).Where(n => !NoDns.Contains(n))];

    /// <summary>
    /// The host's address as LabbyTwo would reach a published port on it, and why that one.
    /// Only from what is already known — the port's own binding, the Docker connection's
    /// endpoint, LabbyTwo's own network — because the Docker API does not say what the
    /// host's LAN address is.
    /// </summary>
    public static (string Host, string Why) HostAddress(ContainerRow? self, string endpoint, IReadOnlyList<ContainerRow> rows, DockerPort? port)
    {
        if (port?.Ip is { Length: > 0 } ip && ip is not "0.0.0.0" and not "::")
            return (ip.Contains(':') ? $"[{ip}]" : ip, "the address the port is published on");

        if (RemoteHost(endpoint) is { } remote && !rows.Any(r => r.Name.Equals(remote, StringComparison.OrdinalIgnoreCase)))
            return (remote, "the Docker host this connection talks to");

        if (self is null)
            return ("localhost", "this machine — LabbyTwo is not running in a container here, so the host's ports are its own");

        if (self.NetworkMode.Equals("host", StringComparison.OrdinalIgnoreCase))
            return ("localhost", "this machine — LabbyTwo runs with network_mode: host");

        if (self.Networks.FirstOrDefault(n => n.Gateway.Length > 0) is { } network)
            return (network.Gateway, $"the Docker host's address on LabbyTwo's own “{network.Name}” network; if Test fails, try the host's LAN address");

        return ("localhost", "a guess — replace it with the Docker host's LAN address");
    }

    /// <summary>The host a tcp:// or http(s):// Docker endpoint names, unless it is this machine.</summary>
    private static string? RemoteHost(string endpoint)
    {
        var text = endpoint.Trim();
        if (text.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            text = "http://" + text["tcp://".Length..];
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.IsLoopback)
            return null;
        return uri.Host;
    }

    /// <summary>
    /// Connections of this provider that already reach the container: by its name or its
    /// Compose service (the check <see cref="ContainerSafety.ConnectionsReaching"/> makes), by
    /// the gateway container's name on the service's own port, or by exactly the host and port
    /// being proposed — the published-port case, where no name appears at all.
    /// </summary>
    public static IReadOnlyList<Connection> AlreadyReaching(
        ContainerRow row, ContainerRow owner, KnownService service, ProposedAddress address, IReadOnlyList<Connection> connections)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { row.Name };
        if (row.Service is { } rowService)
            names.Add(rowService);
        var ownerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { owner.Name };
        if (owner.Service is { } ownerService)
            ownerNames.Add(ownerService);
        var proposed = HostAndPort(address.Url);

        return
        [
            .. connections.Where(connection =>
                string.Equals(connection.Provider, service.Provider, StringComparison.OrdinalIgnoreCase) &&
                connection.Settings.Values.Any(value =>
                {
                    if (ContainerSafety.HostOf(value) is not { } host)
                        return false;
                    if (names.Contains(host))
                        return true;
                    var (_, valuePort) = HostAndPort(value);
                    if (!ReferenceEquals(owner, row) && ownerNames.Contains(host) && valuePort == service.Port)
                        return true;
                    return proposed.Host is { } proposedHost && string.Equals(proposedHost, host, StringComparison.OrdinalIgnoreCase) &&
                           valuePort == proposed.Port;
                })),
        ];
    }

    /// <summary>The host and port of a URL, or of a bare host:port; the scheme's port when none is written.</summary>
    private static (string? Host, int? Port) HostAndPort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, null);
        if (value.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? (uri.Host, uri.Port) : (null, null);

        var parts = value.Split(':', 2);
        return (parts[0], parts.Length == 2 && int.TryParse(parts[1], out var port) ? port : null);
    }

    // ---- pre-filling the editor -------------------------------------------------------

    /// <summary>
    /// The connection the editor opens with. Not saved — the editor saves it, if the person
    /// says so. The URL goes in the provider's URL field, the name is the provider's own
    /// unless the container's name says which of several it is (<c>sonarr-4k</c>), and a
    /// service behind a gateway container starts out "sitting behind" whatever connection
    /// already watches that gateway, so a VPN outage is one alert rather than one per
    /// service behind it.
    /// </summary>
    public static Connection Draft(DiscoveredService found, IReadOnlyList<Connection> connections)
    {
        var settings = new SettingsBag();
        if (UrlField(found.Provider) is { } field)
            settings[field.Key] = found.Address.Url;

        var labels = found.Container.Labels;
        var name = labels.TryGetValue(KnownServices.NameLabel, out var labelledName) && labelledName.Trim().Length > 0
            ? labelledName.Trim()
            : DefaultName(found.Provider, found.Container);
        var icon = labels.TryGetValue(KnownServices.IconLabel, out var labelledIcon) && labelledIcon.Trim().Length > 0
            ? labelledIcon.Trim()
            : found.Provider.Icon;

        string? dependsOn = null;
        if (!ReferenceEquals(found.Owner, found.Container))
            dependsOn = ContainerSafety.ConnectionsReaching(found.Owner, connections).FirstOrDefault()?.Id;

        return new Connection
        {
            Provider = found.Provider.Type,
            Name = name,
            Icon = icon,
            Settings = settings,
            DependsOn = dependsOn,
        };
    }

    /// <summary>
    /// "Sonarr" for a container called sonarr; "Sonarr (sonarr-4k)" for the second one, so two
    /// suggestions of the same kind do not become two connections nobody can tell apart.
    /// </summary>
    public static string DefaultName(IConnectionProvider provider, ContainerRow container)
    {
        static string Squash(string text) => new([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

        var name = container.Service ?? container.Name;
        var plain = Squash(name);
        return plain == Squash(provider.Type) || plain == Squash(provider.DisplayName)
            ? provider.DisplayName
            : $"{provider.DisplayName} ({container.Name})";
    }

    /// <summary>The field the address goes in: one called "url", or the first URL field.</summary>
    public static FieldSpec? UrlField(IConnectionProvider provider) =>
        provider.Fields.FirstOrDefault(f => f.Key.Equals("url", StringComparison.OrdinalIgnoreCase) && f.Kind == FieldKind.Url)
        ?? provider.Fields.FirstOrDefault(f => f.Kind == FieldKind.Url);

    /// <summary>Where to find the credential, from the provider's first secret field — or null when it has none.</summary>
    public static string? KeyHint(IConnectionProvider provider)
    {
        if (provider.Fields.FirstOrDefault(f => f.IsSecret) is not { } secret)
            return null;
        var optional = secret.Required ? "" : " (optional)";
        return secret.Help is { Length: > 0 } help
            ? $"{secret.Label}{optional}: {help}"
            : $"{secret.Label}{optional}: from the app's own settings.";
    }

    /// <summary>What the editor says at the top when it was opened from a suggestion.</summary>
    public static string Intro(DiscoveredService found)
    {
        var text = $"Found in the container “{found.Container.Name}” on {found.Docker.Name}, recognised by {found.MatchedBy}. " +
                   found.Address.Why;
        if (found.Service.Note is { Length: > 0 } note)
            text += " " + note;
        return text;
    }
}
