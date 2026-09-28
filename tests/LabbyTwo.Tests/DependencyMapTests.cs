using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using LabbyTwo.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// The dependency map: where the layout puts things, what it does with the "Sits behind"
/// settings that cannot be drawn as a tree, which connections an outage marks as affected,
/// and that the drawing says all of it in words as well as colours.
/// </summary>
public class DependencyMapTests
{
    private static DependencyInput N(string id, string? parent = null) => new(id, parent);

    // ---------- layout ----------

    [Fact]
    public void RootsAreOnTheLeftAndEachStepBehindIsOneColumnFurther()
    {
        var layout = DependencyLayout.Layout(
        [
            N("router"), N("nas", "router"), N("plex", "nas"), N("sonarr", "nas"), N("pihole", "router"),
        ]);

        var column = layout.Nodes.ToDictionary(n => n.Id, n => n.Column);
        Assert.Equal(0, column["router"]);
        Assert.Equal(1, column["nas"]);
        Assert.Equal(1, column["pihole"]);
        Assert.Equal(2, column["plex"]);
        Assert.Equal(2, column["sonarr"]);

        // Columns go left to right, and a parent sits level with the middle of its children.
        var byId = layout.ById;
        Assert.True(byId["router"].X < byId["nas"].X && byId["nas"].X < byId["plex"].X);
        Assert.Equal((byId["plex"].Row + byId["sonarr"].Row) / 2, byId["nas"].Row);
        Assert.Equal(4, layout.Edges.Count);
        Assert.Empty(layout.StandAlone);
    }

    [Fact]
    public void TheSameInputAlwaysGivesTheSameLayout()
    {
        DependencyInput[] input =
        [
            N("a"), N("b", "a"), N("c", "a"), N("d", "b"), N("e", "x"), N("f", "g"), N("g", "f"), N("h"),
        ];

        var first = DependencyLayout.Layout(input);
        var second = DependencyLayout.Layout(input.ToArray());

        Assert.Equal(first.Nodes, second.Nodes);
        Assert.Equal(first.Edges, second.Edges);
        Assert.Equal(first.StandAlone, second.StandAlone);
        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
    }

    [Fact]
    public void ALoopIsBrokenAtTheFirstMemberAndStillDrawn()
    {
        // The editor lets two connections sit behind each other; a longer loop is as easy.
        var layout = DependencyLayout.Layout([N("a", "c"), N("b", "a"), N("c", "b"), N("self", "self")]);

        var byId = layout.ById;
        Assert.Equal(4, byId.Count);
        Assert.Null(byId["a"].ParentId);
        Assert.Equal(BrokenLink.Cycle, byId["a"].Broken);
        Assert.Equal("a", byId["b"].ParentId);
        Assert.Equal("b", byId["c"].ParentId);
        Assert.Equal(2, byId["c"].Column);

        Assert.Null(byId["self"].ParentId);
        Assert.Equal(BrokenLink.Cycle, byId["self"].Broken);
    }

    [Fact]
    public void AParentThatWasDeletedMakesARoot()
    {
        var layout = DependencyLayout.Layout([N("plex", "gone"), N("sonarr", "plex"), N("lonely", "also-gone")]);

        var byId = layout.ById;
        Assert.Null(byId["plex"].ParentId);
        Assert.Equal(BrokenLink.MissingParent, byId["plex"].Broken);
        Assert.Equal(0, byId["plex"].Column);
        Assert.Equal("plex", byId["sonarr"].ParentId);

        // With nothing behind it either, it joins the stand-alone ones, still flagged.
        var lonely = Assert.Single(layout.StandAlone);
        Assert.Equal("lonely", lonely.Id);
        Assert.Equal(BrokenLink.MissingParent, lonely.Broken);
    }

    [Fact]
    public void ConnectionsWithNoLinksAreGroupedAsStandAlone()
    {
        var layout = DependencyLayout.Layout([N("clock"), N("router"), N("nas", "router"), N("weather")]);

        Assert.Equal(["clock", "weather"], layout.StandAlone.Select(n => n.Id));
        Assert.Equal(["router", "nas"], layout.Nodes.Select(n => n.Id));
    }

    [Fact]
    public void NothingAtAllIsAnEmptyLayout()
    {
        var layout = DependencyLayout.Layout([]);

        Assert.Empty(layout.Nodes);
        Assert.Equal(0, layout.Width);
    }

