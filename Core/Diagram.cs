using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LabbyTwo.Core;

public enum DiagramDirection
{
    /// <summary>Left to right — the default, for the reason the dependency map is: a home lab is wide and shallow.</summary>
    LeftRight,
    TopDown,
}

public enum EdgeStyle
{
    Arrow,

    /// <summary>Mermaid's <c>---</c>: a link with no direction.</summary>
    Line,

    /// <summary>Mermaid's <c>-.-&gt;</c>.</summary>
    Dotted,

    /// <summary>Mermaid's <c>==&gt;</c>.</summary>
    Thick,
}

/// <param name="Key">What edges refer to it by: the name in the native syntax, the id in Mermaid's.</param>
/// <param name="Label">What is drawn in the box, and what is matched against connection names.</param>
public sealed record DiagramNode(string Key, string Label);

public sealed record DiagramEdge(string From, string To, string Label, EdgeStyle Style);

public sealed record DiagramGraph(IReadOnlyList<DiagramNode> Nodes, IReadOnlyList<DiagramEdge> Edges, DiagramDirection Direction);

/// <summary>
/// Reads the text of a <c>```diagram</c> or <c>```mermaid</c> block into nodes and edges.
/// Pure text work, so every odd thing somebody can type is pinned by a test.
///
/// Two syntaxes, chosen by the first line that says anything:
/// <list type="bullet">
/// <item><b>LabbyTwo's own</b>, written the way you would sketch it in a text file:
/// <c>Internet -&gt; Router -&gt; "QNAP NAS" -&gt; Plex</c>. A node is its name, so the name
/// is what gets matched to a connection; quotes are optional unless the name has a comma,
/// an arrow or a quote in it. Commas fan out and in (<c>NAS -&gt; Sonarr, Radarr</c>),
/// <c>-&gt;|over VPN|</c> labels an edge, and <c>direction TD</c> on the first line draws it
/// top-down. <c>#</c> starts a comment.</item>
/// <item><b>A subset of Mermaid's flowchart</b>, recognised by a first line of <c>graph</c> or
/// <c>flowchart</c> and a direction (TD, TB, BT, LR, RL). Nodes are ids with an optional
/// shape holding their label — <c>A[Label]</c>, <c>A(Label)</c>, <c>A((Label))</c>,
/// <c>A{Label}</c>, <c>A([Label])</c>, <c>A[[Label]]</c>, <c>A[(Label)]</c>, <c>A&gt;Label]</c>,
/// with <c>"quoted text"</c> inside any of them; the shape itself is not drawn, every node is a
/// box. Edges are <c>--&gt;</c>, <c>---</c>, <c>-.-&gt;</c>, <c>==&gt;</c> (longer ones too), labelled
/// as <c>--&gt;|text|</c> or <c>-- text --&gt;</c>; <c>A &amp; B --&gt; C</c> and chains work.
/// <c>%%</c> comments, <c>subgraph</c>/<c>end</c>, <c>classDef</c>, <c>class</c>, <c>style</c>,
/// <c>linkStyle</c> and <c>click</c> lines are read past, so a diagram copied from elsewhere
/// still draws; the subgraph's own box is not drawn. Nothing else of Mermaid is understood.</item>
/// </list>
/// </summary>
public static partial class DiagramParser
{
    /// <summary>A diagram bigger than this is not readable inside a note, and its layout would start to cost.</summary>
    public const int MaxNodes = 80;

    public const int MaxEdges = 200;

    /// <summary>The longest label kept. A box is drawn at most a few words wide anyway.</summary>
    public const int MaxLabel = 80;

    [GeneratedRegex(@"^\s*(?:graph|flowchart)(?:\s+(TD|TB|BT|LR|RL))?\s*;?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MermaidHeader();

    [GeneratedRegex(@"^\s*direction\s*:?\s*(TD|TB|BT|LR|RL)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex NativeDirection();

    /// <summary>Whether the text is written in the Mermaid subset — its first line that says anything is a graph header.</summary>
    public static bool IsMermaid(string text) =>
        FirstLine(text) is { } first && MermaidHeader().IsMatch(first);

    /// <summary>
    /// The graph, or null with a sentence saying what is wrong. Either syntax is accepted in
    /// either fence; Mermaid text is known by its header line.
    /// </summary>
    public static DiagramGraph? Parse(string text, out string? problem)
    {
        problem = null;
        var builder = new Builder();
        var graph = IsMermaid(text) ? ParseMermaid(text, builder, out problem) : ParseNative(text, builder, out problem);
        if (graph is null)
            return null;
        if (graph.Nodes.Count == 0)
        {
            problem = "This diagram has nothing in it. Write a line like Internet -> Router -> NAS.";
            return null;
        }
        if (graph.Nodes.Count > MaxNodes)
        {
            problem = $"A diagram draws at most {MaxNodes} boxes; this one has {graph.Nodes.Count}.";
            return null;
        }
        if (graph.Edges.Count > MaxEdges)
        {
            problem = $"A diagram draws at most {MaxEdges} lines; this one has {graph.Edges.Count}.";
            return null;
        }
        return graph;
    }

    private static string? FirstLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal) || line.StartsWith('#'))
                continue;
            return line;
        }
        return null;
    }

