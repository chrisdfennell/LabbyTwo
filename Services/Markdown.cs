using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LabbyTwo.Core;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LabbyTwo.Services;

/// <summary>
/// Renders user-written markdown. Note content is authored by whoever can already
/// reconfigure the whole app, so this is a formatting convenience rather than a trust
/// boundary — but raw HTML stays disabled so a pasted snippet cannot quietly run script
/// in every other viewer's session.
/// </summary>
public sealed partial class Markdown
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseCallouts()
        .DisableHtml()
        .Build();

    // The same syntax with exact source positions, used only to find the code in a note so
    // a shortcode written inside backticks stays an example rather than becoming live.
    private readonly MarkdownPipeline _positions = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseCallouts()
        .DisableHtml()
        .UsePreciseSourceLocation()
        .Build();

    /// <summary>
    /// Random per process, and letters only. Letters so Markdown sees one plain word and
    /// leaves it alone — no emphasis, no link, no escaping. Random so text somebody typed
    /// can never be mistaken for a placeholder; and even if it were, all it could stand
    /// for is one of the note's own shortcodes, never markup.
    /// </summary>
    private static readonly string Nonce = MakeNonce();

    private static readonly Regex PlaceholderPattern = new($"lt{Nonce}q([0-9]+)q", RegexOptions.CultureInvariant);

    public string ToHtml(string? markdown) =>
        string.IsNullOrWhiteSpace(markdown) ? "" : Markdig.Markdown.ToHtml(markdown, _pipeline);

    /// <summary>
    /// Markdown with live values in it, rendered by the same pipeline as <see cref="ToHtml"/>
    /// with each shortcode swapped for a placeholder word first. What comes back is still
    /// the renderer's own sanitised HTML — nothing a shortcode says is ever written into
    /// it — plus the list the placeholders index into, so the component drawing it can put
    /// a live component where each one stands.
    /// </summary>
    public LiveDocument Prepare(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return LiveDocument.Empty;

        var found = WithNoteLinks(Shortcodes.Find(markdown), markdown);
        if (found.Count == 0)
            return new LiveDocument(ToHtml(markdown), []);

        var code = CodeRanges(markdown);
        var codes = new List<Shortcode>();
        var text = new StringBuilder(markdown.Length);
        var at = 0;
        foreach (var shortcode in found)
        {
            if (code.Any(r => shortcode.Index < r.End && r.Start < shortcode.Index + shortcode.Length))
                continue;
            text.Append(markdown, at, shortcode.Index - at);
            text.Append(Placeholder(codes.Count));
            codes.Add(shortcode.Code);
            at = shortcode.Index + shortcode.Length;
        }
        text.Append(markdown, at, markdown.Length - at);

        return new LiveDocument(Markdig.Markdown.ToHtml(text.ToString(), _pipeline), codes);
    }

    /// <summary>
    /// Markdown that may have <c>{{if …}}</c> sections in it, cut into its sections first
    /// (see <see cref="Runbook"/>) and each piece rendered on its own by <see cref="Prepare"/>.
    /// A note with no sections comes back as one piece that is exactly what
    /// <see cref="Prepare"/> would have made of it, so nothing changes for a note that has
    /// never heard of them.
    /// </summary>
    public LivePage PreparePage(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return LivePage.Empty;
        if (!Runbook.MayHaveSections(markdown))
            return new LivePage([Section(Prepare(markdown), 0)]);

        var parts = Runbook.Parse(markdown, CodeRanges(markdown));
        var ordinal = 0;
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        return new LivePage(Convert(parts));

        IReadOnlyList<LivePart> Convert(IReadOnlyList<RunbookPart> pieces)
        {
            var converted = new List<LivePart>();
            foreach (var piece in pieces)
            {
                switch (piece)
                {
                    case RunbookText text:
                        var document = Prepare(text.Markdown);
                        // Each piece is its own document to the renderer, so two pieces with
                        // a "Steps" heading would both call it #steps. Later ones are
                        // numbered on, as the renderer does within one document, so every
                        // heading can still be linked to.
                        document = document with { Html = HeadingId().Replace(document.Html, m => UniqueId(m, usedIds)) };
                        converted.Add(Section(document, ordinal++));
                        break;
                    case RunbookProblem problem:
                        converted.Add(new LiveProblem(problem.Message, ordinal++));
                        break;
                    case RunbookIf section:
                        var at = ordinal++;
                        converted.Add(new LiveIf(section, Convert(section.Then), Convert(section.Else), at));
                        break;
                    case RunbookDetails fold:
                        var place = ordinal++;
                        converted.Add(new LiveDetails(fold, Convert(fold.Body), place));
                        break;
                }
            }
            return converted;
        }
    }

    /// <summary>
    /// The shortcodes, with every <c>[[note link]]</c> added among them in order (see
    /// <see cref="NoteLinks"/>). A link travels as a shortcode of a kind nobody can type, so
    /// it gets everything a shortcode does for free — a placeholder word the renderer leaves
    /// alone, left as text inside code, its words never becoming HTML. A note without
    /// <c>[[</c> in it costs one search for two characters.
    /// </summary>
    private static IReadOnlyList<Shortcodes.Found> WithNoteLinks(IReadOnlyList<Shortcodes.Found> found, string markdown)
    {
        // Find leaves out any link that overlaps a shortcode: the shortcode wins, and stays live.
        var links = NoteLinks.Find(markdown);
        if (links.Count == 0)
            return found;
        return [.. found.Concat(links.Select(l => new Shortcodes.Found(l.Index, l.Length, l.ToShortcode()))).OrderBy(f => f.Index)];
    }

    private static LiveSection Section(LiveDocument document, int ordinal) =>
        new(document, document.Shortcodes.Count > 0 ? MarkupTree.Parse(document.Html) : [], ordinal);

    /// <summary>
    /// The renderer's own heading id attribute. Its value is the renderer's slug — letters,
    /// digits, dashes — so appending "-2" to it cannot make it anything but another id.
    /// </summary>
    [GeneratedRegex("""(<h[1-6] id=")([^"<>]*)(")""")]
    private static partial Regex HeadingId();

    private static string UniqueId(Match match, HashSet<string> used)
    {
        var id = match.Groups[2].Value;
        var unique = id;
        for (var n = 1; !used.Add(unique); n++)
            unique = $"{id}-{n}";
        return match.Groups[1].Value + unique + match.Groups[3].Value;
    }

    /// <summary>The word standing in for the nth shortcode.</summary>
    public static string Placeholder(int index) => $"lt{Nonce}q{index}q";

    /// <summary>Finds placeholder words in rendered HTML.</summary>
    public static Regex Placeholders => PlaceholderPattern;

    /// <summary>Where the code spans and code blocks are, as [start, end) offsets into the source.</summary>
    private List<(int Start, int End)> CodeRanges(string markdown)
    {
        var ranges = new List<(int, int)>();
        var document = Markdig.Markdown.Parse(markdown, _positions);
        foreach (var node in document.Descendants())
        {
            if (node is CodeBlock or CodeInline && !node.Span.IsEmpty)
                ranges.Add((node.Span.Start, node.Span.End + 1));
        }
        return ranges;
    }

    private static string MakeNonce()
    {
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        var letters = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
            letters[i] = (char)('a' + bytes[i] % 26);
        return new string(letters);
    }
}

