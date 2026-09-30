using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LabbyTwo.Core;

/// <summary>
/// What a container was set up with — the part of <c>docker inspect</c> somebody wrote in a
/// compose file — as it is kept in the config history. Each section is a sorted map from
/// something stable (an environment variable's name, a port, a mount's destination) to what
/// it was set to, so two versions can be compared entry by entry and the history can say
/// "PUID went from 1000 to 1001" rather than "the environment changed".
///
/// Deliberately only what a person configured. The image's own defaults — the environment,
/// labels and command baked into the image — are taken out where the image could be read,
/// or every update would show up as a configuration change when nobody changed anything.
/// Things Docker makes up on each recreate are left out or evened out for the same reason:
/// an anonymous volume's random name, Compose's config-hash label, the container's id.
///
/// Secrets are never stored. A value whose name looks like a secret (see
/// <see cref="ContainerConfigs.LooksSecret"/>) is kept as a short keyed hash of the value —
/// enough to say that it changed, and nothing about what it is.
/// </summary>
public sealed record ContainerConfig
{
    /// <summary>The reference it was created from: <c>lscr.io/linuxserver/sonarr:latest</c>.</summary>
    public string Image { get; init; } = "";

    public SortedDictionary<string, string> Env { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Published ports: "8989/tcp" → "8989", or "127.0.0.1:8989" when bound to one address.</summary>
    public SortedDictionary<string, string> Ports { get; init; } = new(StringComparer.Ordinal);

    /// <summary>By where they appear inside the container: "/config" → "volume sonarr_config".</summary>
    public SortedDictionary<string, string> Mounts { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Network name → fixed address, if one was given. "host" alone for host networking.</summary>
    public SortedDictionary<string, string> Networks { get; init; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, string> Labels { get; init; } = new(StringComparer.Ordinal);

    /// <summary>"unless-stopped", "on-failure:5", "no".</summary>
    public string Restart { get; init; } = "";

    /// <summary>Entrypoint and command together, or empty when both are the image's own.</summary>
    public string Command { get; init; } = "";

    /// <summary>False when the image could not be read, so its defaults are still mixed in.</summary>
    public bool ImageDefaultsRemoved { get; init; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>A stored version read back, or null for one that cannot be read.</summary>
    public static ContainerConfig? FromJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            var config = JsonSerializer.Deserialize<ContainerConfig>(text, Json);
            if (config is null)
                return null;
            // Deserialising drops the comparer; put the ordinal one back so the order is the same as when written.
            return config with
            {
                Env = Sorted(config.Env),
                Ports = Sorted(config.Ports),
                Mounts = Sorted(config.Mounts),
                Networks = Sorted(config.Networks),
                Labels = Sorted(config.Labels),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SortedDictionary<string, string> Sorted(IDictionary<string, string>? source) =>
        source is null ? new(StringComparer.Ordinal) : new(source, StringComparer.Ordinal);

    /// <summary>Whether two versions say the same thing — the test for "record a new one".</summary>
    public bool SameAs(ContainerConfig other) => ToJson() == other.ToJson();
}

/// <summary>One difference between two versions of a container's configuration.</summary>
/// <param name="Section">"image", "env", "ports", "mounts", "networks", "labels", "restart policy" or "command".</param>
/// <param name="Key">The entry within it — the variable, the port — or empty for a section that is one value.</param>
/// <param name="Before">What it was, or null when it was added.</param>
/// <param name="After">What it is, or null when it was removed.</param>
public sealed record ConfigDifference(string Section, string Key, string? Before, string? After)
{
    /// <summary>"PUID: 1000 → 1001", "added TZ = Europe/London", "removed 8080/tcp".</summary>
    public string Describe()
    {
        var what = Key.Length > 0 ? Key : Section;
        return (Before, After) switch
        {
            (null, { } after) => after.Length > 0 ? $"added {what} = {after}" : $"added {what}",
            ({ } before, null) => before.Length > 0 ? $"removed {what} (was {before})" : $"removed {what}",
            _ => $"{what}: {Show(Before)} → {Show(After)}",
        };
    }

    private static string Show(string? value) => string.IsNullOrEmpty(value) ? "(empty)" : value;
}

/// <summary>
/// Reading a container's configuration out of <c>docker inspect</c>, keeping secrets out of
/// it, and comparing two versions. Pure, so every rule — what counts as a secret, which
/// labels are noise, what an image default is — is a test rather than a container.
/// </summary>
public static partial class ContainerConfigs
{
    /// <summary>
    /// Names that usually hold a secret. Deliberately broad: a variable hidden that did not
    /// need to be costs nothing, one stored that should not have been is a password in a
    /// database file that goes into backups and config exports.
    /// </summary>
    [GeneratedRegex("PASS|SECRET|TOKEN|KEY|CREDENTIAL|PRIVATE|AUTH|SALT|COOKIE|SESSION|SIGNATURE|CERT|DSN|CONN(ECTION)?_?STR",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretName();

    /// <summary>A URL with a password in it — <c>postgres://user:pass@db/app</c> — whatever the variable is called.</summary>
    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*://[^/\s:@]*:[^/\s@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialUrl();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex AnonymousVolume();

    /// <summary>What a hidden value is stored as, before its hash.</summary>
    public const string HiddenPrefix = "hidden #";

    /// <summary>
    /// Labels that change on every recreate or every Compose upgrade without anybody
    /// changing anything: the hash of the compose file's service (which changes exactly
    /// when something else here does, so it would only repeat it), the Compose version, the
    /// image id Compose records.
    /// </summary>
    public static readonly IReadOnlySet<string> NoisyLabels = new HashSet<string>(StringComparer.Ordinal)
    {
        "com.docker.compose.config-hash",
        "com.docker.compose.version",
        "com.docker.compose.image",
        "com.docker.compose.replace",
    };

    public static bool LooksSecret(string name, string value) =>
        SecretName().IsMatch(name) || CredentialUrl().IsMatch(value);

    /// <summary>
    /// A secret's stand-in: a keyed hash of the container, the name and the value, cut to
    /// eight hex digits. Keyed with a random per-install salt so a table of common passwords
    /// is no use against it, and short so that even with the salt a guess cannot be
    /// confirmed with any confidence — it only needs to notice that the value changed.
    /// </summary>
    public static string Hide(byte[] salt, string container, string name, string value)
    {
        if (value.Length == 0)
            return "";
        var hash = HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes($"{container}\0{name}\0{value}"));
        return HiddenPrefix + Convert.ToHexStringLower(hash)[..8];
    }

    /// <summary>
    /// The configuration in an inspect payload.
    /// </summary>
    /// <param name="inspect">The body of <c>GET /containers/{id}/json</c>.</param>
    /// <param name="image">The body of <c>GET /images/{id}/json</c> for the image it runs, or null
    /// if it could not be read — its defaults then stay in, and the version says so.</param>
    /// <param name="salt">The install's salt for hiding secrets.</param>
    public static ContainerConfig Parse(string inspect, string? image, byte[] salt)
    {
        using var document = JsonDocument.Parse(inspect);
        var root = document.RootElement;
        var name = Str(root, "Name").TrimStart('/');
        var config = Obj(root, "Config");
        var host = Obj(root, "HostConfig");

        using var imageDocument = image is null ? null : TryParse(image);
        var imageConfig = imageDocument is null ? default : Obj(imageDocument.RootElement, "Config");
        var defaults = imageDocument is not null;

        // Environment, less the image's own.
        var imageEnv = new HashSet<string>(Strings(imageConfig, "Env"), StringComparer.Ordinal);
        var env = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Strings(config, "Env"))
        {
            if (imageEnv.Contains(line))
                continue;
            var equals = line.IndexOf('=');
            var key = equals < 0 ? line : line[..equals];
            var value = equals < 0 ? "" : line[(equals + 1)..];
            env[key] = LooksSecret(key, value) ? Hide(salt, name, key, value) : value;
        }

        // Published ports, from what was asked for rather than what is bound now — a stopped
        // container has the same configuration as a running one.
        var ports = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (host.ValueKind == JsonValueKind.Object && host.TryGetProperty("PortBindings", out var bindings) &&
            bindings.ValueKind == JsonValueKind.Object)
        {
            foreach (var port in bindings.EnumerateObject())
            {
                if (port.Value.ValueKind != JsonValueKind.Array)
                    continue;
                var published = port.Value.EnumerateArray()
                    .Select(b => (Ip: Str(b, "HostIp"), Port: Str(b, "HostPort")))
                    .Select(b => b.Ip.Length > 0 && b.Ip != "0.0.0.0" && b.Ip != "::" ? $"{b.Ip}:{b.Port}" : b.Port)
                    .Where(b => b.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                ports[port.Name] = published.Count > 0 ? string.Join(", ", published) : "a random port";
            }
        }

        var mounts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("Mounts", out var mountList) && mountList.ValueKind == JsonValueKind.Array)
        {
            foreach (var mount in mountList.EnumerateArray())
            {
                var type = Str(mount, "Type");
                var volume = Str(mount, "Name");
                var what = type switch
                {
                    "volume" when AnonymousVolume().IsMatch(volume) => "volume (anonymous)",
                    "volume" => $"volume {volume}",
                    "bind" => $"bind {Str(mount, "Source")}",
                    "" => Str(mount, "Source"),
                    _ => type,
                };
                if (mount.TryGetProperty("RW", out var rw) && rw.ValueKind == JsonValueKind.False)
                    what += " (read-only)";
                mounts[Str(mount, "Destination")] = what;
            }
        }

        var networks = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var mode = host.ValueKind == JsonValueKind.Object ? Str(host, "NetworkMode") : "";
        if (mode is "host" or "none" || mode.StartsWith("container:", StringComparison.Ordinal))
        {
            // The mode is the whole story; "container:<id>" names an id that changes on every
            // recreate of the other container, so only the kind is kept.
            networks[mode.StartsWith("container:", StringComparison.Ordinal) ? "another container's network" : mode] = "";
        }
        else if (root.TryGetProperty("NetworkSettings", out var settings) &&
                 settings.TryGetProperty("Networks", out var networkMap) && networkMap.ValueKind == JsonValueKind.Object)
        {
            foreach (var network in networkMap.EnumerateObject())
            {
                var address = network.Value.TryGetProperty("IPAMConfig", out var ipam) && ipam.ValueKind == JsonValueKind.Object
                    ? Str(ipam, "IPv4Address")
                    : "";
                networks[network.Name] = address;
            }
        }

        var imageLabels = Map(imageConfig, "Labels");
        var labels = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in Map(config, "Labels"))
        {
            if (NoisyLabels.Contains(key) || (imageLabels.TryGetValue(key, out var baked) && baked == value))
                continue;
            labels[key] = LooksSecret(key, value) ? Hide(salt, name, key, value) : value;
        }

        var restart = "no";
        if (host.ValueKind == JsonValueKind.Object && host.TryGetProperty("RestartPolicy", out var policy) &&
            policy.ValueKind == JsonValueKind.Object && Str(policy, "Name") is { Length: > 0 } policyName)
        {
            restart = policyName == "on-failure" && policy.TryGetProperty("MaximumRetryCount", out var retries) &&
                      retries.TryGetInt32(out var count) && count > 0
                ? $"on-failure:{count}"
                : policyName;
        }

        // The command only where it differs from the image's: Watchtower's own rule for
        // what was configured rather than inherited.
        var entrypoint = Strings(config, "Entrypoint");
        var cmd = Strings(config, "Cmd");
        var sameEntrypoint = defaults && entrypoint.SequenceEqual(Strings(imageConfig, "Entrypoint"));
        var sameCmd = defaults && cmd.SequenceEqual(Strings(imageConfig, "Cmd"));
        var command = sameEntrypoint && sameCmd
            ? ""
            : string.Join(' ', entrypoint.Concat(cmd).Select(arg => HideArgument(salt, name, arg)).Where(a => a.Length > 0));

        return new ContainerConfig
        {
            Image = Str(config, "Image"),
            Env = env,
            Ports = ports,
            Mounts = mounts,
            Networks = networks,
            Labels = labels,
            Restart = restart,
            Command = command,
            ImageDefaultsRemoved = defaults,
        };
    }

    /// <summary><c>--api-key=abc</c> becomes <c>--api-key=hidden #…</c>; anything else is left as written.</summary>
    private static string HideArgument(byte[] salt, string container, string argument)
    {
        var equals = argument.IndexOf('=');
        if (equals <= 0)
            return argument;
        var key = argument[..equals];
        var value = argument[(equals + 1)..];
        return LooksSecret(key, value) ? $"{key}={Hide(salt, container, key, value)}" : argument;
    }

    /// <summary>Everything that differs from <paramref name="before"/> to <paramref name="after"/>, section by section.</summary>
    public static IReadOnlyList<ConfigDifference> Diff(ContainerConfig before, ContainerConfig after)
    {
        var list = new List<ConfigDifference>();
        if (before.Image != after.Image)
            list.Add(new ConfigDifference("image", "", before.Image, after.Image));
        Compare(list, "env", before.Env, after.Env);
        Compare(list, "ports", before.Ports, after.Ports);
        Compare(list, "mounts", before.Mounts, after.Mounts);
        Compare(list, "networks", before.Networks, after.Networks);
        Compare(list, "labels", before.Labels, after.Labels);
        if (before.Restart != after.Restart)
            list.Add(new ConfigDifference("restart policy", "", before.Restart, after.Restart));
        if (before.Command != after.Command)
            list.Add(new ConfigDifference("command", "", before.Command, after.Command));
        return list;
    }

    private static void Compare(List<ConfigDifference> list, string section,
        IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        foreach (var key in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var had = before.TryGetValue(key, out var was);
            var has = after.TryGetValue(key, out var now);
            if (had && has && was == now)
                continue;
            list.Add(new ConfigDifference(section, key, had ? was : null, has ? now : null));
        }
    }

    /// <summary>
    /// What changed, in the fewest words: "env FOO, BAR; ports; restart policy". Variables
    /// and labels are named, since which one is the point; ports, mounts and the rest are
    /// only named as a section, and the detail says the rest.
    /// </summary>
    public static string Summary(IReadOnlyList<ConfigDifference> differences)
    {
        var parts = new List<string>();
        foreach (var section in differences.GroupBy(d => d.Section))
        {
            if (section.Key is "env" or "labels")
            {
                var keys = section.Select(d => d.Key).ToList();
                var named = string.Join(", ", keys.Take(3));
                parts.Add($"{section.Key} {named}{(keys.Count > 3 ? $" and {keys.Count - 3} more" : "")}");
            }
            else
            {
                parts.Add(section.Key);
            }
        }
        return string.Join("; ", parts);
    }

    // ---- JSON helpers -------------------------------------------------------------

    private static JsonDocument? TryParse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement Obj(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static IReadOnlyList<string> Strings(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(v => v.GetString() ?? "")]
            : [];

    private static Dictionary<string, string> Map(JsonElement element, string property)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in value.EnumerateObject())
                map[entry.Name] = entry.Value.GetString() ?? "";
        }
        return map;
    }
}
