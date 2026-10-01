using System.Text;

namespace LabbyTwo.Core;

/// <summary>
/// One <c>{{kind: args}}</c> written into Markdown — a live value or an embedded card.
/// </summary>
/// <param name="Kind">Lower-cased, as written: "status", "metric", "widget"…</param>
/// <param name="Target">
/// The positional part, split at slashes: <c>NAS / disk_percent</c> is <c>["NAS", "disk_percent"]</c>.
/// </param>
/// <param name="Options">The <c>key=value</c> pairs, keys compared without regard to case.</param>
/// <param name="Source">Exactly what was written, braces and all, for messages and for putting it back.</param>
public sealed record Shortcode(
    string Kind,
    IReadOnlyList<string> Target,
    IReadOnlyDictionary<string, string> Options,
    string Source)
{
    /// <summary>Whether this draws a whole card rather than a few words inside a sentence.</summary>
    public bool IsBlock => Shortcodes.BlockKinds.Contains(Kind);

    public bool IsKnown => Shortcodes.InlineKinds.Contains(Kind) || IsBlock || IsStructure;

    /// <summary>
    /// <c>{{if …}}</c>, <c>{{else}}</c> or <c>{{end}}</c>: not something drawn, but the
    /// edges of a conditional section. On a line of their own they are taken out before the
    /// Markdown is rendered (see <see cref="Runbook"/>); anywhere else they are a mistake,
    /// and are drawn as a "?" saying so.
    /// </summary>
    public bool IsStructure => Shortcodes.StructureKinds.Contains(Kind);

    /// <summary>The nth positional part, or empty.</summary>
    public string Part(int index) => index < Target.Count ? Target[index] : "";

    public string Option(string key, string fallback = "") =>
        Options.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;

    /// <summary>Records compare lists by reference; two parses of the same text should be equal.</summary>
    public bool Equals(Shortcode? other) => other is not null && Source == other.Source;

    public override int GetHashCode() => Source.GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// Finds and reads <c>{{kind: args}}</c> shortcodes. Pure text work with no idea what a
/// connection is, so every awkward thing somebody can type — quotes, slashes inside names,
/// an escaped brace, a shortcode that never closes — is pinned by a test rather than
/// discovered in someone's runbook.
///
/// The grammar is small on purpose:
/// <list type="bullet">
/// <item><c>{{</c>, a kind made of letters and dashes, a colon, the arguments, <c>}}</c>, all
/// on one line. The colon is required, which keeps <c>{{ .Name }}</c> in a pasted Go
/// template, or a Handlebars <c>{{#if}}</c>, as the text it was. The exceptions are the
/// few words that take nothing — <c>{{down}}</c>, <c>{{alerts}}</c>, <c>{{end}}</c> and the
/// rest of <see cref="BareKinds"/> — and
/// <c>{{if …}}</c>, whose condition is read by <see cref="Runbook"/>.</item>
/// <item>The arguments are words, <c>"quoted words"</c>, slashes and <c>key=value</c>
/// pairs. Words between slashes make up one part of the target, so a connection called
/// Home Assistant needs no quotes; a name with a slash in it does.</item>
/// <item>A backslash before the braces (<c>\{{</c>) leaves them alone, which is also what
/// Markdown makes of <c>\{</c>, so the page shows the braces without the backslash.</item>
/// </list>
/// </summary>
public static class Shortcodes
{
    /// <summary>A few words inside a sentence — or a tiny picture the size of a word.</summary>
    public static readonly IReadOnlyList<string> InlineKinds =
    [
        "status", "metric", "forecast", "uptime", "since", "button",
        "sparkline", "uptimebar", "link", "today", "countdown", "ago", "power",
        "gauge",
    ];

    /// <summary>A whole card or list, on a line of its own.</summary>
    public static readonly IReadOnlyList<string> BlockKinds = ["widget", "card", "chart", "down", "alerts", "containers", "updates", "renewals", "changes", "incidents", "backups",
        "table", "diagram"];

    /// <summary>
    /// The edges of a section: <c>{{if …}}</c> shown only while something is true, and
    /// <c>{{details: …}}</c> folded away until somebody opens it. Both end at <c>{{end}}</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> StructureKinds = ["if", "else", "end", "details"];

    /// <summary>
    /// Kinds that make sense with nothing after them, and so may be written without the
    /// colon: <c>{{down}}</c>, <c>{{alerts}}</c>, <c>{{today}}</c>, <c>{{end}}</c>. Only these
    /// few words — the colon still has to be there for anything else, so a Go template's
    /// <c>{{ .Name }}</c> stays text.
    /// </summary>
    public static readonly IReadOnlyList<string> BareKinds = ["down", "else", "end", "alerts", "containers", "updates", "renewals", "changes", "incidents", "backups", "today", "power"];

    /// <summary>Where a shortcode sits in the text it was found in.</summary>
    public sealed record Found(int Index, int Length, Shortcode Code);

    /// <summary>Every shortcode in <paramref name="text"/>, in order.</summary>
    public static IReadOnlyList<Found> Find(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal))
            return [];

        var found = new List<Found>();
        var index = 0;
        while ((index = text.IndexOf("{{", index, StringComparison.Ordinal)) >= 0)
        {
            if (IsEscaped(text, index))
            {
                index += 2;
                continue;
            }

            if (TryRead(text, index, out var length) && Parse(text.Substring(index, length)) is { } code)
            {
                found.Add(new Found(index, length, code));
                index += length;
            }
            else
            {
                index += 2;
            }
        }
        return found;
    }

    /// <summary>
    /// Reads one whole shortcode, <c>{{kind: args}}</c> braces included. Null when the text
    /// is not one — no colon, an empty kind, no closing braces.
    /// </summary>
    public static Shortcode? Parse(string source)
    {
        var text = source.Trim();
        if (!text.StartsWith("{{", StringComparison.Ordinal) || !text.EndsWith("}}", StringComparison.Ordinal) || text.Length < 5)
            return null;

        var inner = text[2..^2];

        // {{if down: NAS}}: everything after the "if" is the condition, read by Runbook
        // rather than here, since its own colon and operators are not arguments. It has to
        // look like one of ours — a colon, or "any down" / "all up" — so Go's {{if .Ready}}
        // stays the text it was.
        var trimmed = inner.Trim();
        if (trimmed.Length > 3 && trimmed.StartsWith("if", StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(trimmed[2]))
        {
            var condition = trimmed[3..].Trim();
            if (condition.Contains(':') || Runbook.IsWholeLabCondition(condition))
                return new Shortcode("if", [condition], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), source);
            return null;
        }

        var colon = inner.IndexOf(':');
        if (colon < 0)
        {
            return BareKinds.Contains(trimmed.ToLowerInvariant())
                ? new Shortcode(trimmed.ToLowerInvariant(), [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), source)
                : null;
        }

        var kind = inner[..colon].Trim();
        if (kind.Length == 0 || !char.IsAsciiLetter(kind[0]) || !kind.All(c => char.IsAsciiLetter(c) || c == '-'))
            return null;

        var (target, options) = ReadArguments(inner[(colon + 1)..]);
        return new Shortcode(kind.ToLowerInvariant(), target, options, source);
    }

    /// <summary>
    /// A shortcode written out, quoting whatever needs it, so that <see cref="Parse"/> reads
    /// back exactly the parts and options it was given. What the editor's insert helper
    /// types for you, and so the one place that has to agree with the parser.
    /// </summary>
    public static string Write(string kind, IEnumerable<string> target, IEnumerable<KeyValuePair<string, string>>? options = null)
    {
        // {{down}} rather than {{down:}}, which would read back the same but looks like a
        // mistake to whoever reads the note.
        var parts = target.Where(p => p.Length > 0).ToList();
        var pairs = (options ?? []).Where(o => o.Value.Length > 0).ToList();
        if (parts.Count == 0 && pairs.Count == 0 && BareKinds.Contains(kind))
            return "{{" + kind + "}}";

        var text = new StringBuilder("{{").Append(kind).Append(':');
        var first = true;
        foreach (var part in parts)
        {
            text.Append(first ? " " : " / ").Append(QuoteTarget(part));
            first = false;
        }
        foreach (var (key, value) in pairs)
        {
            text.Append(' ').Append(key).Append("=\"").Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
        }
        return text.Append("}}").ToString();
    }

    /// <summary>
    /// Shortcodes whose kind is not one this app draws — a typo like <c>{{stauts: NAS}}</c> —
    /// for the editor to point out before the preview shows a question mark.
    /// </summary>
    public static IReadOnlyList<Shortcode> Unknown(string? text) =>
        [.. Find(text).Select(f => f.Code).Where(c => !c.IsKnown)];

    /// <summary>
    /// A value written so that <see cref="Parse"/> reads it back as one word: quoted when it
    /// has anything in it that would otherwise split it or end the shortcode early.
    /// </summary>
    public static string Quote(string value)
    {
        var plain = value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c is not ('"' or '/' or '=' or '\\' or '{' or '}'));
        if (plain)
            return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// A name that may sit in the target without quotes. Spaces are fine there — words are
    /// joined back up — but only single ones, since the joining would squash a double.
    /// </summary>
    public static string QuoteTarget(string value) =>
        value.Length > 0
        && value == value.Trim()
        && !value.Contains("  ", StringComparison.Ordinal)
        && value.All(c => c == ' ' || (!char.IsWhiteSpace(c) && c is not ('"' or '/' or '=' or '\\' or '{' or '}')))
            ? value
            : Quote(value);

    /// <summary>
    /// A backslash escapes the braces only if it is not itself escaped: <c>\\{{</c> is a
    /// literal backslash followed by a real shortcode.
    /// </summary>
    private static bool IsEscaped(string text, int index)
    {
        var slashes = 0;
        for (var i = index - 1; i >= 0 && text[i] == '\\'; i--)
            slashes++;
        return slashes % 2 == 1;
    }

    /// <summary>
    /// Finds the closing braces, skipping any inside quotes so a title can contain them.
    /// Stops at the end of the line: a shortcode left open should not swallow the page.
    /// </summary>
    private static bool TryRead(string text, int start, out int length)
    {
        length = 0;
        var quoted = false;
        for (var i = start + 2; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\n' or '\r')
                return false;
            if (quoted)
            {
                if (c == '\\' && i + 1 < text.Length && text[i + 1] is not ('\n' or '\r'))
                    i++;
                else if (c == '"')
                    quoted = false;
                continue;
            }
            if (c == '"')
            {
                quoted = true;
                continue;
            }
            if (c == '}' && i + 1 < text.Length && text[i + 1] == '}')
            {
                length = i + 2 - start;
                return true;
            }
        }
        return false;
    }

    private static (IReadOnlyList<string> Target, IReadOnlyDictionary<string, string> Options) ReadArguments(string args)
    {
        var parts = new List<string>();
        var current = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void EndPart()
        {
            if (current.Count > 0)
                parts.Add(string.Join(' ', current));
            current.Clear();
        }

        var i = 0;
        while (i < args.Length)
        {
            var c = args[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == '/')
            {
                EndPart();
                i++;
                continue;
            }
            if (c == '"')
            {
                current.Add(ReadQuoted(args, ref i));
                continue;
            }

            // key=value, where the key looks like a setting name. Anything else with an
            // equals sign in it is just a word.
            var keyEnd = i;
            while (keyEnd < args.Length && (char.IsAsciiLetterOrDigit(args[keyEnd]) || args[keyEnd] is '_' or '-'))
                keyEnd++;
            if (keyEnd > i && keyEnd < args.Length && args[keyEnd] == '=' && char.IsAsciiLetter(args[i]))
            {
                var key = args[i..keyEnd];
                i = keyEnd + 1;
                string value;
                if (i < args.Length && args[i] == '"')
                {
                    value = ReadQuoted(args, ref i);
                }
                else
                {
                    // A bare value runs to the next space, slashes included, so a URL or
                    // a path needs no quotes.
                    var start = i;
                    while (i < args.Length && !char.IsWhiteSpace(args[i]))
                        i++;
                    value = args[start..i];
                }
                options[key] = value;
                continue;
            }

            var wordStart = i;
            while (i < args.Length && !char.IsWhiteSpace(args[i]) && args[i] is not ('/' or '"'))
                i++;
            current.Add(args[wordStart..i]);
        }
        EndPart();
        return (parts, options);
    }

    /// <summary>Reads a quoted string starting at the opening quote; <c>\"</c> and <c>\\</c> are escapes.</summary>
    private static string ReadQuoted(string args, ref int i)
    {
        var value = new StringBuilder();
        i++;
        while (i < args.Length)
        {
            var c = args[i];
            if (c == '\\' && i + 1 < args.Length)
            {
                value.Append(args[i + 1]);
                i += 2;
                continue;
            }
            i++;
            if (c == '"')
                break;
            value.Append(c);
        }
        return value.ToString();
    }
}