    // ---------------------------------------------------------------- native

    [GeneratedRegex(@"(?:-+>|=+>)(?:\s*\|(?<label>[^|]*)\|)?")]
    private static partial Regex NativeArrow();

    private static DiagramGraph? ParseNative(string text, Builder builder, out string? problem)
    {
        problem = null;
        var direction = DiagramDirection.LeftRight;
        var lineNumber = 0;
        var first = true;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = StripComment(raw).Trim();
            if (line.Length == 0)
                continue;
            if (first && NativeDirection().Match(line) is { Success: true } match)
            {
                direction = Direction(match.Groups[1].Value);
                first = false;
                continue;
            }
            first = false;

            // Split at arrows that are not inside quotes.
            var segments = new List<string>();
            var labels = new List<string>();
            var at = 0;
            foreach (Match arrow in NativeArrow().Matches(line))
            {
                if (InsideQuotes(line, arrow.Index))
                    continue;
                segments.Add(line[at..arrow.Index]);
                labels.Add(arrow.Groups["label"].Value.Trim());
                at = arrow.Index + arrow.Length;
            }
            segments.Add(line[at..]);

            var groups = new List<List<string>>();
            foreach (var segment in segments)
            {
                var names = ShortcodeArguments.Split(segment, ',').Select(Unquote).Where(n => n.Length > 0).ToList();
                if (names.Count == 0)
                {
                    problem = $"Line {lineNumber} has an arrow with nothing at one end: “{line}”.";
                    return null;
                }
                groups.Add(names);
            }

            // No label: the name is its own, as first written — "router" later is the same box.
            foreach (var group in groups)
                foreach (var name in group)
                    builder.Node(name, null);
            for (var i = 0; i + 1 < groups.Count; i++)
                foreach (var from in groups[i])
                    foreach (var to in groups[i + 1])
                        builder.Edge(from, to, labels[i], EdgeStyle.Arrow);
        }
        return builder.Build(direction);
    }

    /// <summary>A <c>#</c> outside quotes ends the line's meaning.</summary>
    private static string StripComment(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                quoted = !quoted;
            else if (line[i] == '#' && !quoted)
                return line[..i];
        }
        return line;
    }

    private static bool InsideQuotes(string line, int index)
    {
        var quotes = 0;
        for (var i = 0; i < index; i++)
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
                quotes++;
        return quotes % 2 == 1;
    }

    private static string Unquote(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            trimmed = trimmed[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return Clip(trimmed.Trim());
    }

    private static string Clip(string text) => text.Length <= MaxLabel ? text : text[..MaxLabel];

    private static DiagramDirection Direction(string word) =>
        word.ToUpperInvariant() is "TD" or "TB" or "BT" ? DiagramDirection.TopDown : DiagramDirection.LeftRight;

    // ---------------------------------------------------------------- mermaid

    private static readonly string[] IgnoredStatements = ["subgraph", "end", "classdef", "class", "style", "linkstyle", "click", "direction", "accTitle", "accDescr"];

    [GeneratedRegex(@"\G\s*(?<arrow><?(?:-{2,}>|-{3,}|-\.+->|-\.+-|={2,}>|={3,}|-{2,}[xo]))\s*(?:\|(?<label>[^|]*)\|)?")]
    private static partial Regex MermaidArrow();

    [GeneratedRegex(@"\G\s*(?<open>--|==|-\.)\s*(?<label>[^-=.>|][^>|]*?)\s*(?<arrow>-{2,}>|-{3,}|\.+->|\.+-|={2,}>|={3,})")]
    private static partial Regex MermaidTextArrow();

    [GeneratedRegex(@"\G\s*(?<id>[\p{L}\p{N}_][\p{L}\p{N}_\-]*)")]
    private static partial Regex MermaidId();

    private static DiagramGraph? ParseMermaid(string text, Builder builder, out string? problem)
    {
        problem = null;
        var direction = DiagramDirection.TopDown;
        var header = true;
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal))
                continue;
            if (header)
            {
                var match = MermaidHeader().Match(line);
                direction = match.Groups[1].Success ? Direction(match.Groups[1].Value) : DiagramDirection.TopDown;
                header = false;
                continue;
            }

            foreach (var statement in ShortcodeArguments.Split(line, ';'))
            {
                var trimmed = statement.Trim();
                if (trimmed.Length == 0)
                    continue;
                var firstWord = trimmed.Split([' ', '\t'], 2)[0];
                if (IgnoredStatements.Contains(firstWord, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (!MermaidStatement(trimmed, builder, out var why))
                {
                    problem = $"Line {lineNumber} is not part of the Mermaid this understands ({why}): “{Clip(trimmed)}”.";
                    return null;
                }
            }
        }
        return builder.Build(direction);
    }

    /// <summary>One statement: groups of nodes joined by arrows.</summary>
    private static bool MermaidStatement(string text, Builder builder, out string why)
    {
        why = "";
        var at = 0;
        List<string>? previous = null;
        (string Label, EdgeStyle Style)? pending = null;

        while (true)
        {
            var group = new List<string>();
            while (true)
            {
                if (!MermaidNode(text, ref at, builder, out var key, out why))
                    return false;
                group.Add(key);
                var amp = SkipSpaces(text, at);
                if (amp < text.Length && text[amp] == '&')
                {
                    at = amp + 1;
                    continue;
                }
                break;
            }

            if (previous is not null && pending is { } edge)
                foreach (var from in previous)
                    foreach (var to in group)
                        builder.Edge(from, to, edge.Label, edge.Style);

            at = SkipSpaces(text, at);
            if (at >= text.Length)
                return true;

            if (MermaidTextArrow().Match(text, at) is { Success: true } labelled)
            {
                pending = (Unquote(labelled.Groups["label"].Value), StyleOf(labelled.Groups["open"].Value + labelled.Groups["arrow"].Value));
                at = labelled.Index + labelled.Length;
            }
            else if (MermaidArrow().Match(text, at) is { Success: true } arrow)
            {
                pending = (Unquote(arrow.Groups["label"].Value), StyleOf(arrow.Groups["arrow"].Value));
                at = arrow.Index + arrow.Length;
            }
            else
            {
                why = $"“{Clip(text[at..])}” is not an arrow";
                return false;
            }
            previous = group;
        }
    }

    private static EdgeStyle StyleOf(string arrow) =>
        arrow.Contains('.') ? EdgeStyle.Dotted
        : arrow.Contains('=') ? EdgeStyle.Thick
        : arrow.EndsWith('>') ? EdgeStyle.Arrow
        : EdgeStyle.Line;

    private static int SkipSpaces(string text, int at)
    {
        while (at < text.Length && char.IsWhiteSpace(text[at]))
            at++;
        return at;
    }

    /// <summary>An id, then optionally a shape with the label inside it, then optionally <c>:::class</c>.</summary>
    private static bool MermaidNode(string text, ref int at, Builder builder, out string key, out string why)
    {
        key = "";
        why = "";
        var id = MermaidId().Match(text, at);
        if (!id.Success)
        {
            why = at >= text.Length ? "it ends with an arrow" : $"“{Clip(text[at..])}” is not a node";
            return false;
        }
        key = id.Groups["id"].Value;
        // An id that runs straight into an arrow ("A-->B") took the arrow's dashes with it.
        var dashes = key.Length - key.TrimEnd('-').Length;
        if (dashes > 0)
            key = key[..^dashes];
        at = id.Index + id.Length - dashes;

        string? label = null;
        if (at < text.Length && text[at] is '[' or '(' or '{' or '>')
        {
            var end = ShapeEnd(text, at);
            if (end < 0)
            {
                why = $"the shape after “{key}” is never closed";
                return false;
            }
            var inner = text[at..end].TrimStart('[', '(', '{', '>').TrimEnd(']', ')', '}');
            label = Unquote(inner);
            at = end;
        }
        if (at + 3 <= text.Length && string.CompareOrdinal(text, at, ":::", 0, 3) == 0)
        {
            at += 3;
            while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] is '_' or '-'))
                at++;
        }

        builder.Node(key, label);
        return true;
    }

    /// <summary>Just past the bracket that closes the one at <paramref name="start"/>, quotes respected; -1 if it never closes.</summary>
    private static int ShapeEnd(string text, int start)
    {
        var depth = 0;
        var quoted = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (quoted)
                continue;
            if (c is '[' or '(' or '{' || (c == '>' && i == start))
                depth++;
            else if (c is ']' or ')' or '}')
            {
                depth--;
                if (depth == 0)
                    return i + 1;
            }
        }
        return -1;
    }

    /// <summary>Collects nodes in the order they are first met, and edges without repeats.</summary>
    private sealed class Builder
    {
        private readonly List<string> _order = [];
        private readonly Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<DiagramEdge> _edges = [];
        private readonly HashSet<(string, string, string)> _seen = [];

        /// <summary>
        /// A node by key. Keys compare ignoring case, so "NAS" and "nas" in two lines are one
        /// box. A label given later fills in one not given earlier — Mermaid lets a node be
        /// used first and shaped afterwards.
        /// </summary>
        public void Node(string key, string? label)
        {
            if (!_keys.ContainsKey(key))
            {
                _keys[key] = key;
                _order.Add(key);
            }
            if (label is { Length: > 0 })
                _labels[_keys[key]] = label;
            else
                _labels.TryAdd(_keys[key], _keys[key]);
        }

        public void Edge(string from, string to, string label, EdgeStyle style)
        {
            var a = _keys[from];
            var b = _keys[to];
            if (_seen.Add((a.ToUpperInvariant(), b.ToUpperInvariant(), label)))
                _edges.Add(new DiagramEdge(a, b, label, style));
        }

        public DiagramGraph Build(DiagramDirection direction) =>
            new([.. _order.Select(k => new DiagramNode(k, _labels[k]))], _edges, direction);
    }
}

