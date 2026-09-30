using System.Text.Json;

namespace LabbyTwo.Core;

/// <summary>
/// A one-shot Watchtower LabbyTwo started to update containers (see <c>SelfUpdater</c>),
/// as Docker lists it. It is meant to do its job and remove itself within minutes; one
/// that is still there hours later is stuck — and was, on a NAS, for fifteen hours after
/// an "Update all", holding Docker busy while LabbyTwo reported half the lab down.
/// </summary>
/// <param name="Targets">How many containers it was told to update; zero when that is not known.</param>
/// <param name="Labelled">True when it carries LabbyTwo's label. Helpers started by older
/// versions have none and are recognised by their image and <c>--run-once</c>.</param>
public sealed record UpdateHelper(
    string Id,
    string Name,
    DateTimeOffset Created,
    string State,
    int Targets,
    bool Labelled)
{
    /// <summary>How long it may reasonably run: see <see cref="UpdateHelperRules.Limit"/>.</summary>
    public TimeSpan Limit => UpdateHelperRules.Limit(Targets);

    public TimeSpan Age(DateTimeOffset now) => now > Created ? now - Created : TimeSpan.Zero;

    public bool IsRunning => State is "running" or "restarting";

    /// <summary>Still running well past the time any real update of that many containers takes.</summary>
    public bool IsStuck(DateTimeOffset now) => IsRunning && Age(now) > Limit;
}

/// <summary>
/// Finding LabbyTwo's update helpers in a Docker container list, and deciding when one has
/// run too long. Pure, so the limit and the recognising are tests rather than a night
/// spent waiting for a helper to hang.
///
/// Nothing here stops a helper. A stuck one is reported with its last log lines and a
/// button; killing it silently could leave a container half-recreated, and somebody
/// should see that happen rather than find out later.
/// </summary>
public static class UpdateHelperRules
{
    /// <summary>The label every helper LabbyTwo starts carries.</summary>
    public const string Label = "labbytwo.update-helper";

    /// <summary>How many containers it was told to update, for its time limit.</summary>
    public const string TargetsLabel = "labbytwo.update-helper.targets";

    /// <summary>What its name starts with, so it is recognisable in <c>docker ps</c> too.</summary>
    public const string NamePrefix = "labbytwo-update-helper-";

    /// <summary>The allowance for pulling Watchtower and starting, whatever it updates.</summary>
    public static readonly TimeSpan BaseLimit = TimeSpan.FromMinutes(30);

    /// <summary>The allowance per container: a pull and a recreate, generously.</summary>
    public static readonly TimeSpan PerContainer = TimeSpan.FromMinutes(1);

    /// <summary>Thirty minutes, and a minute more for each container it was told to update.</summary>
    public static TimeSpan Limit(int targets) => BaseLimit + PerContainer * Math.Max(1, targets);

    /// <summary>A name for a new helper, unique to the second.</summary>
    public static string NameFor(DateTimeOffset now) => $"{NamePrefix}{now.ToUniversalTime():yyyyMMdd-HHmmss}";

    /// <summary>
    /// The update helpers in a <c>/containers/json?all=1</c> answer: LabbyTwo's by label,
    /// and older ones — started before they were labelled — by being a Watchtower told to
    /// run once. A Watchtower somebody runs on a schedule never has <c>--run-once</c>, so
    /// it is not mistaken for one.
    /// </summary>
    public static IReadOnlyList<UpdateHelper> Find(string listPayload)
    {
        using var document = JsonDocument.Parse(listPayload);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var helpers = new List<UpdateHelper>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var labels = entry.TryGetProperty("Labels", out var l) && l.ValueKind == JsonValueKind.Object
                ? l.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "")
                : [];
            var image = Text(entry, "Image");
            var command = Text(entry, "Command");
            var labelled = labels.ContainsKey(Label);
            var oneShot = image.Contains("containrrr/watchtower", StringComparison.OrdinalIgnoreCase) &&
                          command.Contains("--run-once", StringComparison.Ordinal);
            if (!labelled && !oneShot)
                continue;

            var name = entry.TryGetProperty("Names", out var names) && names.ValueKind == JsonValueKind.Array &&
                       names.GetArrayLength() > 0 ? (names[0].GetString() ?? "").TrimStart('/') : "";
            var created = entry.TryGetProperty("Created", out var c) && c.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : DateTimeOffset.MinValue;
            var targets = labels.TryGetValue(TargetsLabel, out var counted) && int.TryParse(counted, out var n)
                ? n
                : Targets(command);

            helpers.Add(new UpdateHelper(Text(entry, "Id"), name, created, Text(entry, "State"), targets, labelled));
        }
        return helpers;
    }

    /// <summary>The container names on a helper's command line: every word after the program that is not a flag.</summary>
    private static int Targets(string command) =>
        command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Count(word => !word.StartsWith('-'));

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
