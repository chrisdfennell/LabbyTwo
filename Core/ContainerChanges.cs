using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LabbyTwo.Core;

/// <summary>
/// What is remembered about one container between two looks at a Docker host: enough to
/// tell a restart from a recreate from a new image, and nothing that changes on its own —
/// Docker's "Up 3 hours" is not kept, since storing it would mean writing the baseline on
/// every sweep for no reason.
/// </summary>
/// <param name="Name">Without Docker's leading slash; the key, because it is what survives a
/// recreate and what people call it.</param>
/// <param name="Id">The container id. A new one under the same name is a recreate.</param>
/// <param name="Image">The reference it was created from, for saying what it runs.</param>
/// <param name="ImageId">The image actually running. A new one is an update.</param>
/// <param name="State">Docker's word: running, exited, paused, restarting, created, dead.</param>
public sealed record ContainerSnapshot(string Name, string Id, string Image, string ImageId, string State)
{
    public bool IsRunning => State == "running";
}

/// <summary>One container change found by <see cref="ContainerChanges.Diff"/>.</summary>
/// <param name="Name">The container's name, which is the change's subject.</param>
public sealed record ContainerChange(string Action, string Name, string Title, string Detail);

/// <summary>
/// Compares two looks at a Docker host's containers and says what changed between them.
///
/// Why polling and not Docker's <c>/events</c> stream, which says exactly this as it
/// happens: the stream is a request held open for ever, and everything between LabbyTwo and
/// the socket is built to end those. A socket proxy (Tecnativa's, linuxserver's) is HAProxy
/// with a server timeout; a TCP endpoint crosses whatever else is on the network; and the
/// proxy needs <c>EVENTS=1</c>, which is one more flag to explain. Every reconnect is also a
/// gap in which events are simply gone, so a stream still needs a list to resynchronise
/// against — at which point the list is the thing that has to be right, and the stream is
/// an optimisation. The list is also already being fetched: the Docker probe asks for it
/// every sweep and shares the answer (see <c>DockerContainers.Remember</c>), so diffing it
/// costs no extra request at all, works through any proxy that lets the dashboard work, and
/// needs no reconnect logic because there is no connection to lose.
///
/// The cost is resolution. A change is seen at the next sweep rather than the moment it
/// happens, and a container that stops and starts again between two sweeps would be
/// invisible if the list were all there was. It is not quite all: Docker's status says how
/// long a container has been up, and one that has been up for less time than has passed
/// since the last look was started in between — so a restart is caught even when it was
/// over before anyone looked. A container crash-looping faster than the sweep shows as one
/// restart per sweep rather than one per crash; its state is "restarting" and the
/// suggested "something has stopped" rule is the better alarm for that.
///
/// Pure, with the clock passed in, so every case — a recreate, a new image, a restart seen
/// only through the uptime — is pinned by a test rather than by waiting for Watchtower.
/// </summary>
public static partial class ContainerChanges
{
    /// <summary>
    /// A little slack when comparing an uptime to the time between two looks: Docker words
    /// the uptime at its own moment, which is not quite the moment the list arrived here.
    /// </summary>
    public static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Everything that changed between <paramref name="before"/> and <paramref name="now"/>.
    /// </summary>
    /// <param name="before">The last look, keyed by name.</param>
    /// <param name="now">This look: each container with Docker's status sentence, which is
    /// where the uptime is.</param>
    /// <param name="sinceLastLook">How long ago the last look was, or null when it was not
    /// in this process — after a restart the baseline comes from the database, and how long
    /// ago it was taken is unknown, so uptime cannot be judged against it.</param>
    public static IReadOnlyList<ContainerChange> Diff(
        IReadOnlyDictionary<string, ContainerSnapshot> before,
        IReadOnlyList<(ContainerSnapshot Container, string Status)> now,
        TimeSpan? sinceLastLook)
    {
        var changes = new List<ContainerChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (container, status) in now.OrderBy(c => c.Container.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = container.Name;
            seen.Add(name);

            if (!before.TryGetValue(name, out var old))
            {
                changes.Add(new ContainerChange(ChangeActions.Created, name, $"{name} was created",
                    $"From {container.Image}{(container.IsRunning ? ", and started" : "")}."));
                continue;
            }

            if (old.Id != container.Id)
            {
                // Same name, new container: Compose or Watchtower recreating it. Whether the
                // image moved is the part worth saying — that is an update.
                if (old.ImageId != container.ImageId && container.ImageId.Length > 0)
                {
                    changes.Add(new ContainerChange(ChangeActions.Image, name, $"{name} is on a new image",
                        $"{Describe(container)} — was {ShortImage(old.ImageId)}, now {ShortImage(container.ImageId)}. Recreated."));
                }
                else
                {
                    changes.Add(new ContainerChange(ChangeActions.Recreated, name, $"{name} was recreated",
                        $"Same image, {Describe(container)}."));
                }
                continue;
            }

            if (StateChange(old, container, status) is { } stateChange)
            {
                changes.Add(stateChange);
                continue;
            }

            // Running at both looks, same container — but if it has been up for less time
            // than has passed since the last look, it started again in between.
            if (old.IsRunning && container.IsRunning && sinceLastLook is { } gap
                && UptimeAtMost(status) is { } uptime && uptime + Slack < gap)
            {
                changes.Add(new ContainerChange(ChangeActions.Restarted, name, $"{name} restarted",
                    $"Up for {Uptime(status)} when last checked, so it restarted since the check before."));
            }
        }

        foreach (var (name, old) in before.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Contains(name))
                changes.Add(new ContainerChange(ChangeActions.Removed, name, $"{name} was removed", $"It ran {old.Image}."));
        }