/// <summary>
/// A whole Markdown page cut at its <c>{{if …}}</c> sections: rendered pieces, sections
/// holding more pieces, and notes about mistakes. Each part carries an ordinal, unique in
/// the page and the same every time the same text is prepared, which the component drawing
/// it uses to keep each part's components when a section appears or disappears around it.
/// </summary>
public sealed record LivePage(IReadOnlyList<LivePart> Parts)
{
    public static LivePage Empty { get; } = new([]);

    /// <summary>Whether any part is a section, which is what the page has to watch the lab for.</summary>
    public bool HasSections => Sections().Any();

    /// <summary>Every section, nested ones included.</summary>
    public IEnumerable<LiveIf> Sections() => Walk(Parts);

    private static IEnumerable<LiveIf> Walk(IEnumerable<LivePart> parts)
    {
        foreach (var part in parts)
        {
            // A fold is not a condition, but a condition can sit inside one, and a closed
            // fold still has to know which branch to draw the moment it is opened.
            var inside = part switch
            {
                LiveIf section => section.Then.Concat(section.Else),
                LiveDetails fold => fold.Body,
                _ => null,
            };
            if (part is LiveIf found)
                yield return found;
            if (inside is null)
                continue;
            foreach (var inner in Walk(inside))
                yield return inner;
        }
    }
}

public abstract record LivePart(int Ordinal);

/// <summary>A piece of Markdown, rendered, and read back into elements when it has shortcodes in it.</summary>
public sealed record LiveSection(LiveDocument Document, IReadOnlyList<MarkupNode> Nodes, int Ordinal) : LivePart(Ordinal);

/// <summary>A mistake in how the sections were written, drawn as a "?" note where it was.</summary>
public sealed record LiveProblem(string Message, int Ordinal) : LivePart(Ordinal);

/// <summary>A section, with its two branches already prepared; only one is ever drawn.</summary>
public sealed record LiveIf(RunbookIf Section, IReadOnlyList<LivePart> Then, IReadOnlyList<LivePart> Else, int Ordinal) : LivePart(Ordinal)
{
    /// <summary>Records compare by value; two sections with the same text are still two sections.</summary>
    public bool Equals(LiveIf? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => Ordinal;
}

/// <summary>A fold, with its contents already prepared; drawn as a native details element.</summary>
public sealed record LiveDetails(RunbookDetails Fold, IReadOnlyList<LivePart> Body, int Ordinal) : LivePart(Ordinal)
{
    /// <summary>By reference, like <see cref="LiveIf"/>: two folds with the same title are still two folds.</summary>
    public bool Equals(LiveDetails? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => Ordinal;
}

/// <summary>
/// Rendered Markdown with placeholders where shortcodes were. <see cref="Html"/> is the
/// renderer's own output and safe as it stands; <see cref="Shortcodes"/>[n] is what the
/// nth placeholder stands for.
/// </summary>
public sealed record LiveDocument(string Html, IReadOnlyList<Shortcode> Shortcodes)
{
    public static LiveDocument Empty { get; } = new("", []);
}
