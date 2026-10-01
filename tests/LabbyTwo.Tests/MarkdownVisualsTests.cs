using LabbyTwo.Core;
using LabbyTwo.Services;

namespace LabbyTwo.Tests;

/// <summary>
/// {{table}}, {{gauge}} and diagrams as text: what each reads out of what was written, the
/// lines a reading is coloured against, and where a diagram's boxes go — all without a
/// component, so every odd thing somebody can type is pinned here rather than in a runbook.
/// </summary>
public sealed class MarkdownVisualsTests
{
    private static Shortcode Code(string text) => Shortcodes.Parse(text) ?? throw new InvalidOperationException(text);

    // ---------- {{table}} parsing ----------

    [Fact]
    public void ATableReadsRowsBeforeTheSlashAndColumnsAfterIt()
    {
        var spec = TableShortcode.Read(Code("""{{table: "QNAP NAS", "Pi", PC / cpu_percent, ram_percent, temp_c}}"""), out var problem);

        Assert.Null(problem);
        Assert.Equal(["QNAP NAS", "Pi", "PC"], spec!.Connections);
        Assert.Equal(["cpu_percent", "ram_percent", "temp_c"], spec.Columns);
        Assert.Null(spec.Tab);
        Assert.Null(spec.Sparkline);
        Assert.Null(spec.Title);
    }

    [Fact]
    public void ATableTakesStatusASparklineWindowAndATitle()
    {
        var spec = TableShortcode.Read(Code("""{{table: Home Assistant, "Home, office" / status, Disk used sparkline=7d title="The lab"}}"""), out _);

        Assert.Equal(["Home Assistant", "Home, office"], spec!.Connections);
        Assert.Equal(["status", "Disk used"], spec.Columns);
        Assert.Equal(TimeSpan.FromDays(7), spec.Sparkline);
        Assert.Equal("The lab", spec.Title);
        Assert.True(TableShortcode.IsSpecial("Status"));
        Assert.False(TableShortcode.IsSpecial("cpu_percent"));
    }

    [Theory]
    [InlineData("""{{table: tab "Media" / status, uptime}}""")]
    [InlineData("""{{table: tab Media / status, uptime}}""")]
    [InlineData("""{{table: tab="Media" / status, uptime}}""")]
    public void ATableCanTakeItsRowsFromATab(string text)
    {
        var spec = TableShortcode.Read(Code(text), out var problem);

        Assert.Null(problem);
        Assert.Equal("Media", spec!.Tab);
        Assert.Empty(spec.Connections);
        Assert.Equal(["status", "uptime"], spec.Columns);
    }

    [Theory]
    [InlineData("{{table: NAS}}", "Say what goes in the rows and the columns")]
    [InlineData("{{table: NAS / cpu_percent / ram_percent}}", "one slash")]
    [InlineData("{{table: / cpu_percent}}", "Say which connections go in the rows")]
    [InlineData("{{table: NAS / }}", "Say what goes in the columns")]
    [InlineData("{{table: NAS / cpu_percent sparkline=45d}}", "between 1h and 30d")]
    [InlineData("{{table: NAS / cpu_percent sparkline=often}}", "is not a window")]
    public void ATableThatCannotBeReadSaysWhy(string text, string why)
    {
        Assert.Null(TableShortcode.Read(Code(text), out var problem));
        Assert.Contains(why, problem);
    }

    [Fact]
    public void ASparklineOfTrueIsADay()
    {
        Assert.Equal(TimeSpan.FromHours(24), TableShortcode.Read(Code("{{table: NAS / cpu_percent sparkline=true}}"), out _)!.Sparkline);
        Assert.Null(TableShortcode.Read(Code("{{table: NAS / cpu_percent sparkline=off}}"), out _)!.Sparkline);
    }