        return changes;
    }

    private static ContainerChange? StateChange(ContainerSnapshot old, ContainerSnapshot now, string status)
    {
        if (old.State == now.State)
            return null;
        var name = now.Name;

        return now.State switch
        {
            // Restarting is Docker's restart policy bringing back something that died: a
            // restart, and one nobody asked for.
            "restarting" => new ContainerChange(ChangeActions.Restarted, name, $"{name} is restarting",
                "It stopped and its restart policy is bringing it back."),
            "paused" => new ContainerChange(ChangeActions.Paused, name, $"{name} was paused", ""),
            "running" when old.State == "paused" => new ContainerChange(ChangeActions.Unpaused, name, $"{name} was unpaused", ""),
            "running" when old.State == "restarting" => new ContainerChange(ChangeActions.Started, name, $"{name} came back after restarting", ""),
            "running" => new ContainerChange(ChangeActions.Started, name, $"{name} started", ""),
            "exited" or "dead" or "created" when old.IsRunning || old.State is "restarting" or "paused" =>
                new ContainerChange(ChangeActions.Stopped, name, $"{name} stopped", ExitWords(status)),
            _ => null,
        };
    }

    private static string ExitWords(string status) =>
        ExitPattern().Match(status) is { Success: true } match
            ? match.Groups[1].Value == "0" ? "It exited cleanly (code 0)." : $"It exited with code {match.Groups[1].Value}."
            : "";

    [GeneratedRegex(@"^Exited \((-?\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ExitPattern();

    /// <summary>
    /// The longest a container can have been up, from Docker's status sentence — "Up 25
    /// seconds", "Up About a minute", "Up 3 hours (healthy)". Docker rounds its words down
    /// (minutes) or to the nearest (hours), so this is the top of the range each wording
    /// covers. Null for a status that is not "Up …", or one worded in a way this does not
    /// know — which is then never read as a restart, the safe way to be wrong.
    /// </summary>
    public static TimeSpan? UptimeAtMost(string status)
    {
        var match = UpPattern().Match(status ?? "");
        if (!match.Success)
            return null;
        var words = match.Groups[1].Value.Trim().ToLowerInvariant();

        if (words.StartsWith("less than a second", StringComparison.Ordinal))
            return TimeSpan.FromSeconds(1);
        if (words.StartsWith("about a minute", StringComparison.Ordinal))
            return TimeSpan.FromMinutes(2);
        if (words.StartsWith("about an hour", StringComparison.Ordinal))
            return TimeSpan.FromMinutes(90);

        var amount = AmountPattern().Match(words);
        if (!amount.Success)
            return null;
        var count = int.Parse(amount.Groups[1].Value, CultureInfo.InvariantCulture);
        return amount.Groups[2].Value switch
        {
            "second" => TimeSpan.FromSeconds(count + 1),
            "minute" => TimeSpan.FromMinutes(count + 1),
            "hour" => TimeSpan.FromHours(count + 0.5),
            "day" => TimeSpan.FromDays(count + 1),
            "week" => TimeSpan.FromDays((count + 1) * 7),
            "month" => TimeSpan.FromDays((count + 1) * 31),
            "year" => TimeSpan.FromDays((count + 1) * 366),
            _ => null,
        };
    }

    [GeneratedRegex(@"^Up\s+(.+?)(\s*\(.*\))?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UpPattern();

    [GeneratedRegex(@"^(\d+)\s+(second|minute|hour|day|week|month|year)s?\b", RegexOptions.CultureInvariant)]
    private static partial Regex AmountPattern();

    /// <summary>"25 seconds" from "Up 25 seconds (healthy)".</summary>
    private static string Uptime(string status) =>
        UpPattern().Match(status) is { Success: true } match ? match.Groups[1].Value.Trim().ToLowerInvariant() : status;

    private static string Describe(ContainerSnapshot container) =>
        container.Image.Length > 0 && !container.Image.StartsWith("sha256:", StringComparison.Ordinal)
            ? container.Image
            : "image " + ShortImage(container.ImageId);

    /// <summary>The first twelve hex digits of an image id, the way <c>docker images</c> shows it.</summary>
    public static string ShortImage(string imageId)
    {
        var hex = imageId.StartsWith("sha256:", StringComparison.Ordinal) ? imageId[7..] : imageId;
        return hex.Length > 12 ? hex[..12] : hex.Length > 0 ? hex : "unknown";
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A look at a host as the baseline stores it.</summary>
    public static string Serialise(IEnumerable<ContainerSnapshot> containers) =>
        JsonSerializer.Serialize(containers.OrderBy(c => c.Name, StringComparer.Ordinal).ToList(), Json);

    /// <summary>A stored baseline read back, or null for one that cannot be read — which is then treated as no baseline.</summary>
    public static Dictionary<string, ContainerSnapshot>? Deserialise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            var list = JsonSerializer.Deserialize<List<ContainerSnapshot>>(text, Json);
            return list?.Where(c => c.Name is { Length: > 0 })
                .GroupBy(c => c.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether two looks differ in anything a baseline keeps. When they do not, the baseline
    /// is not written again — the NAS's database is big and busy, and a sweep that found
    /// every container as it was is not worth a write.
    /// </summary>
    public static bool SameAs(IReadOnlyDictionary<string, ContainerSnapshot> before, IReadOnlyCollection<ContainerSnapshot> now) =>
        before.Count == now.Count && now.All(c => before.TryGetValue(c.Name, out var old) && old == c);
}
