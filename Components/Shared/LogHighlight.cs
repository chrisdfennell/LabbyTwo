using Microsoft.AspNetCore.Components.Rendering;

namespace LabbyTwo.Components.Shared;

/// <summary>
/// A log line with the parts a search matched wrapped in <c>&lt;mark&gt;</c>. Every piece goes
/// in as text, never as markup: a log is written by whatever runs in a container, and a line
/// holding <c>&lt;script&gt;</c> must be shown as those characters.
/// </summary>
public static class LogHighlight
{
    /// <summary>The line cut at the spans: each piece and whether it is a match.</summary>
    public static IReadOnlyList<(string Text, bool Marked)> Pieces(string text, IReadOnlyList<(int Start, int Length)> spans)
    {
        var pieces = new List<(string, bool)>();
        var at = 0;
        foreach (var (start, length) in spans.OrderBy(s => s.Start))
        {
            // Spans come from the masked text they were found in, but a defensive clamp costs
            // nothing and keeps an off-by-one from throwing in the middle of a render.
            var from = Math.Clamp(start, at, text.Length);
            var to = Math.Clamp(start + length, from, text.Length);
            if (from > at)
                pieces.Add((text[at..from], false));
            if (to > from)
                pieces.Add((text[from..to], true));
            at = to;
        }
        if (at < text.Length)
            pieces.Add((text[at..], false));
        return pieces;
    }

    public static void Build(RenderTreeBuilder builder, string text, IReadOnlyList<(int Start, int Length)> spans)
    {
        foreach (var (piece, marked) in Pieces(text, spans))
        {
            if (marked)
            {
                builder.OpenElement(0, "mark");
                builder.AddContent(1, piece);
                builder.CloseElement();
            }
            else
            {
                builder.AddContent(2, piece);
            }
        }
    }
}
