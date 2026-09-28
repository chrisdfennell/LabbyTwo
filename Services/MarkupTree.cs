using System.Net;
using System.Text.RegularExpressions;

namespace LabbyTwo.Services;

/// <summary>A piece of rendered Markdown: some text, or an element with more inside it.</summary>
public abstract record MarkupNode;

/// <summary>Text exactly as the renderer wrote it, entities and all. Never contains a tag.</summary>
public sealed record MarkupText(string Html) : MarkupNode;

/// <summary>An element, with its attributes decoded and its original HTML kept whole.</summary>
public sealed record MarkupElement(
    string Name,
    IReadOnlyList<KeyValuePair<string, string>> Attributes,
    IReadOnlyList<MarkupNode> Children,
    string OuterHtml) : MarkupNode;

/// <summary>
/// Reads the Markdown renderer's HTML back into elements, so a live value can be put in
/// the middle of a paragraph.
///
/// Blazor inserts each piece of raw markup on its own, so the obvious approach — cut the
/// HTML at each placeholder and put a component between the halves — hands the browser
/// <c>&lt;p&gt;NAS is</c> and <c>used.&lt;/p&gt;</c> as two separate fragments, and it
/// closes the first paragraph early and throws the stray end tag away. Instead, the
/// elements that contain a placeholder are rebuilt as real render-tree elements and
/// everything else is passed through as the markup it already was.
///
/// Not a general HTML parser and not trying to be one: its only input is what Markdig
/// writes with raw HTML turned off — tags it chose, attributes it quoted, text it escaped —
/// so every <c>&lt;</c> in that output starts a tag. Anything unexpected (a stray end tag)
/// is tolerated rather than thrown about.
/// </summary>
public static partial class MarkupTree
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr",
    };

    [GeneratedRegex("""<(/?)([A-Za-z][A-Za-z0-9-]*)((?:\s+[^\s/>"'=]+(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s"'=<>`]+))?)*)\s*(/?)>""")]
    private static partial Regex Tag();

    [GeneratedRegex("""([^\s/>"'=]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'=<>`]+)))?""")]
    private static partial Regex Attribute();

    private sealed class Open(string name, IReadOnlyList<KeyValuePair<string, string>> attributes, int start)
    {
        public string Name { get; } = name;
        public IReadOnlyList<KeyValuePair<string, string>> Attributes { get; } = attributes;
        public int Start { get; } = start;
        public List<MarkupNode> Children { get; } = [];
    }

    public static IReadOnlyList<MarkupNode> Parse(string html)
    {
        var root = new List<MarkupNode>();
        var stack = new Stack<Open>();
        List<MarkupNode> Current() => stack.Count > 0 ? stack.Peek().Children : root;

        void Close(Open element, int end) =>
            (stack.Count > 0 ? stack.Peek().Children : root)
                .Add(new MarkupElement(element.Name, element.Attributes, element.Children, html[element.Start..end]));

        var at = 0;
        foreach (Match tag in Tag().Matches(html))
        {
            if (tag.Index > at)
                Current().Add(new MarkupText(html[at..tag.Index]));
            at = tag.Index + tag.Length;

            var name = tag.Groups[2].Value.ToLowerInvariant();
            if (tag.Groups[1].Length > 0)
            {
                // An end tag closes the nearest element of its name and anything left open
                // inside it. One that matches nothing is dropped.
                if (!stack.Any(o => o.Name == name))
                    continue;
                while (stack.Count > 0)
                {
                    var open = stack.Pop();
                    Close(open, open.Name == name ? at : tag.Index);
                    if (open.Name == name)
                        break;
                }
                continue;
            }

            var attributes = ReadAttributes(tag.Groups[3].Value);
            if (VoidElements.Contains(name) || tag.Groups[4].Length > 0)
                Current().Add(new MarkupElement(name, attributes, [], tag.Value));
            else
                stack.Push(new Open(name, attributes, tag.Index));
        }

        if (at < html.Length)
            Current().Add(new MarkupText(html[at..]));
        while (stack.Count > 0)
            Close(stack.Pop(), html.Length);

        return root;
    }

    private static List<KeyValuePair<string, string>> ReadAttributes(string text)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (Match attribute in Attribute().Matches(text))
        {
            var value = attribute.Groups[2].Success ? attribute.Groups[2].Value
                : attribute.Groups[3].Success ? attribute.Groups[3].Value
                : attribute.Groups[4].Success ? attribute.Groups[4].Value
                : "";
            // Decoded because the render tree encodes attribute values itself; passing the
            // renderer's &amp; through would show up as a literal "&amp;" in a link.
            list.Add(new(attribute.Groups[1].Value.ToLowerInvariant(), WebUtility.HtmlDecode(value)));
        }
        return list;
    }
}