public sealed record PlacedBox(DiagramNode Node, int Layer, int Slot, double X, double Y, double Width, double Height);

/// <param name="Path">The SVG path from one box's edge to the other's.</param>
/// <param name="LabelX">Where the label's middle goes.</param>
public sealed record PlacedLink(DiagramEdge Edge, string Path, double LabelX, double LabelY, bool Backwards);

public sealed record DiagramLayoutResult(IReadOnlyList<PlacedBox> Boxes, IReadOnlyList<PlacedLink> Links, double Width, double Height, DiagramDirection Direction);

/// <summary>
/// Places a diagram in layers — a plain Sugiyama-style layout, small enough to read in one
/// sitting and run on the server, so no script has to be fetched from anywhere to draw it.
///
/// <list type="number">
/// <item>Loops are broken by turning round the edges a depth-first walk finds going back up,
/// in the order the nodes were written, so the same text always lays out the same way. They
/// are still drawn pointing the way they were written.</item>
/// <item>Each node goes in the layer after the furthest of its predecessors (longest path),
/// so a chain reads in order and everything a node depends on is before it.</item>
/// <item>Within a layer, nodes start in the order written and are then sorted a few times,
/// down and back up, by the average position of what they are linked to — the barycentre
/// heuristic, which removes most crossings in diagrams this size.</item>
/// <item>Layers are centred on each other, and edges are curves from one box's side to the
/// next box's.</item>
/// </list>
/// An edge that skips layers is drawn straight across them and may pass behind a box; a
/// diagram that needs better routing than that is a diagram that needs fewer edges.
/// </summary>
public static class DiagramLayout
{
    public const double BoxHeight = 40;
    public const double MinBoxWidth = 88;
    public const double MaxBoxWidth = 200;
    public const double LayerGap = 64;
    public const double SlotGap = 18;
    public const double Margin = 8;

