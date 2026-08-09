using YamlDotNet.RepresentationModel;

namespace LabbyTwo.Services.Import;

/// <summary>
/// Thin helpers over YamlDotNet's document model. Every dashboard we import from is
/// hand-written YAML of a shape the importer only partly knows, so the whole approach is
/// "ask for what you want, accept that it may not be there" rather than deserialising
/// into classes that a missing key would break.
/// </summary>
internal static class Yaml
{
    public static YamlNode? Parse(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return stream.Documents.Count > 0 ? stream.Documents[0].RootNode : null;
    }

    public static YamlMappingNode? AsMap(this YamlNode? node) => node as YamlMappingNode;

    public static IEnumerable<YamlNode> AsList(this YamlNode? node) =>
        node is YamlSequenceNode sequence ? sequence.Children : [];

    /// <summary>The value of a key, or null. Case-insensitive: these files are hand-typed.</summary>
    public static YamlNode? Child(this YamlNode? node, string key)
    {
        if (node is not YamlMappingNode map)
            return null;
        foreach (var (name, value) in map.Children)
        {
            if (name is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }

    public static string Text(this YamlNode? node, string key, string fallback = "") =>
        node.Child(key) is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value)
            ? scalar.Value!.Trim()
            : fallback;

    public static string Scalar(this YamlNode? node, string fallback = "") =>
        node is YamlScalarNode scalar && scalar.Value is { Length: > 0 } value ? value.Trim() : fallback;

    /// <summary>
    /// Homepage nests everything as one-key maps — a group is <c>{ "Media": [ … ] }</c>.
    /// Enumerating those pairs is most of what reading that format is.
    /// </summary>
    public static IEnumerable<(string Key, YamlNode Value)> Pairs(this YamlNode? node)
    {
        if (node is not YamlMappingNode map)
            yield break;
        foreach (var (name, value) in map.Children)
        {
            if (name is YamlScalarNode { Value: { Length: > 0 } key })
                yield return (key, value);
        }
    }
}
