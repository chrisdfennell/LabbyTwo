using System.Text.Json;

namespace LabbyTwo.Core;

/// <summary>
/// The path language every JSON-reading feature shares — the JSON API provider, MQTT
/// payloads, multi-step check assertions. One implementation so <c>disks[0].used</c> means
/// the same thing wherever somebody types it.
/// </summary>
public static class JsonPath
{
    /// <summary>
    /// Walks a dotted path with optional [n] indexers. Deliberately tiny — this is not
    /// JSONPath, it is the 95% of shapes a home lab API actually returns.
    /// </summary>
    public static JsonElement? Resolve(JsonElement root, string path)
    {
        var current = root;
        foreach (var rawSegment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment;

            // A segment may be "items[2]" or bare "items" or just "[2]".
            while (true)
            {
                var bracket = segment.IndexOf('[');
                var name = bracket < 0 ? segment : segment[..bracket];

                if (name.Length > 0)
                {
                    if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var child))
                        return null;
                    current = child;
                }

                if (bracket < 0)
                    break;

                var close = segment.IndexOf(']', bracket);
                if (close < 0 || !int.TryParse(segment[(bracket + 1)..close], out var index))
                    return null;
                if (current.ValueKind != JsonValueKind.Array || index < 0 || index >= current.GetArrayLength())
                    return null;
                current = current[index];

                segment = segment[(close + 1)..];
                if (segment.Length == 0)
                    break;
            }
        }
        return current;
    }
}
