using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>One thing on the dependency map: an id and the id of what it sits behind.</summary>
public sealed record DependencyInput(string Id, string? ParentId);

/// <summary>
/// Why a node that names a parent is drawn as a root anyway. Shown next to it, because a
/// "Sits behind" that silently stopped meaning anything is how an alert goes unsuppressed
/// without anybody knowing why.
/// </summary>
public enum BrokenLink
{
    None,

    /// <summary>The parent was deleted, or never existed in this set.</summary>
    MissingParent,

    /// <summary>It sat behind something that, somewhere up the line, sat behind it.</summary>
    Cycle,
}

/// <param name="Column">How many steps from its root: 0 for a root, 1 for what sits behind it.</param>
/// <param name="Row">
/// Vertical slot, in rows. Fractional for a parent, which is centred on its children.
/// </param>
public sealed record PlacedNode(
    string Id,
    string? ParentId,
    int Column,
    double Row,
    double X,
    double Y,
    BrokenLink Broken);

/// <param name="Path">An SVG path from the parent's right edge to the child's left edge.</param>
public sealed record PlacedEdge(string ParentId, string ChildId, string Path);

/// <summary>
/// A class rather than a record: it caches a lookup, and a record would compare that too.
/// </summary>
public sealed class DependencyLayoutResult(
    IReadOnlyList<PlacedNode> nodes,
    IReadOnlyList<PlacedEdge> edges,
    IReadOnlyList<PlacedNode> standAlone,
    double width,
    double height)
{
    public static readonly DependencyLayoutResult Empty = new([], [], [], 0, 0);

    /// <summary>The nodes in trees, column by column, top to bottom within each.</summary>
    public IReadOnlyList<PlacedNode> Nodes { get; } = nodes;

    public IReadOnlyList<PlacedEdge> Edges { get; } = edges;

    /// <summary>Roots with nothing behind them, in input order. Not given a position.</summary>
    public IReadOnlyList<PlacedNode> StandAlone { get; } = standAlone;

    /// <summary>The size of the drawing of the trees, margins included.</summary>
    public double Width { get; } = width;

    public double Height { get; } = height;

    /// <summary>Every node, the ones in trees and the stand-alone ones, keyed by id.</summary>
    public IReadOnlyDictionary<string, PlacedNode> ById =>
        field ??= Nodes.Concat(StandAlone).ToDictionary(node => node.Id);

    /// <summary>The sanitised parent of every node, for <see cref="DependencyLayout.Affected"/>.</summary>
    public IReadOnlyDictionary<string, string?> Parents =>
        field ??= ById.ToDictionary(pair => pair.Key, pair => pair.Value.ParentId);
}

/// <summary>
/// Places connections in layers by what they sit behind: roots on the left, what sits
/// behind them one column to the right, and so on. A pure function of its input, so the
/// map on the page, the one on the wall and the tests all agree on where everything goes.
///
/// Each connection names at most one parent, so the input is a forest and not a general
/// graph. That is what makes this simple: the first layer keeps the order the connections
/// were given in, and every later layer is sorted by the position of its parent (the
/// barycentre of a node with one parent is just that parent), which keeps siblings
/// together and in the same order as the parents — and a forest ordered that way has no
/// crossing edges at all. Rows are then given out leaf by leaf, depth first, and each
/// parent sits in the middle of its children, the usual tidy-tree shape.
///
/// Left to right rather than top down because a home lab is wide and shallow: one router
/// with thirty things behind it is thirty leaves, which as a column scrolls the way a page
/// already does, and as a row is a strip nobody could read on a phone.
/// </summary>
public static class DependencyLayout
{
    public const double NodeWidth = 208;
    public const double NodeHeight = 48;
    public const double ColumnGap = 72;
    public const double RowGap = 14;

    /// <summary>Extra space between two separate trees, in rows, so they read as separate.</summary>
    public const double TreeGap = 0.5;

    public const double Margin = 8;

    private static double RowPitch => NodeHeight + RowGap;

