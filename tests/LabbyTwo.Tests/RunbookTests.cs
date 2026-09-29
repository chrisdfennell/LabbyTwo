using LabbyTwo.Core;
using LabbyTwo.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// The text and the rules behind runbooks: cutting Markdown at {{if}} / {{else}} / {{end}},
/// reading conditions, deciding them against the monitor's verdicts, what {{down}} lists
/// and which action a {{button}} means. Components are tested in RunbookRenderTests.
/// </summary>
public sealed class RunbookTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly Markdown _markdown = new();

    public RunbookTests() => _services = TestHost.Build(_directory);

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private Registry Registry => _services.GetRequiredService<Registry>();

    private static IReadOnlyList<RunbookPart> Parse(string text) => Runbook.Parse(text, []);

    // ---------- the grammar ----------

    [Fact]
    public void BareWordsAndIfAreShortcodesButGoTemplatesAreNot()
    {
        Assert.Equal("down", Shortcodes.Parse("{{down}}")!.Kind);
        Assert.Equal("else", Shortcodes.Parse("{{ else }}")!.Kind);
        Assert.Equal("end", Shortcodes.Parse("{{END}}")!.Kind);

        var branch = Shortcodes.Parse("{{if metric: NAS / disk_percent > 90}}")!;
        Assert.Equal("if", branch.Kind);
        Assert.Equal(["metric: NAS / disk_percent > 90"], branch.Target);
        Assert.True(branch.IsStructure);

        // Go's own {{if}}, {{ .Name }} and a Handlebars block stay text.
        Assert.Null(Shortcodes.Parse("{{if .Ready}}"));
        Assert.Null(Shortcodes.Parse("{{ .Name }}"));
        Assert.Null(Shortcodes.Parse("{{#if ready}}"));
        Assert.Null(Shortcodes.Parse("{{up}}"));
    }

    [Fact]
    public void DownIsWrittenWithoutAColon()
    {
        Assert.Equal("{{down}}", Shortcodes.Write("down", []));
        Assert.Equal("{{down: only=\"NAS\"}}", Shortcodes.Write("down", [], [new("only", "NAS")]));
    }

    // ---------- cutting into sections ----------

    [Fact]
    public void AnIfWithElseBecomesTwoBranches()
    {
        var parts = Parse("Before.\n\n{{if down: NAS}}\n### Down\nFix it.\n{{else}}\nFine.\n{{end}}\n\nAfter.\n");

        Assert.Equal(3, parts.Count);
        Assert.Equal("Before.\n\n", Assert.IsType<RunbookText>(parts[0]).Markdown);
        var section = Assert.IsType<RunbookIf>(parts[1]);
        Assert.Equal(new RunbookCondition(RunbookTest.Down, "NAS"), section.Condition);
        Assert.Equal("### Down\nFix it.\n", Assert.IsType<RunbookText>(Assert.Single(section.Then)).Markdown);
        Assert.Equal("Fine.\n", Assert.IsType<RunbookText>(Assert.Single(section.Else)).Markdown);
        Assert.Equal("\nAfter.\n", Assert.IsType<RunbookText>(parts[2]).Markdown);
    }

    [Fact]
    public void SectionsNest()
    {
        var parts = Parse("{{if any down}}\nA\n{{if down: NAS}}\nB\n{{end}}\nC\n{{end}}\n");

        var outer = Assert.IsType<RunbookIf>(Assert.Single(parts));
        Assert.Equal(RunbookTest.AnyDown, outer.Condition!.Test);
        Assert.Equal(3, outer.Then.Count);
        var inner = Assert.IsType<RunbookIf>(outer.Then[1]);
        Assert.Equal("B\n", Assert.IsType<RunbookText>(Assert.Single(inner.Then)).Markdown);
    }

    [Fact]
    public void NestingTooDeepIsANoteNotARecursion()
    {
        var text = string.Concat(Enumerable.Repeat("{{if all up}}\n", Runbook.MaxDepth + 1)) + "deep\n"
            + string.Concat(Enumerable.Repeat("{{end}}\n", Runbook.MaxDepth + 1));

        var section = Assert.IsType<RunbookIf>(Assert.Single(Parse(text)));
        for (var depth = 1; depth < Runbook.MaxDepth; depth++)
            section = Assert.IsType<RunbookIf>(Assert.Single(section.Then));

        var tooDeep = Assert.IsType<RunbookIf>(Assert.Single(section.Then));
        Assert.Null(tooDeep.Condition);
        Assert.Contains($"more than {Runbook.MaxDepth} deep", tooDeep.Problem);
    }

    [Fact]
    public void AStrayEndOrElseIsANoteWhereItStands()
    {
        var parts = Parse("One\n{{end}}\nTwo\n{{else}}\nThree\n");

        Assert.Collection(parts,
            p => Assert.Equal("One\n", Assert.IsType<RunbookText>(p).Markdown),
            p => Assert.Contains("{{end}} on line 2 has no {{if …}}", Assert.IsType<RunbookProblem>(p).Message),
            p => Assert.Equal("Two\n", Assert.IsType<RunbookText>(p).Markdown),
            p => Assert.Contains("{{else}} on line 4", Assert.IsType<RunbookProblem>(p).Message),
            p => Assert.Equal("Three\n", Assert.IsType<RunbookText>(p).Markdown));
    }

    [Fact]
    public void AnIfNeverClosedShowsEverythingAfterItWithANote()
    {
        var parts = Parse("Top\n{{if down: NAS}}\nIn the section\n{{else}}\nOtherwise\n");

        Assert.Collection(parts,
            p => Assert.IsType<RunbookText>(p),
            p => Assert.Contains("never closed", Assert.IsType<RunbookProblem>(p).Message),
            p => Assert.Equal("In the section\n", Assert.IsType<RunbookText>(p).Markdown),
            p => Assert.Equal("Otherwise\n", Assert.IsType<RunbookText>(p).Markdown));
    }

    [Fact]
    public void ASecondElseIsANoteOnTheSection()
    {
        var section = Assert.IsType<RunbookIf>(Assert.Single(Parse("{{if all up}}\na\n{{else}}\nb\n{{else}}\nc\n{{end}}")));

        Assert.Contains("second {{else}}", Assert.Single(section.Notes));
        Assert.Equal(2, section.Else.Count);
    }

    [Theory]
    [InlineData("\\{{if down: NAS}}\ntext\n\\{{end}}\n")]         // escaped
    [InlineData("    {{if down: NAS}}\n    text\n    {{end}}\n")]  // indented into code
    [InlineData("- {{if down: NAS}}\n- text\n- {{end}}\n")]        // inside a list item
    [InlineData("Text {{if down: NAS}} more\n")]                    // inside a sentence
    public void OnlyALineOfItsOwnIsASectionLine(string text)
    {
        Assert.All(Parse(text), p => Assert.IsType<RunbookText>(p));
    }

    [Fact]
    public void SectionLinesInCodeAreExamples()
    {
        var page = _markdown.PreparePage("Write it like this:\n\n```\n{{if down: NAS}}\nsteps\n{{end}}\n```\n\nand `{{end}}` closes it.\n");

        var section = Assert.IsType<LiveSection>(Assert.Single(page.Parts));
        Assert.False(page.HasSections);
        Assert.Contains("{{if down: NAS}}", section.Document.Html);
        Assert.Contains("<code>{{end}}</code>", section.Document.Html);
    }

    [Fact]
    public void AStructureWordInsideASentenceIsLeftForTheValueToComplainAbout()
    {
        var page = _markdown.PreparePage("See {{end}} here.");

        var section = Assert.IsType<LiveSection>(Assert.Single(page.Parts));
        Assert.True(Assert.Single(section.Document.Shortcodes).IsStructure);
    }

    [Fact]
    public void APageWithoutSectionsIsOnePieceExactlyAsBefore()
    {
        const string text = "# Title\n\nNAS is {{status: NAS}}.\n\n- a\n- b\n";

        var page = _markdown.PreparePage(text);

        var section = Assert.IsType<LiveSection>(Assert.Single(page.Parts));
        Assert.Equal(_markdown.Prepare(text).Html, section.Document.Html);
    }

    [Fact]
    public void HeadingsInSectionsKeepTheirAnchorsAndStayUnique()
    {
        var page = _markdown.PreparePage("## Steps\n\n{{if down: NAS}}\n## Steps\n{{else}}\n## Steps\n{{end}}\n");

        var html = string.Concat(Pieces(page.Parts).Select(p => p.Document.Html));
        Assert.Contains("<h2 id=\"steps\">", html);
        Assert.Contains("<h2 id=\"steps-1\">", html);
        Assert.Contains("<h2 id=\"steps-2\">", html);
    }

    [Fact]
    public void AListCutByASectionIsTwoWholeLists()
    {
        // Conditions wrap whole blocks; one put through the middle of a list ends it there
        // and starts another after, rather than leaving half a list for the browser to mend.
        var page = _markdown.PreparePage("- one\n- two\n{{if all up}}\n- three\n{{end}}\n- four\n");

        var pieces = Pieces(page.Parts).Select(p => p.Document.Html).ToList();
        Assert.Equal(3, pieces.Count);
        Assert.All(pieces, html =>
        {
            Assert.StartsWith("<ul>", html);
            Assert.EndsWith("</ul>\n", html);
        });
    }

    [Fact]
    public void ScriptInAConditionOrABranchNeverReachesTheHtml()
    {
        var page = _markdown.PreparePage("{{if down: \"<script>alert(1)</script>\"}}\n<script>alert(2)</script>\n<img src=x onerror=alert(3)>\n{{end}}\n");

        var section = Assert.IsType<LiveIf>(Assert.Single(page.Parts));
        Assert.Equal("<script>alert(1)</script>", section.Section.Condition!.Connection);
        var html = Assert.IsType<LiveSection>(Assert.Single(section.Then)).Document.Html;
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<LiveSection> Pieces(IEnumerable<LivePart> parts) => parts.SelectMany(part => part switch
    {
        LiveSection section => [section],
        LiveIf branch => Pieces(branch.Then.Concat(branch.Else)),
        _ => Enumerable.Empty<LiveSection>(),
    });

    // ---------- reading conditions ----------

    [Theory]
    [InlineData("down: NAS", RunbookTest.Down, "NAS")]
    [InlineData("up: Home Assistant", RunbookTest.Up, "Home Assistant")]
    [InlineData("down: \"Router / WAN\"", RunbookTest.Down, "Router / WAN")]
    [InlineData("any  DOWN", RunbookTest.AnyDown, "")]
    [InlineData("All up", RunbookTest.AllUp, "")]
    public void ConditionsAreRead(string text, RunbookTest test, string connection)
    {
        var condition = Runbook.ParseCondition(text, out var problem);

        Assert.Null(problem);
        Assert.Equal(test, condition!.Test);
        Assert.Equal(connection, condition.Connection);
    }

    [Theory]
    [InlineData("metric: NAS / disk_percent > 90", "NAS", "disk_percent", ">", 90, "")]
    [InlineData("metric: NAS / Disk used >= 90.5%", "NAS", "Disk used", ">=", 90.5, "%")]
    [InlineData("metric: Router / latency_ms<=-3 ms", "Router", "latency_ms", "<=", -3, "ms")]
    [InlineData("metric: UPS / on_battery == 1", "UPS", "on_battery", "==", 1, "")]
    [InlineData("metric: UPS / load != .5", "UPS", "load", "!=", 0.5, "")]
    [InlineData("metric: Home Assistant / cpu < 10", "Home Assistant", "cpu", "<", 10, "")]
    public void MetricComparisonsAreRead(string text, string connection, string metric, string op, double value, string unit)
    {
        var condition = Runbook.ParseCondition(text, out var problem);

        Assert.Null(problem);
        Assert.Equal(new RunbookCondition(RunbookTest.Metric, connection, metric, op, value, unit), condition);
    }

    [Theory]
    [InlineData("sideways: NAS", "is not a condition")]
    [InlineData("down:", "Say which connection")]
    [InlineData("down: NAS / Router", "One connection")]
    [InlineData("metric: NAS / cpu", "Compare the metric with a number")]
    [InlineData("metric: NAS > 3", "Name a connection and a metric")]
    public void ABadConditionSaysWhy(string text, string reason)
    {
        Assert.Null(Runbook.ParseCondition(text, out var problem));
        Assert.Contains(reason, problem);
    }

    [Theory]
    [InlineData(">", 90, false)]
    [InlineData(">", 91, true)]
    [InlineData(">=", 90, true)]
    [InlineData("<", 90, false)]
    [InlineData("<=", 90, true)]
    [InlineData("==", 90.0000000001, true)]
    [InlineData("!=", 90, false)]
    public void OperatorsCompareTheReadingWithTheNumber(string op, double reading, bool holds)
    {
        Assert.Equal(holds, new RunbookCondition(RunbookTest.Metric, "NAS", "x", op, 90).Compare(reading));
    }

    // ---------- deciding them ----------

    private static readonly Connection Nas = new() { Id = "nas", Provider = "http", Name = "NAS" };
    private static readonly Connection Plex = new() { Id = "plex", Provider = "http", Name = "Plex" };
    private static readonly Connection Paused = new() { Id = "paused", Provider = "http", Name = "Old box", Enabled = false };
    private static readonly Connection Webhook = new() { Id = "hook", Provider = "webhook", Name = "Webhook" };

    private static HealthMonitor.ProbeState State(string id, bool? up, string message = "", DateTimeOffset? changed = null,
        Dictionary<string, double>? metrics = null) =>
        new(id, up, message, TimeSpan.Zero, DateTimeOffset.Now, changed ?? DateTimeOffset.Now, up == false ? 3 : 0,
            metrics ?? [], new Dictionary<string, string>());

    private RunbookFacts Facts(Dictionary<string, HealthMonitor.ProbeState> states, Dictionary<string, double>? stored = null)
    {
        var registry = Registry;
        return new RunbookFacts(
            registry,
            c => c.Enabled && registry.Provider(c.Provider)?.IsMonitored != false,
            id => states.GetValueOrDefault(id),
            _ => stored ?? []);
    }

    private static RunbookCondition Condition(string text) => Runbook.ParseCondition(text, out _)!;

    [Fact]
    public void DownAndUpFollowTheMonitorsVerdict()
    {
        var facts = Facts(new() { ["nas"] = State("nas", false), ["plex"] = State("plex", true) });
        Connection[] all = [Nas, Plex];

        Assert.True(facts.Evaluate(Condition("down: nas"), all).Holds);
        Assert.False(facts.Evaluate(Condition("up: NAS"), all).Holds);
        Assert.True(facts.Evaluate(Condition("up: Plex"), all).Holds);
        Assert.False(facts.Evaluate(Condition("down: Plex"), all).Holds);
    }

    [Fact]
    public void ACheckingConnectionIsNeitherUpNorDown()
    {
        var facts = Facts([]);
        Connection[] all = [Nas];

        Assert.False(facts.Evaluate(Condition("down: NAS"), all).Holds);
        Assert.False(facts.Evaluate(Condition("up: NAS"), all).Holds);
        Assert.False(facts.Evaluate(Condition("any down"), all).Holds);
        Assert.False(facts.Evaluate(Condition("all up"), all).Holds);
    }

    [Fact]
    public void WholeLabConditionsCountOnlyWhatIsMonitored()
    {
        // The paused box and the webhook were last seen down; neither is a service now.
        var facts = Facts(new()
        {
            ["nas"] = State("nas", true),
            ["plex"] = State("plex", true),
            ["paused"] = State("paused", false),
            ["hook"] = State("hook", false),
        });
        Connection[] all = [Nas, Plex, Paused, Webhook];

        Assert.True(facts.Evaluate(Condition("all up"), all).Holds);
        Assert.False(facts.Evaluate(Condition("any down"), all).Holds);
        Assert.False(facts.Evaluate(Condition("down: Old box"), all).Holds);
    }

    [Fact]
    public void AnUnknownConnectionIsAProblemAndHoldsNothing()
    {
        var result = Facts([]).Evaluate(Condition("down: Fridge"), [Nas]);

        Assert.False(result.Holds);
        Assert.Contains("No connection called “Fridge”", result.Problem);
    }

    [Fact]
    public void AMetricIsComparedInItsStoredUnitLiveFirst()
    {
        var facts = Facts(
            new() { ["nas"] = State("nas", true, metrics: new() { ["latency_ms"] = 250 }) },
            stored: new() { ["latency_ms"] = 10 });
        Connection[] all = [Nas];

        Assert.True(facts.Evaluate(Condition("metric: NAS / latency_ms > 200"), all).Holds);
        Assert.True(facts.Evaluate(Condition("metric: NAS / Response time > 200 ms"), all).Holds);
        Assert.False(facts.Evaluate(Condition("metric: NAS / latency_ms < 200"), all).Holds);

        var wrongUnit = facts.Evaluate(Condition("metric: NAS / latency_ms > 0.2 s"), all);
        Assert.False(wrongUnit.Holds);
        Assert.Contains("its own unit, ms", wrongUnit.Problem);
    }

    [Fact]
    public void AStoredReadingIsUsedWhenThereIsNoLiveOne()
    {
        var facts = Facts([], stored: new() { ["disk_percent"] = 93 });

        Assert.True(facts.Evaluate(Condition("metric: NAS / disk_percent >= 90"), [Nas]).Holds);
    }

    [Fact]
    public void AMetricWithNoReadingIsFalseAndAnUnknownOneIsAProblem()
    {
        var facts = Facts([]);

        var noReading = facts.Evaluate(Condition("metric: NAS / latency_ms > 1"), [Nas]);
        Assert.False(noReading.Holds);
        Assert.Null(noReading.Problem);

        var unknown = facts.Evaluate(Condition("metric: NAS / warp_factor > 1"), [Nas]);
        Assert.Contains("has not reported a metric called “warp_factor”", unknown.Problem);
    }

    // ---------- {{down}} ----------

    private static Shortcode Down(string text = "{{down}}") => Shortcodes.Parse(text)!;

    [Fact]
    public void DownListsWhatIsDownLongestFirst()
    {
        var now = DateTimeOffset.Now;
        var router = new Connection { Id = "router", Provider = "http", Name = "Router" };
        var facts = Facts(new()
        {
            ["nas"] = State("nas", false, "Connection refused", now.AddMinutes(-5)),
            ["router"] = State("router", false, "Timed out", now.AddHours(-1)),
            ["plex"] = State("plex", true),
        });

        var list = facts.Down(Down(), [Nas, Plex, router], now);

        Assert.Null(list.Problem);
        Assert.Equal(["Router", "NAS"], list.Entries.Select(e => e.Connection.Name));
        Assert.Equal("Timed out", list.Entries[0].State!.Message);
    }

    [Fact]
    public void DownLeavesOutPausedAndUnmonitoredAndCheckingUnlessAsked()
    {
        var facts = Facts(new()
        {
            ["paused"] = State("paused", false),
            ["hook"] = State("hook", false),
        });
        Connection[] all = [Nas, Paused, Webhook];

        Assert.Empty(facts.Down(Down(), all, DateTimeOffset.Now).Entries);

        var withChecking = facts.Down(Down("{{down: include=\"checking\"}}"), all, DateTimeOffset.Now);
        Assert.Equal("NAS", Assert.Single(withChecking.Entries).Connection.Name);
        Assert.Null(withChecking.Entries[0].State);
    }

    [Fact]
    public void ASilencedConnectionIsListedAndMarkedUnlessHidden()
    {
        // The dashboard counts it as down, so the runbook does too — marked, because it is
        // usually the NAS somebody just pressed Restart on.
        var now = DateTimeOffset.Now;
        var silenced = Nas with { SilencedUntil = now.AddMinutes(10) };
        var facts = Facts(new() { ["nas"] = State("nas", false) });

        var shown = Assert.Single(facts.Down(Down(), [silenced], now).Entries);
        Assert.Equal(silenced.SilencedUntil, shown.Silenced);

        Assert.Empty(facts.Down(Down("{{down: silenced=hide}}"), [silenced], now).Entries);

        // A silence that has run out is no silence.
        var lapsed = Nas with { SilencedUntil = now.AddMinutes(-1) };
        Assert.Null(Assert.Single(facts.Down(Down(), [lapsed], now).Entries).Silenced);
    }

    [Fact]
    public void OnlyNarrowsTheListAndNamesWhatItCouldNotFind()
    {
        var facts = Facts(new() { ["nas"] = State("nas", false), ["plex"] = State("plex", false) });

        var list = facts.Down(Down("{{down: only=\"plex, Fridge\"}}"), [Nas, Plex], DateTimeOffset.Now);

        Assert.Equal("Plex", Assert.Single(list.Entries).Connection.Name);
        Assert.Contains("“Fridge”", list.Problem);
    }

    // ---------- {{button}} ----------

    private static readonly IReadOnlyList<ProviderAction> Actions =
    [
        new("restart", "Restart") { Dangerous = true },
        new("shutdown", "Shut down") { Dangerous = true },
    ];

    [Theory]
    [InlineData("restart", "restart")]
    [InlineData("RESTART", "restart")]
    [InlineData("shut down", "shutdown")]
    [InlineData(" Shut Down ", "shutdown")]
    public void AnActionIsFoundByKeyOrLabel(string written, string id)
    {
        Assert.Equal(id, ShortcodeLookup.Action(Actions, written)!.Id);
    }

    [Fact]
    public void AnActionNotOfferedIsNotFound()
    {
        Assert.Null(ShortcodeLookup.Action(Actions, "wake"));
        Assert.Null(ShortcodeLookup.Action(Actions, ""));
    }
}