    [Fact]
    public void TheSparklineWindowReadsBackAsTheSameWindow()
    {
        Assert.Equal("24h", LiveReading.WindowWord(TimeSpan.FromHours(24)));
        Assert.Equal("720h", LiveReading.WindowWord(TimeSpan.FromDays(30)));
        Assert.Equal("90min", LiveReading.WindowWord(TimeSpan.FromMinutes(90)));
        Assert.Equal(TimeSpan.FromDays(30), SparkWindow.Parse(LiveReading.WindowWord(TimeSpan.FromDays(30)), out _));
    }

    // ---------- {{gauge}} parsing ----------

    [Fact]
    public void AGaugeReadsItsConnectionMetricAndOptions()
    {
        var spec = GaugeShortcode.Read(Code("""{{gauge: "QNAP NAS" / disk_percent min=10 max=90 warn=70 crit=85 label="NAS" size=medium style=bar}}"""), out var problem);

        Assert.Null(problem);
        Assert.Equal(new GaugeSpec("QNAP NAS", "disk_percent", 10, 90, 70, 85, "NAS", true, GaugeStyle.Bar), spec);
    }

    [Fact]
    public void AGaugeIsASmallRingUnlessToldOtherwise()
    {
        var spec = GaugeShortcode.Read(Code("{{gauge: NAS / disk_percent}}"), out _)!;

        Assert.False(spec.Medium);
        Assert.Equal(GaugeStyle.Ring, spec.Style);
        Assert.Null(spec.Max);
    }

    [Theory]
    [InlineData("{{gauge: NAS}}", "Say which metric")]
    [InlineData("{{gauge: metric=cpu_percent}}", "Say which connection")]
    [InlineData("{{gauge: NAS / cpu_percent max=lots}}", "max=“lots” is not a number")]
    [InlineData("{{gauge: NAS / cpu_percent min=50 max=10}}", "max= has to be above min=")]
    [InlineData("{{gauge: NAS / cpu_percent size=huge}}", "Use small or medium")]
    [InlineData("{{gauge: NAS / cpu_percent style=dial}}", "Use ring or bar")]
    public void AGaugeThatCannotBeReadSaysWhy(string text, string why)
    {
        Assert.Null(GaugeShortcode.Read(Code(text), out var problem));
        Assert.Contains(why, problem);
    }

    [Fact]
    public void AGaugesScaleIsAPercentagesOrReachesPastItsLines()
    {
        var percent = new MetricSpec("disk_percent", "Disk used", "%");
        var temperature = new MetricSpec("temp_c", "Temperature", "°C", 1);
        var watts = new MetricSpec("power_watts", "Power", " W");
        var plain = GaugeShortcode.Read(Code("{{gauge: NAS / x}}"), out _)!;

        Assert.Equal((0, 100), GaugeShortcode.Scale(plain, percent, MetricBands.None));
        Assert.Equal((0, 100), GaugeShortcode.Scale(plain, temperature, new MetricBands(60, 80, false, BandSource.AlertRules)));
        Assert.Equal((0, 500), GaugeShortcode.Scale(plain, watts, new MetricBands(null, 400, false, BandSource.AlertRules)));
        Assert.Equal((5, 50), GaugeShortcode.Scale(plain with { Min = 5, Max = 50 }, watts, MetricBands.None));

        Assert.Equal(0.5, GaugeShortcode.Fraction(50, 0, 100));
        Assert.Equal(1, GaugeShortcode.Fraction(150, 0, 100));
        Assert.Equal(0, GaugeShortcode.Fraction(-3, 0, 100));
    }

    // ---------- threshold colouring ----------

    private static readonly MetricSpec Disk = MetricSpec.WellKnown.Single(m => m.Key == "disk_percent");
    private static readonly MetricSpec Cpu = MetricSpec.WellKnown.Single(m => m.Key == "cpu_percent");
    private static readonly MetricSpec Battery = MetricSpec.WellKnown.Single(m => m.Key == "battery_percent");

