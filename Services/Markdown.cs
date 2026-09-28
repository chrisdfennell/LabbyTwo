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
        .DisableHtml()
        .Build();

    // The same syntax with exact source positions, used only to find the code in a note so
    // a shortcode written inside backticks stays an example rather than becoming live.
    private readonly MarkdownPipeline _positions = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
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

        var found = Shortcodes.Find(markdown);
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
/// Rendered Markdown with placeholders where shortcodes were. <see cref="Html"/> is the
/// renderer's own output and safe as it stands; <see cref="Shortcodes"/>[n] is what the
/// nth placeholder stands for.
/// </summary>
public sealed record LiveDocument(string Html, IReadOnlyList<Shortcode> Shortcodes)
{
    public static LiveDocument Empty { get; } = new("", []);
}