    public static DependencyLayoutResult Layout(IEnumerable<DependencyInput> input)
    {
        // The first of a repeated id wins, and a blank id is skipped. Neither can come out
        // of the connections table, but this is also what a widget's filtered subset and a
        // test's hand-written list go through, and a duplicate key would throw below.
        var order = new List<string>();
        var declared = new Dictionary<string, string?>();
        foreach (var node in input)
        {
            if (string.IsNullOrEmpty(node.Id) || declared.ContainsKey(node.Id))
                continue;
            declared[node.Id] = node.ParentId;
            order.Add(node.Id);
        }

        if (order.Count == 0)
            return DependencyLayoutResult.Empty;

        var (parent, broken) = Sanitise(order, declared);

        var index = new Dictionary<string, int>(order.Count);
        for (var i = 0; i < order.Count; i++)
            index[order[i]] = i;

        var children = order.ToDictionary(id => id, _ => new List<string>());
        foreach (var id in order)
            if (parent[id] is { } p)
                children[p].Add(id);

        // A root with nothing behind it has no edges to draw, and scattering these between
        // the trees would make each tree harder to follow. They are listed on their own.
        var roots = order.Where(id => parent[id] is null).ToList();
        var standAloneIds = roots.Where(id => children[id].Count == 0).ToList();
        var treeRoots = roots.Where(id => children[id].Count > 0).ToList();

        var layers = Layers(treeRoots, children, index);

        // Rows, leaf by leaf. Children are visited in the order the layer sort gave them,
        // which keeps the rows consistent with the layers and so free of crossings.
        var position = new Dictionary<string, int>();
        foreach (var layer in layers)
            for (var i = 0; i < layer.Count; i++)
                position[layer[i]] = i;

        var column = new Dictionary<string, int>();
        var row = new Dictionary<string, double>();
        var next = 0.0;
        foreach (var root in treeRoots)
        {
            Place(root, 0);
            next += TreeGap;
        }

        double Place(string id, int depth)
        {
            column[id] = depth;
            var kids = children[id].OrderBy(child => position[child]).ToList();
            if (kids.Count == 0)
            {
                row[id] = next;
                next += 1;
                return row[id];
            }

            var first = double.NaN;
            var last = 0.0;
            foreach (var kid in kids)
            {
                last = Place(kid, depth + 1);
                if (double.IsNaN(first))
                    first = last;
            }
            row[id] = (first + last) / 2;
            return row[id];
        }

        var placed = new List<PlacedNode>();
        foreach (var layer in layers)
        {
            foreach (var id in layer.OrderBy(id => row[id]))
            {
                placed.Add(new PlacedNode(
                    id, parent[id], column[id], row[id],
                    Margin + column[id] * (NodeWidth + ColumnGap),
                    Margin + row[id] * RowPitch,
                    broken[id]));
            }
        }

        var byId = placed.ToDictionary(node => node.Id);
        var edges = placed
            .Where(node => node.ParentId is not null)
            .Select(node => new PlacedEdge(node.ParentId!, node.Id, EdgePath(byId[node.ParentId!], node)))
            .ToList();

        var standAlone = standAloneIds
            .Select(id => new PlacedNode(id, null, 0, 0, 0, 0, broken[id]))
            .ToList();

        var columns = placed.Count == 0 ? 0 : placed.Max(node => node.Column) + 1;
        var rows = placed.Count == 0 ? 0 : placed.Max(node => node.Row) + 1;
        var width = placed.Count == 0 ? 0 : 2 * Margin + columns * NodeWidth + (columns - 1) * ColumnGap;
        var height = placed.Count == 0 ? 0 : 2 * Margin + rows * NodeHeight + (rows - 1) * RowGap;

        return new DependencyLayoutResult(placed, edges, standAlone, width, height);
    }

    /// <summary>
    /// Parents that exist and do not lead back round to the node. What is left is a forest.
    ///
    /// The editor offers every other connection as a parent, so two connections can be set
    /// to sit behind each other, and deleting a connection leaves its children pointing at
    /// an id that is gone. Neither should take the map down or loop it for ever. A loop is
    /// broken at the member that comes first in the input, which is the same member every
    /// time for the same connections, so the map does not rearrange itself between renders.
    /// </summary>
    private static (Dictionary<string, string?> Parent, Dictionary<string, BrokenLink> Broken) Sanitise(
        List<string> order, Dictionary<string, string?> declared)
    {
        var parent = new Dictionary<string, string?>(order.Count);
        var broken = new Dictionary<string, BrokenLink>(order.Count);

        foreach (var id in order)
        {
            var named = declared[id];
            if (string.IsNullOrEmpty(named))
            {
                parent[id] = null;
                broken[id] = BrokenLink.None;
            }
            else if (named == id || !declared.ContainsKey(named))
            {
                parent[id] = null;
                broken[id] = named == id ? BrokenLink.Cycle : BrokenLink.MissingParent;
            }
            else
            {
                parent[id] = named;
                broken[id] = BrokenLink.None;
            }
        }

        // Each node has one parent, so following parents from anywhere either reaches a
        // root or goes round a loop. Colouring the walk finds each loop exactly once.
        var state = order.ToDictionary(id => id, _ => 0); // 0 unseen, 1 on this walk, 2 done
        var rank = new Dictionary<string, int>(order.Count);
        for (var i = 0; i < order.Count; i++)
            rank[order[i]] = i;

        foreach (var start in order)
        {
            var walk = new List<string>();
            var current = start;
            while (current is not null && state[current] == 0)
            {
                state[current] = 1;
                walk.Add(current);
                current = parent[current];
            }

            if (current is not null && state[current] == 1)
            {
                var loop = walk.SkipWhile(id => id != current).ToList();
                var cut = loop.MinBy(id => rank[id])!;
                parent[cut] = null;
                broken[cut] = BrokenLink.Cycle;
            }

            foreach (var id in walk)
                state[id] = 2;
        }

        return (parent, broken);
    }

