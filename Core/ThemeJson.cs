using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabbyTwo.Core;

/// <summary>
/// A theme as a file: what Export writes and Import reads back.
///
/// <para>The schema, version 1:</para>
/// <code>
/// {
///   "labbytwo-theme": 1,
///   "name": "Midnight Oil",          // required, 1–60 characters of text
///   "author": "Somebody",            // optional, up to 60
///   "description": "…",              // optional, up to 200
///   "dark":  { "ink": "#0a0e13", … },// at least one of dark / light
///   "light": { "ink": "#f2f5f9", … }
/// }
/// </code>
/// <para>
/// Token names are the ones in <see cref="ThemeTokens"/>, without dashes. Each variant must
/// give every required token; optional ones may be left out. Colours are hex (#rgb, #rgba,
/// #rrggbb, #rrggbbaa), rgb()/rgba() or hsl()/hsla().
/// </para>
/// <para>
/// Validation is strict and all-or-nothing. An unknown key anywhere, a token that is not in
/// the catalogue, a value that is not a colour, a missing required token or a version from
/// the future rejects the whole file with every reason listed — nothing half-imports. A
/// theme is data; a file that has something else in it is not a theme, whatever else it is.
/// </para>
/// </summary>
public static class ThemeJson
{
    public const string VersionKey = "labbytwo-theme";
    public const int CurrentVersion = 1;

    /// <summary>A theme file is a few kilobytes; anything near this is not one.</summary>
    public const int MaxBytes = 64 * 1024;

    public const int MaxName = 60;
    public const int MaxDescription = 200;

    private static readonly HashSet<string> TopLevelKeys =
        [VersionKey, "name", "author", "description", "dark", "light"];

    /// <summary>What an import produced: a theme, or every reason there is not one.</summary>
    public sealed record Result(Theme? Theme, IReadOnlyList<string> Errors)
    {
        public bool Ok => Theme is not null;

        public static Result Fail(params string[] errors) => new(null, errors);
    }

    /// <summary>
    /// Whatever was pasted or uploaded: a LabbyTwo theme file if it says it is one, otherwise
    /// a base16 scheme if it looks like one. The two readers never see each other's input, so
    /// neither has to be lenient to cope with the other.
    /// </summary>
    public static Result ImportAny(string? text)
    {
        if (text is not null && !text.Contains(VersionKey, StringComparison.Ordinal) && Base16.LooksLike(text))
            return Base16.Import(text);
        return Import(text);
    }

