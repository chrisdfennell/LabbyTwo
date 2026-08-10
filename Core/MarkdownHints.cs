using System.Text.RegularExpressions;

namespace LabbyTwo.Core;

/// <summary>
/// Mistakes in a Markdown source that render as something the author plainly did not mean.
///
/// Only one so far, and it is the one everybody hits: CommonMark requires a space between
/// the hashes and the text, so <c>##Wi-Fi</c> is a paragraph rather than a heading. The
/// renderer is correct and the preview is honest, which is exactly why it is baffling —
/// you typed a heading and got your own text back.
///
/// These are suggestions, never applied on their own. Rewriting what someone typed is a
/// worse failure than not noticing it: <c>#1 priority</c> and <c>#!/bin/sh</c> are not
/// headings either, and turning them into headings silently would be a bug we shipped on
/// purpose.
/// </summary>
public static partial class MarkdownHints
{
    /// <param name="Line">1-based, so it matches what an editor would call the line.</param>
    /// <param name="Text">The line as written.</param>
    /// <param name="Suggestion">The same line with the space inserted.</param>
    public sealed record Hint(int Line, string Text, string Suggestion);

    // Up to three spaces of indent still makes a heading; four makes an indented code
    // block, which is why the bound matters. A letter has to follow the hashes: it rules
    // out "#1 priority", "#!/bin/sh" and "#-- divider", none of which are headings.
    [GeneratedRegex(@"^( {0,3})(#{1,6})(\p{L}.*)$")]
    private static partial Regex TightHeading();

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})")]
    private static partial Regex Fence();

    // A letter follows the hashes in these too, but they are code, and "# include <stdio.h>"
    // would be a broken line rather than a fixed one.
    private static readonly string[] NotHeadings =
        ["include", "define", "pragma", "ifdef", "ifndef", "endif", "region", "endregion"];

    /// <summary>Every line that looks like a heading but will not render as one.</summary>
    public static IReadOnlyList<Hint> For(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return [];

        var lines = markdown.Split('\n');
        var hints = new List<Hint>();

        foreach (var index in TightHeadingLines(lines))
        {
            var line = lines[index].TrimEnd('\r');
            var match = TightHeading().Match(line);
            hints.Add(new Hint(
                index + 1,
                line,
                $"{match.Groups[1].Value}{match.Groups[2].Value} {match.Groups[3].Value}"));
        }

        return hints;
    }

    /// <summary>
    /// The source with a space added to exactly the lines <see cref="For"/> reports, and
    /// nothing else touched — not the line endings, not the trailing newline.
    /// </summary>
    public static string Fix(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown ?? "";

        var lines = markdown.Split('\n');

        foreach (var index in TightHeadingLines(lines))
        {
            // Split('\n') leaves the \r of a CRLF file on the end of every line. Repairing
            // the line has to put it back, or fixing one heading rewrites the whole file's
            // line endings and every diff of the note becomes unreadable.
            var carriageReturn = lines[index].EndsWith('\r') ? "\r" : "";
            var match = TightHeading().Match(lines[index].TrimEnd('\r'));
            lines[index] =
                $"{match.Groups[1].Value}{match.Groups[2].Value} {match.Groups[3].Value}{carriageReturn}";
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Indexes of the offending lines. Shared so the hint and the repair can never disagree
    /// about which lines they mean.
    /// </summary>
    private static IEnumerable<int> TightHeadingLines(string[] lines)
    {
        char? fence = null;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');

            if (Fence().Match(line) is { Success: true } marker)
            {
                // A fence only closes with the character that opened it, so ~~~ inside a
                // ``` block is content rather than the end of the block.
                var character = marker.Groups[1].Value[0];
                fence = fence is null ? character : fence == character ? null : fence;
                continue;
            }

            if (fence is not null)
                continue;

            var match = TightHeading().Match(line);
            if (!match.Success)
                continue;

            var word = match.Groups[3].Value;
            if (NotHeadings.Any(w => word.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
                continue;

            yield return index;
        }
    }
}