    /// <summary>Roughly the width of one character of the box's text, for sizing boxes without measuring fonts.</summary>
    public const double CharWidth = 7.2;

    /// <summary>The most characters a box shows; the rest is in its tooltip.</summary>
    public static int MaxChars => (int)((MaxBoxWidth - 24) / CharWidth);

    public static DiagramLayoutResult Layout(DiagramGraph graph)
    {
        var nodes = graph.Nodes;
        if (nodes.Count == 0)
            return new([], [], 0, 0, graph.Direction);

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < nodes.Count; i++)
            index[nodes[i].Key] = i;

        var edges = graph.Edges.Where(e => index.ContainsKey(e.From) && index.ContainsKey(e.To)).ToList();
        var backwards = BackEdges(nodes.Count, edges, index);

        // Directed acyclic edges, self-loops left out (they would be a box pointing at itself).
        var forward = new List<(int From, int To)>();
        for (var i = 0; i < edges.Count; i++)
        {
            var from = index[edges[i].From];
            var to = index[edges[i].To];
            if (from == to)
                continue;
            forward.Add(backwards.Contains(i) ? (to, from) : (from, to));
        }

        var layer = Layers(nodes.Count, forward);
        var layers = Enumerable.Range(0, layer.Max() + 1)
            .Select(l => Enumerable.Range(0, nodes.Count).Where(n => layer[n] == l).ToList())
            .ToList();
        Order(layers, forward, nodes.Count);