    [Fact]
    public void ChildrenListedOutOfOrderDoNotCross()
    {
        // Listed so that taking each column in input order would put B's child above A's
        // while A is above B: the two edges cross. Ordering by parent position undoes it.
        DependencyInput[] input = [N("A"), N("B"), N("b1", "B"), N("a1", "A"), N("b2", "B"), N("a2", "A")];

        var layout = DependencyLayout.Layout(input);

        Assert.Equal(0, DependencyLayout.Crossings(layout));
        var byId = layout.ById;
        Assert.True(byId["a1"].Row < byId["b1"].Row);
        Assert.True(byId["a2"].Row < byId["b1"].Row);

        // And the naive order really would have crossed, or this proves nothing.
        var naive = new DependencyLayoutResult(
            [.. input.Select((n, i) => new PlacedNode(n.Id, n.ParentId, n.ParentId is null ? 0 : 1, i, 0, 0, BrokenLink.None))],
            layout.Edges, [], 0, 0);
        Assert.True(DependencyLayout.Crossings(naive) > 0);
    }

    [Fact]
    public void TwoHundredConnectionsLayOutWithoutCrossings()
    {
        // A router, nineteen hosts behind it, and nine services per host listed in a
        // scrambled order — plus a few that are stand-alone.
        var input = new List<DependencyInput> { N("router") };
        for (var h = 0; h < 19; h++)
        {
            input.Add(N($"host{h}", "router"));
            for (var s = 0; s < 9; s++)
                input.Add(N($"svc{h}-{s}", $"host{(h * 7 + s) % 19}"));
        }
        for (var i = 0; i < 9; i++)
            input.Add(N($"alone{i}"));

        var layout = DependencyLayout.Layout(input);

        Assert.Equal(200, layout.Nodes.Count + layout.StandAlone.Count);
        Assert.Equal(9, layout.StandAlone.Count);
        Assert.Equal(0, DependencyLayout.Crossings(layout));
        // No two boxes in the same column share a row.
        Assert.All(layout.Nodes.GroupBy(n => n.Column), column =>
            Assert.Equal(column.Count(), column.Select(n => n.Row).Distinct().Count()));
    }

    [Fact]
    public void ASubtreeIsTheRootAndEverythingBehindIt()
    {
        DependencyInput[] input = [N("router"), N("nas", "router"), N("plex", "nas"), N("pihole", "router"), N("x", "y"), N("y", "x")];

        Assert.Equal(["nas", "plex"], DependencyLayout.Subtree(input, "nas"));
        Assert.Equal(4, DependencyLayout.Subtree(input, "router").Count);
        Assert.Empty(DependencyLayout.Subtree(input, "nope"));
        // A loop does not make it go round for ever.
        Assert.Equal(2, DependencyLayout.Subtree(input, "x").Count);
    }

    // ---------- affected ----------

    [Fact]
    public void EverythingBehindADownParentIsAffectedAndBlamesTheTopmostOutage()
    {
        var layout = DependencyLayout.Layout(
        [
            N("router"), N("nas", "router"), N("plex", "nas"), N("sonarr", "nas"), N("pihole", "router"), N("other"), N("x", "other"),
        ]);
        HashSet<string> down = ["nas", "plex"];

        var affected = DependencyLayout.Affected(layout.Parents, down.Contains);

        Assert.Equal("nas", affected["plex"]);
        Assert.Equal("nas", affected["sonarr"]);
        Assert.False(affected.ContainsKey("nas"));      // the cause, not a casualty
        Assert.False(affected.ContainsKey("pihole"));   // a sibling of the outage, not behind it
        Assert.False(affected.ContainsKey("x"));

        // With the router down too, the router is the one to go and look at.
        down.Add("router");
        affected = DependencyLayout.Affected(layout.Parents, down.Contains);
        Assert.Equal("router", affected["nas"]);
        Assert.Equal("router", affected["plex"]);
        Assert.Equal("router", affected["pihole"]);
    }

    [Fact]
    public void AffectedStopsAtALoopInAHandBuiltMap()
    {
        var parents = new Dictionary<string, string?> { ["a"] = "b", ["b"] = "a" };

        var affected = DependencyLayout.Affected(parents, id => id == "a");

        Assert.Equal("a", affected["b"]);
    }