    /// <summary>The file for <paramref name="theme"/>. Only the tokens the theme sets itself, so a round trip is exact.</summary>
    public static string Export(Theme theme)
    {
        var root = new JsonObject
        {
            [VersionKey] = CurrentVersion,
            ["name"] = theme.Name,
        };
        if (theme.Author.Length > 0)
            root["author"] = theme.Author;
        if (theme.Description.Length > 0)
            root["description"] = theme.Description;
        if (theme.Dark is { } dark)
            root["dark"] = Variant(dark);
        if (theme.Light is { } light)
            root["light"] = Variant(light);

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Variant(ThemeVariant variant)
    {
        var tokens = new JsonObject();
        foreach (var token in ThemeTokens.All)
        {
            if (variant[token.Name] is { } colour)
                tokens[token.Name] = colour.Css;
        }
        return tokens;
    }

    /// <summary>
    /// Reads a theme file. The theme comes back with an empty id; giving it one is the
    /// store's business, so an import can never overwrite a theme by naming its id.
    /// </summary>
    public static Result Import(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result.Fail("There is nothing to import.");
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
            return Result.Fail($"That is too big to be a theme (the limit is {MaxBytes / 1024} KB).");

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            return Result.Fail("That is not valid JSON.");
        }
        catch (ArgumentException)
        {
            // A key given twice. JsonObject refuses it, and so does a theme: which of the two
            // was meant is not something to guess.
            return Result.Fail("A key appears twice in that file.");
        }

        // Touching the object is what makes JsonNode find a duplicate key, so check now,
        // inside the same guard, rather than later in the middle of validation.
        try
        {
            _ = node is JsonObject probe ? probe.Count + probe.Sum(p => p.Value is JsonObject inner ? inner.Count : 0) : 0;
        }
        catch (ArgumentException)
        {
            return Result.Fail("A key appears twice in that file.");
        }

        if (node is not JsonObject root)
            return Result.Fail("A theme file is a JSON object.");

        var errors = new List<string>();

        foreach (var key in root.Select(p => p.Key))
        {
            if (!TopLevelKeys.Contains(key))
                errors.Add($"\"{Shorten(key)}\" is not part of a theme file.");
        }

        if (root[VersionKey] is not JsonValue versionValue || !versionValue.TryGetValue<int>(out var version))
            errors.Add($"\"{VersionKey}\" is missing — this does not look like a LabbyTwo theme file.");
        else if (version > CurrentVersion)
            errors.Add($"This theme file is version {version}; this LabbyTwo reads up to version {CurrentVersion}.");
        else if (version < 1)
            errors.Add($"\"{VersionKey}\" must be {CurrentVersion}.");

        var name = Text(root, "name", MaxName, errors, required: true);
        var author = Text(root, "author", MaxName, errors, required: false);
        var description = Text(root, "description", MaxDescription, errors, required: false);

        var dark = ReadVariant(root, "dark", errors);
        var light = ReadVariant(root, "light", errors);
        if (!root.ContainsKey("dark") && !root.ContainsKey("light"))
            errors.Add("A theme needs a \"dark\" or a \"light\" set of colours, or both.");

        if (errors.Count > 0)
            return new Result(null, errors);

        return new Result(new Theme("", name, author, dark, light, description), []);
    }

    private static string Text(JsonObject root, string key, int max, List<string> errors, bool required)
    {
        if (!root.TryGetPropertyValue(key, out var node) || node is null)
        {
            if (required)
                errors.Add($"\"{key}\" is missing.");
            return "";
        }

        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            errors.Add($"\"{key}\" must be text.");
            return "";
        }

        var clean = CleanText(text);
        if (required && clean.Length == 0)
            errors.Add($"\"{key}\" is empty.");
        else if (clean.Length > max)
            errors.Add($"\"{key}\" is longer than {max} characters.");
        return clean;
    }

    /// <summary>
    /// Names are shown as text, through Razor's encoding, so they need no escaping here — but
    /// control characters and line breaks have no business in a one-line label.
    /// </summary>
    public static string CleanText(string text) =>
        new string([.. text.Where(c => !char.IsControl(c))]).Trim();

    private static ThemeVariant? ReadVariant(JsonObject root, string key, List<string> errors)
    {
        if (!root.TryGetPropertyValue(key, out var node))
            return null;

        if (node is not JsonObject tokens)
        {
            errors.Add($"\"{key}\" must be an object of token names and colours.");
            return null;
        }

        var colours = new Dictionary<string, ThemeColor>(StringComparer.Ordinal);
        var before = errors.Count;
        foreach (var (name, value) in tokens)
        {
            if (!ThemeTokens.IsKnown(name))
            {
                errors.Add($"{key}: \"{Shorten(name)}\" is not a theme token.");
                continue;
            }

            if (value is not JsonValue v || !v.TryGetValue<string>(out var text) || !ThemeColor.TryParse(text, out var colour))
            {
                errors.Add($"{key}: \"{name}\" is not a colour — use #rrggbb, rgb() or hsl().");
                continue;
            }

            colours[name] = colour;
        }

        foreach (var missing in ThemeTokens.RequiredNames.Where(n => !colours.ContainsKey(n)))
            errors.Add($"{key}: \"{missing}\" is missing.");

        return errors.Count == before ? new ThemeVariant(colours) : null;
    }

    /// <summary>A key quoted back in an error is trimmed, so a hostile file cannot fill the page with its own text.</summary>
    private static string Shorten(string text)
    {
        var clean = CleanText(text);
        return clean.Length > 40 ? clean[..40] + "…" : clean;
    }
}