    [Fact]
    public void OneAlertRuleIsTheCriticalLineAndTheReadingMustBePastIt()
    {
        AlertRule[] rules = [new() { ConnectionId = "nas", Metric = "cpu_percent", Threshold = 90 }];
        var bands = MetricBands.For(rules, "nas", "cpu_percent", Cpu);

        Assert.Equal(new MetricBands(null, 90, false, BandSource.AlertRules, Strict: true), bands);
        Assert.Equal("is-fine", bands.Level(90));
        Assert.Equal("is-critical", bands.Level(90.1));
    }

    [Fact]
    public void TwoRulesOnOneSideAreAWarningAndACriticalLine()
    {
        AlertRule[] rules =
        [
            new() { Metric = "disk_percent", Threshold = 95 },
            new() { Metric = "disk_percent", Threshold = 85 },
        ];
        var bands = MetricBands.For(rules, "nas", "disk_percent", Disk);

        Assert.Equal((85d, 95d), (bands.Warn!.Value, bands.Crit!.Value));
        Assert.Equal("is-fine", bands.Level(80));
        Assert.Equal("is-warm", bands.Level(90));
        Assert.Equal("is-critical", bands.Level(96));
        Assert.Equal("warning above 85%, critical above 95% (from its alert rules)", bands.Describe(v => $"{v}%"));
    }

    [Fact]
    public void AConnectionsOwnRulesWinOverOnesForEveryConnection()
    {
        AlertRule[] rules =
        [
            new() { Metric = "temp_c", Threshold = 50 },
            new() { ConnectionId = "pi", Metric = "temp_c", Threshold = 70 },
            new() { ConnectionId = "other", Metric = "temp_c", Threshold = 10 },
        ];
        var temp = MetricSpec.WellKnown.Single(m => m.Key == "temp_c");

        Assert.Equal(70, MetricBands.For(rules, "pi", "temp_c", temp).Crit);
        Assert.Equal(50, MetricBands.For(rules, "nas", "temp_c", temp).Crit);
    }

    [Fact]
    public void ABelowRuleColoursGoingDown()
    {
        AlertRule[] rules =
        [
            new() { Metric = "battery_percent", Comparison = Comparison.Below, Threshold = 20 },
            new() { Metric = "battery_percent", Comparison = Comparison.Below, Threshold = 50 },
        ];
        var bands = MetricBands.For(rules, "ups", "battery_percent", Battery);

        Assert.True(bands.Falling);
        Assert.Equal((50d, 20d), (bands.Warn!.Value, bands.Crit!.Value));
        Assert.Equal("is-fine", bands.Level(80));
        Assert.Equal("is-warm", bands.Level(40));
        Assert.Equal("is-critical", bands.Level(10));
    }

    [Fact]
    public void DisabledUnusualAndOtherMetricsRulesAreLeftOut()
    {
        AlertRule[] rules =
        [
            new() { Metric = "cpu_percent", Threshold = 50, Enabled = false },
            new() { Metric = "cpu_percent", Threshold = 200, Kind = RuleKind.Unusual },
            new() { Metric = "ram_percent", Threshold = 10 },
        ];

        Assert.Equal(MetricBands.None, MetricBands.For(rules, "nas", "cpu_percent", Cpu));
        Assert.Equal("", MetricBands.None.Level(99));
    }

    [Fact]
    public void WithNoRulesADiskUsesTheGaugeCardsLinesAndACpuStaysUncoloured()
    {
        var disk = MetricBands.For([], "nas", "disk_percent", Disk);

        Assert.Equal(BandSource.MetricHint, disk.Source);
        Assert.Equal("is-warm", disk.Level(80));
        Assert.Equal("is-critical", disk.Level(95));
        Assert.True(MetricBands.For(null, "nas", "cpu_percent", Cpu).IsEmpty);
    }

    [Fact]
    public void WrittenLinesWinAndTheirOrderSaysWhichWayIsWorse()
    {
        AlertRule[] rules = [new() { Metric = "disk_percent", Threshold = 95 }];

        var written = MetricBands.For(rules, "nas", "disk_percent", Disk, warn: 60, crit: 70);
        Assert.Equal(new MetricBands(60, 70, false, BandSource.Written), written);
        Assert.Equal("is-warm", written.Level(60));

        var falling = MetricBands.For(null, "ups", "battery_percent", Battery, warn: 50, crit: 20);
        Assert.True(falling.Falling);
        Assert.Equal("is-critical", falling.Level(20));
    }

