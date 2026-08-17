using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.DockerLabelsPlugin;

/// <summary>
/// A dashboard that populates itself from the containers already running.
///
/// LabbyTwo's pitch is that nothing is hardcoded, and this takes that one step further: the
/// service you started ten minutes ago describes itself in its own Compose file, and the
/// dashboard picks it up. Nobody retypes a name, a URL and an icon that are already written
/// down somewhere.
///
/// <code>
/// services:
///   jellyfin:
///     image: jellyfin/jellyfin
///     labels:
///       labbytwo.enable: "true"
///       labbytwo.name: "Jellyfin"
///       labbytwo.url: "http://192.168.86.57:8096"
///       labbytwo.icon: "🎬"
///       labbytwo.tab: "Media"
/// </code>
///
/// It reads the Docker API's own JSON rather than talking to Docker itself, because an
/// importer is a pure function of a file — which is what makes it unit-testable without a
/// running app, and what lets it read a file from a machine LabbyTwo cannot reach.
/// <see cref="DockerLabelsEndpoints"/> is the other half: it fetches that JSON from the
/// local socket so there is a file to give this in the first place.
/// </summary>
public sealed class DockerLabelsImporter : IDashboardImporter
{
    /// <summary>
    /// The label namespace. A prefix rather than a fixed set, so a container can carry
    /// labels for Homepage, Traefik and this at once without any of them arguing.
    /// </summary>
    public const string Prefix = "labbytwo.";

    public string Key => "docker-labels";
    public string DisplayName => "Docker labels";
    public string Icon => "🐳";

    public string Description =>
        "Container JSON from the Docker API — containers labelled labbytwo.enable=true become connections and tiles.";

    public IReadOnlyList<string> Extensions => [".json"];

    /// <summary>
    /// Cheap, and must not throw: a detector that blows up on a file it does not understand
    /// stops the other importers being asked about it.
    /// </summary>
    public bool CanHandle(ImportSource source)
    {
        if (source.Extension != ".json")
            return false;

        try
        {
            using var document = JsonDocument.Parse(source.Text);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            // Docker's own shape and nobody else's: an array of objects each carrying Id
            // and Image. Checking only the first is the point of a cheap detector.
            foreach (var entry in document.RootElement.EnumerateArray())
                return entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("Id", out _)
                    && (entry.TryGetProperty("Image", out _) || entry.TryGetProperty("Config", out _));

            // An empty array really is valid Docker output — a host with nothing running.
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public ImportPlan Read(ImportSource source)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(source.Text);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"That is not readable JSON: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new FormatException(
                    "Expected the array that `docker ps --format json` or /containers/json returns, "
                    + "not a single object.");

            var plan = new ImportPlan();
            var tabs = new Dictionary<string, ImportedTab>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var considered = 0;

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                considered++;

                var labels = Labels(entry);
                var container = ContainerName(entry);

                if (!labels.TryGetValue($"{Prefix}enable", out var enable)
                    || !enable.Equals("true", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = labels.GetValueOrDefault($"{Prefix}name", container);
                var url = labels.GetValueOrDefault($"{Prefix}url", "").Trim();

                if (url.Length == 0)
                {
                    // Named rather than silently skipped, and with the published port in the
                    // message: somebody who labelled a container and got nothing needs to
                    // know which label is missing and what to put in it.
                    plan.Notes.Add(PublishedPort(entry) is { } port
                        ? $"“{container}” has no {Prefix}url label. It publishes port {port}, so that label "
                          + $"probably wants to be http://your-host:{port}."
                        : $"“{container}” has no {Prefix}url label, so there was nothing to point a tile at.");
                    continue;
                }

                // Two containers labelled with the same name would produce two connections
                // called the same thing, which is a picker nobody can use.
                if (!seen.Add(name))
                {
                    plan.Notes.Add($"More than one container is labelled “{name}”; only the first was imported.");
                    continue;
                }

                var reference = $"docker/{name}";
                plan.Connections.Add(new ImportedConnection(
                    reference,
                    // A container running something LabbyTwo has a real integration for can
                    // say so, and get the proper provider instead of a plain URL check.
                    labels.GetValueOrDefault($"{Prefix}provider", "http"),
                    name,
                    labels.GetValueOrDefault($"{Prefix}icon", ""),
                    new SettingsBag { ["url"] = url }));

                var tabName = labels.GetValueOrDefault($"{Prefix}tab", "Docker");
                if (!tabs.TryGetValue(tabName, out var tab))
                    tabs[tabName] = tab = new ImportedTab(tabName, tabName == "Docker" ? "🐳" : "");

                tab.Widgets.Add(new ImportedWidget("service-tile", name, 3, ConnectionRef: reference));
            }

            plan.Tabs.AddRange(tabs.Values);

            if (plan.Connections.Count == 0)
                plan.Notes.Add(considered == 0
                    ? "That file lists no containers at all."
                    : $"None of the {considered} container(s) in that file carry {Prefix}enable=true, "
                      + "which is the label that opts one in.");

            return plan;
        }
    }

    /// <summary>
    /// Labels live in two different places depending on which command produced the file.
    /// <c>/containers/json</c> and <c>docker ps</c> put them at the top level; <c>docker
    /// inspect</c> nests them under <c>Config</c>. Both are things a person will reasonably
    /// paste in, so both are read.
    /// </summary>
    private static Dictionary<string, string> Labels(JsonElement entry)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        JsonElement node;
        if (entry.TryGetProperty("Labels", out node) && node.ValueKind == JsonValueKind.Object)
        {
            // as found
        }
        else if (entry.TryGetProperty("Config", out var config)
                 && config.ValueKind == JsonValueKind.Object
                 && config.TryGetProperty("Labels", out node)
                 && node.ValueKind == JsonValueKind.Object)
        {
            // as found, one level down
        }
        else
        {
            return labels;
        }

        foreach (var label in node.EnumerateObject())
            if (label.Value.ValueKind == JsonValueKind.String)
                labels[label.Name] = label.Value.GetString() ?? "";

        return labels;
    }

    /// <summary>
    /// The container's name. <c>Names</c> is an array with a leading slash on each — an
    /// artefact of the API — and <c>docker inspect</c> uses a singular <c>Name</c> instead.
    /// </summary>
    private static string ContainerName(JsonElement entry)
    {
        if (entry.TryGetProperty("Names", out var names)
            && names.ValueKind == JsonValueKind.Array
            && names.GetArrayLength() > 0)
            return names[0].GetString()?.TrimStart('/') ?? "container";

        if (entry.TryGetProperty("Name", out var single) && single.ValueKind == JsonValueKind.String)
            return single.GetString()?.TrimStart('/') ?? "container";

        return entry.TryGetProperty("Id", out var id) && id.GetString() is { Length: >= 12 } full
            ? full[..12]
            : "container";
    }

    /// <summary>The first published port, used only to make a "you forgot the url label" note useful.</summary>
    private static int? PublishedPort(JsonElement entry)
    {
        if (!entry.TryGetProperty("Ports", out var ports) || ports.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var port in ports.EnumerateArray())
            if (port.TryGetProperty("PublicPort", out var published)
                && published.TryGetInt32(out var number))
                return number;

        return null;
    }
}