    /// <summary>
    /// The layers, each in order. The first keeps the input order; each after it is sorted
    /// by where its parent is in the layer before (ties by input order), one downward
    /// sweep of barycentre ordering. With one parent per node a single sweep is already the
    /// best there is, so there is no point going back up.
    /// </summary>
    private static List<List<string>> Layers(
        List<string> roots, Dictionary<string, List<string>> children, Dictionary<string, int> index)
    {
        var layers = new List<List<string>>();
        var current = roots.OrderBy(id => index[id]).ToList();
        while (current.Count > 0)
        {
            layers.Add(current);
            var position = new Dictionary<string, int>();
            for (var i = 0; i < current.Count; i++)
                position[current[i]] = i;

            current =
            [
                .. current
                    .SelectMany(p => children[p].Select(child => (Child: child, Barycentre: position[p])))
                    .OrderBy(pair => pair.Barycentre)
                    .ThenBy(pair => index[pair.Child])
                    .Select(pair => pair.Child),
            ];
        }
        return layers;
    }

    /// <summary>
    /// An S-curve from the middle of the parent's right edge to the middle of the child's
    /// left. Both control points sit halfway across the gap, so every edge leaves and
    /// arrives horizontally and siblings fan out rather than overlapping near the parent.
    /// </summary>
    private static string EdgePath(PlacedNode from, PlacedNode to)
    {
        var x1 = from.X + NodeWidth;
        var y1 = from.Y + NodeHeight / 2;
        var x2 = to.X;
        var y2 = to.Y + NodeHeight / 2;
        var mid = (x1 + x2) / 2;
        return string.Create(CultureInfo.InvariantCulture,
            $"M{x1:0.#},{y1:0.#} C{mid:0.#},{y1:0.#} {mid:0.#},{y2:0.#} {x2:0.#},{y2:0.#}");
    }

    /// <summary>
    /// How many pairs of edges cross between adjacent columns. Two edges between the same
    /// columns cross when their parents and children are in opposite orders.
    /// </summary>
    public static int Crossings(DependencyLayoutResult layout)
    {
        var byId = layout.ById;
        var spans = layout.Edges
            .Select(edge => (Column: byId[edge.ParentId].Column, From: byId[edge.ParentId].Row, To: byId[edge.ChildId].Row))
            .ToList();

        var crossings = 0;
        for (var i = 0; i < spans.Count; i++)
            for (var j = i + 1; j < spans.Count; j++)
                if (spans[i].Column == spans[j].Column
                    && (spans[i].From - spans[j].From) * (spans[i].To - spans[j].To) < 0)
                    crossings++;
        return crossings;
    }

    /// <summary>
    /// The ids that sit behind <paramref name="rootId"/>, directly or further down, with the
    /// root itself first. Worked out on the sanitised parents, so a loop cannot make it run
    /// for ever and a node whose parent is missing is not counted as behind anything.
    /// </summary>
    public static IReadOnlyList<string> Subtree(IEnumerable<DependencyInput> input, string rootId)
    {
        var layout = Layout(input);
        if (!layout.ById.ContainsKey(rootId))
            return [];

        var children = layout.Nodes
            .Where(node => node.ParentId is not null)
            .GroupBy(node => node.ParentId!)
            .ToDictionary(group => group.Key, group => group.Select(node => node.Id).ToList());

        var result = new List<string>();
        var queue = new Queue<string>([rootId]);
        while (queue.TryDequeue(out var id))
        {
            result.Add(id);
            if (children.TryGetValue(id, out var kids))
                foreach (var kid in kids)
                    queue.Enqueue(kid);
        }
        return result;
    }

    /// <summary>
    /// For every node with something down above it, the furthest-up connection that is down:
    /// the one to go and fix. The router rather than the NAS behind it, when both are down,
    /// because the NAS being down is then most likely the router's doing too.
    ///
    /// <paramref name="parents"/> is the sanitised parent of each id, as a layout gives it,
    /// so there is no loop to guard against here.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Affected(
        IReadOnlyDictionary<string, string?> parents, Func<string, bool> isDown)
    {
        var cause = new Dictionary<string, string>();
        foreach (var id in parents.Keys)
        {
            string? topmost = null;
            var seen = new HashSet<string> { id };
            var current = parents[id];
            // The seen set is belt and braces: parents from a layout have no loops, but this
            // is public and a hand-built map could.
            while (current is not null && seen.Add(current))
            {
                if (isDown(current))
                    topmost = current;
                current = parents.GetValueOrDefault(current);
            }

            if (topmost is not null)
                cause[id] = topmost;
        }
        return cause;
    }
}