    // ---------- unit conversion: lines are stored units, the words are the reader's ----------

    [Fact]
    public void TheLinesStayInTheStoredUnitAndAreDescribedInTheReadersUnits()
    {
        var temp = MetricSpec.WellKnown.Single(m => m.Key == "temp_c");
        AlertRule[] rules = [new() { Metric = "temp_c", Threshold = 60 }];
        var bands = MetricBands.For(rules, "pi", "temp_c", temp);
        var fahrenheit = Units.Preferences.Of(Units.Imperial);

        // 61 °C is past a 60 °C rule whatever the page is read in…
        Assert.Equal("is-critical", bands.Level(61));
        // …and the tooltip says the line in the reader's unit.
        Assert.Equal("critical above 140.0°F (from its alert rules)", bands.Describe(v => Units.Format(temp, v, fahrenheit)));
    }

    // ---------- diagrams: parsing ----------

    [Fact]
    public void TheNativeSyntaxChainsFansOutAndIn()
    {
        var graph = DiagramParser.Parse("""
            Internet -> Router -> "QNAP NAS" -> Plex
            "QNAP NAS" -> Sonarr, Radarr
            Router -> "Domain controller"
            Sonarr, Radarr -> Downloads
            """, out var problem)!;

        Assert.Null(problem);
        Assert.Equal(DiagramDirection.LeftRight, graph.Direction);
        Assert.Equal(["Internet", "Router", "QNAP NAS", "Plex", "Sonarr", "Radarr", "Domain controller", "Downloads"], graph.Nodes.Select(n => n.Label));
        Assert.Equal(
        [
            "Internet>Router", "Router>QNAP NAS", "QNAP NAS>Plex", "QNAP NAS>Sonarr", "QNAP NAS>Radarr",
            "Router>Domain controller", "Sonarr>Downloads", "Radarr>Downloads",
        ], graph.Edges.Select(e => $"{e.From}>{e.To}"));
    }

    [Fact]
    public void TheNativeSyntaxTakesLabelsCommentsDirectionAndQuotedCommas()
    {
        var graph = DiagramParser.Parse("""
            direction TD
            # the office
            Router ->|VPN| "Home, office"   # a comment
            router => nas
            """, out _)!;

        Assert.Equal(DiagramDirection.TopDown, graph.Direction);
        Assert.Equal(["Router", "Home, office", "nas"], graph.Nodes.Select(n => n.Label));
        Assert.Equal("VPN", graph.Edges[0].Label);
        // Names compare ignoring case, so "router" is the same box as "Router".
        Assert.Equal("Router", graph.Edges[1].From);
    }

    [Fact]
    public void TheMermaidSubsetReadsIdsShapesLabelsAndArrows()
    {
        var text = """
            %% copied from somewhere
            graph LR
              A[Internet] -->|fibre| B(Router)
              B --> C[("QNAP NAS")]
              B -- wifi --> D{Laptop}
              C -.-> E((Backup)) & F>Offsite]
              C === G[[Plex]]
              C --- H
              classDef big fill:#f00
              class C big
              style C fill:#0f0
              subgraph Media
                G --> I[Sonarr];
              end
              click C "https://example.com"
            """;
        Assert.True(DiagramParser.IsMermaid(text));
        var graph = DiagramParser.Parse(text, out var problem)!;

        Assert.Null(problem);
        Assert.Equal(DiagramDirection.LeftRight, graph.Direction);
        Assert.Equal(["Internet", "Router", "QNAP NAS", "Laptop", "Backup", "Offsite", "Plex", "H", "Sonarr"], graph.Nodes.Select(n => n.Label));
        Assert.Equal(["A", "B", "C", "D", "E", "F", "G", "H", "I"], graph.Nodes.Select(n => n.Key));

        var edges = graph.Edges.ToDictionary(e => $"{e.From}>{e.To}");
        Assert.Equal("fibre", edges["A>B"].Label);
        Assert.Equal("wifi", edges["B>D"].Label);
        Assert.Equal(EdgeStyle.Dotted, edges["C>E"].Style);
        Assert.Equal(EdgeStyle.Dotted, edges["C>F"].Style);
        Assert.Equal(EdgeStyle.Thick, edges["C>G"].Style);
        Assert.Equal(EdgeStyle.Line, edges["C>H"].Style);
        Assert.Equal(EdgeStyle.Arrow, edges["G>I"].Style);
    }