        var widest = nodes.Max(n => Math.Min(n.Label.Length, MaxChars));
        var boxWidth = Math.Clamp(widest * CharWidth + 24, MinBoxWidth, MaxBoxWidth);
        var leftRight = graph.Direction == DiagramDirection.LeftRight;

        // Along the layers is one step per layer; across them, each layer is centred on the widest.
        var step = leftRight ? boxWidth + LayerGap : BoxHeight + LayerGap;
        var pitch = leftRight ? BoxHeight + SlotGap : boxWidth + SlotGap;
        var mostSlots = layers.Max(l => l.Count);

        var boxes = new PlacedBox[nodes.Count];
        for (var l = 0; l < layers.Count; l++)
        {
            var offset = (mostSlots - layers[l].Count) * pitch / 2;
            for (var s = 0; s < layers[l].Count; s++)
            {
                var n = layers[l][s];
                var along = Margin + l * step;
                var across = Margin + offset + s * pitch;
                boxes[n] = leftRight
                    ? new PlacedBox(nodes[n], l, s, along, across, boxWidth, BoxHeight)
                    : new PlacedBox(nodes[n], l, s, across, along, boxWidth, BoxHeight);
            }
        }

        var links = new List<PlacedLink>();
        for (var i = 0; i < edges.Count; i++)
        {
            var from = boxes[index[edges[i].From]];
            var to = boxes[index[edges[i].To]];
            if (ReferenceEquals(from, to))
                continue;
            links.Add(Link(edges[i], from, to, leftRight));
        }

        var width = leftRight
            ? 2 * Margin + (layers.Count - 1) * step + boxWidth
            : 2 * Margin + mostSlots * pitch - SlotGap;
        var height = leftRight
            ? 2 * Margin + mostSlots * pitch - SlotGap
            : 2 * Margin + (layers.Count - 1) * step + BoxHeight;
        // Room for a backwards edge's loop under (or beside) the boxes.
        if (links.Any(l => l.Backwards))
        {
            if (leftRight)
                height += LayerGap / 2;
            else
                width += LayerGap / 2;
        }