    [Fact]
    public void NodesComeFromConnectionsAndTheMonitorsMemory()
    {
        var now = DateTimeOffset.Now;
        Connection[] connections =
        [
            new() { Id = "nas", Name = "NAS", Icon = "🗄️" },
            new() { Id = "plex", Name = "Plex", DependsOn = "nas", SilencedUntil = now.AddHours(1) },
            new() { Id = "new", Name = "New" },
            new() { Id = "off", Name = "Off", Enabled = false },
            new() { Id = "hook", Name = "Webhook" },
        ];
        var states = new Dictionary<string, HealthMonitor.ProbeState>
        {
            ["nas"] = State("nas", false, "timed out"),
            ["plex"] = State("plex", true, "200"),
        };

        var nodes = DependencyMapModel.From(connections,
            id => states.GetValueOrDefault(id), c => c.Enabled, c => c.Id == "hook", now);

        Assert.Equal(["nas", "plex", "new", "off"], nodes.Select(n => n.Id));
        Assert.Equal(MapStatus.Down, nodes[0].Status);
        Assert.Equal("timed out", nodes[0].Detail);
        Assert.Equal(MapStatus.Up, nodes[1].Status);
        Assert.True(nodes[1].Silenced);
        Assert.Equal("nas", nodes[1].ParentId);
        Assert.Equal("🔌", nodes[2].Icon);
        Assert.Equal(MapStatus.Checking, nodes[2].Status);
        Assert.Equal(MapStatus.Disabled, nodes[3].Status);
    }

    private static HealthMonitor.ProbeState State(string id, bool up, string message) =>
        new(id, up, message, TimeSpan.FromMilliseconds(5), DateTimeOffset.Now, null, up ? 0 : 3,
            new Dictionary<string, double>(), new Dictionary<string, string>());

    // ---------- drawing ----------

    private static async Task<string> RenderAsync(IReadOnlyList<MapNode> nodes, string? root = null)
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(DependencyMapView.Nodes)] = nodes,
                [nameof(DependencyMapView.RootId)] = root,
            });
            var output = await renderer.RenderComponentAsync<DependencyMapView>(parameters);
            // Decoded, because the renderer escapes every glyph and the tests read words.
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    private static readonly MapNode[] Lab =
    [
        new("router", "Router", "📡", null, MapStatus.Up),
        new("nas", "NAS", "🗄️", "router", MapStatus.Down),
        new("plex", "Plex", "🎬", "nas", MapStatus.Down),
        new("sonarr", "Sonarr", "📺", "nas", MapStatus.Up),
        new("clock", "Clock", "🕐", null, MapStatus.Checking),
    ];

    [Fact]
    public async Task ADownParentMarksWhatIsBehindItInWords()
    {
        var html = await RenderAsync(Lab);

        Assert.Contains("is-affected", html);
        Assert.Contains("down because NAS is down", html);
        Assert.Contains("up, but NAS is down", html);
        // Every status is a word, not only a colour.
        Assert.Contains("▲ up", html);
        Assert.Contains("▼ down", html);
        Assert.Contains("checking", html);
        // Boxes link to the connection's editor.
        Assert.Contains("settings/connections?edit=plex", html);
        // One edge per link, the ones out of the outage drawn as cut.
        Assert.Equal(3, Count(html, "depmap-edge"));
        Assert.Equal(2, Count(html, "depmap-edge is-cut"));
    }

    [Fact]
    public async Task TheListSaysTheSameThingForScreenReaders()
    {
        var html = await RenderAsync(Lab);

        Assert.Contains("aria-hidden=\"true\"", html);
        Assert.Contains("What sits behind what", html);
        Assert.Contains("Stand-alone connections", html);
        Assert.Contains("2 behind it", html);
    }

    [Fact]
    public async Task ARootFilterDrawsOnlyWhatIsBehindIt()
    {
        var html = await RenderAsync(Lab, root: "nas");

        Assert.Contains("Plex", html);
        Assert.DoesNotContain("Router", html);
        Assert.DoesNotContain("Clock", html);
        // The NAS's own parent is outside the drawing, not deleted.
        Assert.DoesNotContain("parent was deleted", html);
        Assert.Contains("down because NAS is down", html);

        var gone = await RenderAsync(Lab, root: "deleted");
        Assert.Contains("no longer exists", gone);
    }

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