    [Fact]
    public void MermaidWithoutSpacesAroundArrowsAndTopDownByDefault()
    {
        var graph = DiagramParser.Parse("flowchart\nA-->B-->C\nA-.->C", out _)!;

        Assert.Equal(DiagramDirection.TopDown, graph.Direction);
        Assert.Equal(["A>B", "B>C", "A>C"], graph.Edges.Select(e => $"{e.From}>{e.To}"));
    }

    [Theory]
    [InlineData("", "nothing in it")]
    [InlineData("A -> ", "nothing at one end")]
    [InlineData("graph TD\nA --> B[never closed", "never closed")]
    [InlineData("graph TD\nA ~~~ B", "is not an arrow")]
    [InlineData("graph TD\nA -->", "ends with an arrow")]
    public void ADiagramThatCannotBeReadSaysWhy(string text, string why)
    {
        Assert.Null(DiagramParser.Parse(text, out var problem));
        Assert.Contains(why, problem);
    }

    [Fact]
    public void ADiagramTooBigToReadIsRefused()
    {
        var text = string.Join('\n', Enumerable.Range(0, DiagramParser.MaxNodes + 1).Select(i => $"Hub -> Node{i}"));
        Assert.Null(DiagramParser.Parse(text, out var problem));
        Assert.Contains("at most 80 boxes", problem);
    }

    [Fact]
    public void ASequenceDiagramIsNotMermaidThisDraws()
    {
        Assert.False(DiagramParser.IsMermaid("sequenceDiagram\nAlice->>Bob: Hi"));
        Assert.True(DiagramParser.IsMermaid("%% c\n\ngraph TD;"));
    }

    // ---------- diagrams: layout ----------

    [Fact]
    public void AChainIsLaidOutOneLayerPerStepLeftToRight()
    {
        var graph = DiagramParser.Parse("Internet -> Router -> NAS -> Plex\nRouter -> DC", out _)!;
        var layout = DiagramLayout.Layout(graph);
        var box = layout.Boxes.ToDictionary(b => b.Node.Key);

        Assert.Equal([0, 1, 2, 3, 2], graph.Nodes.Select(n => box[n.Key].Layer));
        Assert.True(box["Router"].X > box["Internet"].X);
        Assert.Equal(box["NAS"].X, box["DC"].X);
        Assert.NotEqual(box["NAS"].Y, box["DC"].Y);
        Assert.All(layout.Links, l => Assert.False(l.Backwards));
        Assert.All(layout.Boxes, b => Assert.InRange(b.X + b.Width, 0, layout.Width));
        Assert.All(layout.Boxes, b => Assert.InRange(b.Y + b.Height, 0, layout.Height));
    }

    [Fact]
    public void TopDownSwapsTheAxes()
    {
        var layout = DiagramLayout.Layout(DiagramParser.Parse("direction TD\nA -> B", out _)!);
        var box = layout.Boxes.ToDictionary(b => b.Node.Key);

        Assert.Equal(box["A"].X, box["B"].X);
        Assert.True(box["B"].Y > box["A"].Y);
    }