        return new DiagramLayoutResult(boxes, links, width, height, graph.Direction);
    }

    /// <summary>The edges a depth-first walk, in written order, finds pointing back up the path it is on.</summary>
    private static HashSet<int> BackEdges(int count, List<DiagramEdge> edges, Dictionary<string, int> index)
    {
        var outgoing = Enumerable.Range(0, count).Select(_ => new List<(int To, int Edge)>()).ToArray();
        for (var i = 0; i < edges.Count; i++)
            outgoing[index[edges[i].From]].Add((index[edges[i].To], i));

        var state = new int[count]; // 0 unseen, 1 on the path, 2 done
        var back = new HashSet<int>();
        for (var start = 0; start < count; start++)
        {
            if (state[start] != 0)
                continue;
            // Iterative, so a long chain cannot run the stack out.
            var stack = new Stack<(int Node, int Next)>();
            stack.Push((start, 0));
            state[start] = 1;
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                if (next < outgoing[node].Count)
                {
                    stack.Push((node, next + 1));
                    var (to, edge) = outgoing[node][next];
                    if (state[to] == 1)
                        back.Add(edge);
                    else if (state[to] == 0)
                    {
                        state[to] = 1;
                        stack.Push((to, 0));
                    }
                }
                else
                {
                    state[node] = 2;
                }
            }
        }
        return back;
    }

    /// <summary>Longest path from a source, by Kahn's algorithm over the acyclic edges.</summary>
    private static int[] Layers(int count, List<(int From, int To)> edges)
    {
        var incoming = new int[count];
        var outgoing = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        foreach (var (from, to) in edges)
        {
            outgoing[from].Add(to);
            incoming[to]++;
        }
        var layer = new int[count];
        var ready = new Queue<int>(Enumerable.Range(0, count).Where(n => incoming[n] == 0));
        while (ready.TryDequeue(out var node))
        {
            foreach (var to in outgoing[node])
            {
                layer[to] = Math.Max(layer[to], layer[node] + 1);
                if (--incoming[to] == 0)
                    ready.Enqueue(to);
            }
        }
        return layer;
    }

    /// <summary>Barycentre sweeps, down then up, a few times. Ties keep the order they had, so the result is stable.</summary>
    private static void Order(List<List<int>> layers, List<(int From, int To)> edges, int count)
    {
        var position = new double[count];
        void Number()
        {
            foreach (var layer in layers)
                for (var i = 0; i < layer.Count; i++)
                    position[layer[i]] = layer.Count == 1 ? 0.5 : (double)i / (layer.Count - 1);
        }
        Number();

        var up = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        var down = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        foreach (var (from, to) in edges)
        {
            down[from].Add(to);
            up[to].Add(from);
        }

        for (var sweep = 0; sweep < 4; sweep++)
        {
            for (var l = 1; l < layers.Count; l++)
                Sort(layers[l], up);
            for (var l = layers.Count - 2; l >= 0; l--)
                Sort(layers[l], down);
        }

        void Sort(List<int> layer, List<int>[] neighbours)
        {
            var keyed = layer
                .Select((node, i) => (Node: node, Key: neighbours[node].Count > 0 ? neighbours[node].Average(n => position[n]) : position[node], Was: i))
                .OrderBy(x => x.Key)
                .ThenBy(x => x.Was)
                .Select(x => x.Node)
                .ToList();
            layer.Clear();
            layer.AddRange(keyed);
            for (var i = 0; i < layer.Count; i++)
                position[layer[i]] = layer.Count == 1 ? 0.5 : (double)i / (layer.Count - 1);
        }
    }

    private static PlacedLink Link(DiagramEdge edge, PlacedBox from, PlacedBox to, bool leftRight)
    {
        var forward = to.Layer > from.Layer;
        if (leftRight)
        {
            if (forward)
            {
                var (x1, y1, x2, y2) = (from.X + from.Width, from.Y + from.Height / 2, to.X, to.Y + to.Height / 2);
                var mid = (x1 + x2) / 2;
                return new PlacedLink(edge, Path($"M{x1},{y1} C{mid},{y1} {mid},{y2} {x2},{y2}"), mid, (y1 + y2) / 2 - 4, false);
            }
            // Backwards or within a layer: a loop under both boxes.
            var (bx1, by1, bx2, by2) = (from.X + from.Width / 2, from.Y + from.Height, to.X + to.Width / 2, to.Y + to.Height);
            var below = Math.Max(by1, by2) + LayerGap / 2;
            return new PlacedLink(edge, Path($"M{bx1},{by1} C{bx1},{below} {bx2},{below} {bx2},{by2}"), (bx1 + bx2) / 2, below - 4, true);
        }

        if (forward)
        {
            var (x1, y1, x2, y2) = (from.X + from.Width / 2, from.Y + from.Height, to.X + to.Width / 2, to.Y);
            var mid = (y1 + y2) / 2;
            return new PlacedLink(edge, Path($"M{x1},{y1} C{x1},{mid} {x2},{mid} {x2},{y2}"), (x1 + x2) / 2, mid + 4, false);
        }
        var (rx1, ry1, rx2, ry2) = (from.X + from.Width, from.Y + from.Height / 2, to.X + to.Width, to.Y + to.Height / 2);
        var right = Math.Max(rx1, rx2) + LayerGap / 2;
        return new PlacedLink(edge, Path($"M{rx1},{ry1} C{right},{ry1} {right},{ry2} {rx2},{ry2}"), right - 4, (ry1 + ry2) / 2, true);
    }

    /// <summary>The path with every number written the invariant way, whatever the server's culture.</summary>
    private static string Path(FormattableString path) =>
        string.Format(CultureInfo.InvariantCulture, path.Format,
            [.. path.GetArguments().Select(a => a is double d ? Math.Round(d, 1) : a)]);

    /// <summary>A label cut to fit its box, with an ellipsis; the whole of it goes in the tooltip.</summary>
    public static string Clip(string text, int length) =>
        text.Length <= length ? text : text[..Math.Max(1, length - 1)].TrimEnd() + "…";
}