    [Fact]
    public void ALoopIsBrokenStillDrawnAndLaysOutTheSameEveryTime()
    {
        var graph = DiagramParser.Parse("A -> B -> C -> A\nC -> C", out _)!;
        var first = DiagramLayout.Layout(graph);
        var second = DiagramLayout.Layout(graph);

        // Three edges drawn (the self-loop is not), one of them back round.
        Assert.Equal(3, first.Links.Count);
        Assert.Single(first.Links, l => l.Backwards);
        Assert.Equal(first.Links.Select(l => l.Path), second.Links.Select(l => l.Path));
        Assert.Equal(first.Boxes.Select(b => (b.X, b.Y)), second.Boxes.Select(b => (b.X, b.Y)));
    }

    [Fact]
    public void OrderingWithinALayerFollowsWhatItIsLinkedTo()
    {
        // Written so the second layer starts in the opposite order to its parents'.
        var graph = DiagramParser.Parse("Root -> P1, P2\nP2 -> C2\nP1 -> C1", out _)!;
        var box = DiagramLayout.Layout(graph).Boxes.ToDictionary(b => b.Node.Key);

        Assert.Equal(box["P1"].Slot < box["P2"].Slot, box["C1"].Slot < box["C2"].Slot);
    }

    [Fact]
    public void PathsAreWrittenTheInvariantWay()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var layout = DiagramLayout.Layout(DiagramParser.Parse("Alpha -> Beta, Gamma", out _)!);
            Assert.All(layout.Links, l => Assert.Matches(@"^M[\d.]+,[\d.]+ C[\d.]+,[\d.]+ [\d.]+,[\d.]+ [\d.]+,[\d.]+$", l.Path));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    // ---------- the Markdown around them ----------

    [Fact]
    public void ADiagramBlockBecomesOneBlockShortcodeAndOtherCodeStaysCode()
    {
        var document = new Markdown().Prepare("""
            Before.
            ```diagram
            Internet -> Router -> {{status: NAS}}
            ```
            After.

            ```bash
            echo "{{status: NAS}}"
            ```

            ```mermaid
            sequenceDiagram
            A->>B: hi
            ```
            """);

        var diagram = Assert.Single(document.Shortcodes);
        Assert.Equal("diagram", diagram.Kind);
        Assert.True(diagram.IsBlock);
        Assert.Equal("Internet -> Router -> {{status: NAS}}", diagram.Option(Markdown.DiagramBody));
        Assert.Equal("diagram", diagram.Option(Markdown.DiagramFence));
        // On a line of its own, so it is a paragraph that is nothing but the diagram.
        Assert.Contains($"<p>{Markdown.Placeholder(0)}</p>", document.Html);
        Assert.Contains("<p>Before.</p>", document.Html);
        Assert.Contains("<p>After.</p>", document.Html);
        Assert.Contains("<pre><code class=\"language-bash\">echo &quot;{{status: NAS}}&quot;", document.Html);
        // A sequence diagram is not the subset drawn here: it is left exactly as the renderer
        // has always drawn a Mermaid block — as its text.
        Assert.Contains("<pre class=\"mermaid\">sequenceDiagram", document.Html);
    }

    [Fact]
    public void AMermaidFlowchartIsADiagram()
    {
        var document = new Markdown().Prepare("```mermaid\ngraph TD\nA --> B\n```");
        Assert.Equal("mermaid", Assert.Single(document.Shortcodes).Option(Markdown.DiagramFence));
    }

    [Fact]
    public void ADiagramCannotBeForgedFromAOneLineShortcode()
    {
        // Option names start with a letter, so "#body" cannot be written in a shortcode.
        var code = Code("""{{diagram: #body="A -> B"}}""");
        Assert.Equal("", code.Option(Markdown.DiagramBody));
    }

    [Fact]
    public void TheNewKindsAreKnownAndInTheRightPlaces()
    {
        Assert.Contains("gauge", Shortcodes.InlineKinds);
        Assert.Contains("table", Shortcodes.BlockKinds);
        Assert.Contains("diagram", Shortcodes.BlockKinds);
        Assert.Empty(Shortcodes.Unknown("{{gauge: NAS / disk_percent}} {{table: NAS / status}} {{diagram: auto}}"));
    }
}
