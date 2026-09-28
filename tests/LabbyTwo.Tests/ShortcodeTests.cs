using LabbyTwo.Core;
using LabbyTwo.Services;

namespace LabbyTwo.Tests;

/// <summary>
/// The text side of live values in Markdown: reading <c>{{kind: args}}</c>, finding what a
/// name refers to, the words a value is drawn as, and — most of all — that swapping
/// shortcodes for placeholders around the renderer leaves its sanitising exactly as it was.
/// </summary>
public sealed class ShortcodeTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly Microsoft.Extensions.DependencyInjection.ServiceProvider _services;

    public ShortcodeTests() => _services = TestHost.Build(_directory);

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private Registry TestRegistry() =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Registry>(_services);

    // ---------- parsing ----------

    [Fact]
    public void AnInlineShortcodeIsReadWithItsTarget()
    {
        var code = Shortcodes.Parse("{{metric: NAS / disk_percent}}")!;

        Assert.Equal("metric", code.Kind);
        Assert.Equal(["NAS", "disk_percent"], code.Target);
        Assert.False(code.IsBlock);
        Assert.True(code.IsKnown);
    }

    [Fact]
    public void KindsAreCaseInsensitiveAndSpacingIsForgiven()
    {
        var code = Shortcodes.Parse("{{ STATUS :NAS}}")!;

        Assert.Equal("status", code.Kind);
        Assert.Equal(["NAS"], code.Target);
    }

    [Fact]
    public void ANameWithSpacesNeedsNoQuotes()
    {
        var code = Shortcodes.Parse("{{status: Home Assistant}}")!;

        Assert.Equal(["Home Assistant"], code.Target);
    }

    [Fact]
    public void QuotesKeepSlashesAndBracesInsideOnePart()
    {
        var code = Shortcodes.Parse("""{{metric: "Router / WAN}}" / rtt_ms}}""")!;

        Assert.Equal(["Router / WAN}}", "rtt_ms"], code.Target);
    }

    [Fact]
    public void EscapedQuotesInsideQuotesAreKept()
    {
        var code = Shortcodes.Parse("""{{status: "The \"big\" box"}}""")!;

        Assert.Equal(["The \"big\" box"], code.Target);
    }

    [Fact]
    public void ABlockShortcodeReadsItsOptions()
    {
        var code = Shortcodes.Parse("""{{card: gauge connection="NAS" metric=cpu_percent title="CPU now"}}""")!;

        Assert.True(code.IsBlock);
        Assert.Equal(["gauge"], code.Target);
        Assert.Equal("NAS", code.Option("connection"));
        Assert.Equal("cpu_percent", code.Option("METRIC"));
        Assert.Equal("CPU now", code.Option("title"));
        Assert.Equal("fallback", code.Option("missing", "fallback"));
    }

    [Fact]
    public void ABareOptionValueRunsToTheNextSpaceSlashesIncluded()
    {
        var code = Shortcodes.Parse("{{card: iframe url=http://nas.lan/admin height=300}}")!;

        Assert.Equal("http://nas.lan/admin", code.Option("url"));
        Assert.Equal("300", code.Option("height"));
    }

    [Theory]
    [InlineData("{{ .Name }}")]           // a Go template: no colon
    [InlineData("{{#if ready}}")]         // Handlebars
    [InlineData("{{: NAS}}")]             // no kind
    [InlineData("{{1st: NAS}}")]          // a kind must start with a letter
    [InlineData("{{status: NAS")]         // never closed
    public void TextThatIsNotAShortcodeIsLeftAlone(string text)
    {
        Assert.Empty(Shortcodes.Find(text));
    }

    [Fact]
    public void FindReturnsEveryShortcodeWithItsPosition()
    {
        const string text = "NAS is {{status: NAS}} with {{metric: NAS / disk_percent}} used.";

        var found = Shortcodes.Find(text);

        Assert.Equal(2, found.Count);
        Assert.Equal("{{status: NAS}}", text.Substring(found[0].Index, found[0].Length));
        Assert.Equal("{{metric: NAS / disk_percent}}", text.Substring(found[1].Index, found[1].Length));
    }

    [Fact]
    public void ABackslashEscapesTheBraces()
    {
        Assert.Empty(Shortcodes.Find(@"Write \{{status: NAS}} to show a status."));
    }

    [Fact]
    public void AnEscapedBackslashDoesNotEscapeTheBraces()
    {
        Assert.Single(Shortcodes.Find(@"C:\\{{status: NAS}}"));
    }

    [Fact]
    public void AShortcodeLeftOpenDoesNotSwallowTheNextLine()
    {
        var found = Shortcodes.Find("{{status: NAS\nand then {{status: Router}}");

        Assert.Equal("{{status: Router}}", Assert.Single(found).Code.Source);
    }

    [Fact]
    public void TwoParsesOfTheSameTextAreEqual()
    {
        Assert.Equal(Shortcodes.Parse("{{metric: NAS / cpu}}"), Shortcodes.Parse("{{metric: NAS / cpu}}"));
    }

    [Fact]
    public void UnknownKindsAreReportedForTheEditor()
    {
        var unknown = Shortcodes.Unknown("{{stauts: NAS}} and {{status: NAS}}");

        Assert.Equal("stauts", Assert.Single(unknown).Kind);
    }

    [Theory]
    [InlineData("NAS")]
    [InlineData("Home Assistant")]
    [InlineData("Router / WAN")]
    [InlineData("a \"quoted\" name")]
    [InlineData("back\\slash")]
    [InlineData("x=y")]
    [InlineData("braces }} inside")]
    public void WhatTheInsertHelperWritesReadsBackTheSame(string name)
    {
        var written = Shortcodes.Write("card", ["gauge", name], [new("connection", name), new("metric", "cpu_percent")]);
        var code = Shortcodes.Parse(written)!;

        Assert.Equal(["gauge", name], code.Target);
        Assert.Equal(name, code.Option("connection"));
        Assert.Equal("cpu_percent", code.Option("metric"));
        Assert.Single(Shortcodes.Find("before " + written + " after"));
    }

    // ---------- rendering around the renderer ----------

    private readonly Markdown _markdown = new();

    [Fact]
    public void MarkdownWithoutShortcodesRendersExactlyAsBefore()
    {
        const string text = "# Title\n\nSome *text* with a [link](https://example.com).";

        var document = _markdown.Prepare(text);

        Assert.Equal(_markdown.ToHtml(text), document.Html);
        Assert.Empty(document.Shortcodes);
    }

    [Fact]
    public void EachShortcodeBecomesAPlaceholderInTheRenderedHtml()
    {
        var document = _markdown.Prepare("NAS is **{{status: NAS}}** now.");

        Assert.Equal("status", Assert.Single(document.Shortcodes).Kind);
        Assert.Contains("<strong>" + Markdown.Placeholder(0) + "</strong>", document.Html);
        Assert.DoesNotContain("{{", document.Html);
    }

    [Fact]
    public void AShortcodeInCodeStaysAnExample()
    {
        var document = _markdown.Prepare("Write `{{status: NAS}}`, or:\n\n```\n{{metric: NAS / cpu}}\n```\n");

        Assert.Empty(document.Shortcodes);
        Assert.Contains("<code>{{status: NAS}}</code>", document.Html);
        Assert.Contains("{{metric: NAS / cpu}}", document.Html);
    }

    [Fact]
    public void AnEscapedShortcodeShowsItsBracesWithoutTheBackslash()
    {
        var document = _markdown.Prepare(@"Type \{{status: NAS}} for a status.");

        Assert.Empty(document.Shortcodes);
        Assert.Contains("Type {{status: NAS}} for a status.", document.Html);
    }

    [Fact]
    public void ScriptInAShortcodeArgumentNeverReachesTheHtml()
    {
        var document = _markdown.Prepare("""Hi {{status: "<script>alert(1)</script>"}} <script>alert(2)</script>""");

        Assert.DoesNotContain("<script", document.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("<script>alert(1)</script>", Assert.Single(document.Shortcodes).Part(0));
    }

    [Fact]
    public void TypedTextThatLooksLikeAPlaceholderIsNotOne()
    {
        // The nonce is random per process, so nobody can type a real one; a guess at the
        // shape stays ordinary text.
        var document = _markdown.Prepare("ltaaaaaaaaaaq0q and {{status: NAS}}");

        Assert.Single(Markdown.Placeholders.Matches(document.Html));
    }

    // ---------- reading the renderer's HTML back ----------

    [Fact]
    public void MarkupTreeRebuildsNestedElementsAndKeepsTheirHtml()
    {
        var nodes = MarkupTree.Parse("<p>One <a href=\"/x?a=1&amp;b=2\">two</a><br />three</p>\n<hr />");

        var p = Assert.IsType<MarkupElement>(nodes[0]);
        Assert.Equal("p", p.Name);
        var a = Assert.IsType<MarkupElement>(p.Children[1]);
        Assert.Equal("/x?a=1&b=2", a.Attributes.Single(kv => kv.Key == "href").Value);
        Assert.Equal("<a href=\"/x?a=1&amp;b=2\">two</a>", a.OuterHtml);
        Assert.Equal("br", Assert.IsType<MarkupElement>(p.Children[2]).Name);
        Assert.Equal("hr", Assert.IsType<MarkupElement>(nodes[2]).Name);
    }

    [Fact]
    public void MarkupTreeToleratesAStrayEndTag()
    {
        var nodes = MarkupTree.Parse("<p>a</em>b</p>");

        var p = Assert.IsType<MarkupElement>(Assert.Single(nodes));
        Assert.Equal("ab", string.Concat(p.Children.OfType<MarkupText>().Select(t => t.Html)));
    }

    // ---------- resolution ----------

    private static readonly Connection Nas = new() { Id = "c1", Provider = "http", Name = "NAS" };
    private static readonly Connection Router = new() { Id = "NAS", Provider = "http", Name = "Router" };

    [Fact]
    public void AConnectionIsFoundByNameIgnoringCase()
    {
        Assert.Same(Nas, ShortcodeLookup.Connection([Nas], " nas "));
        Assert.Null(ShortcodeLookup.Connection([Nas], "Fridge"));
        Assert.Null(ShortcodeLookup.Connection([Nas], ""));
    }

    [Fact]
    public void AnIdWinsOverAName()
    {
        Assert.Same(Router, ShortcodeLookup.Connection([Nas, Router], "NAS"));
        Assert.Same(Nas, ShortcodeLookup.Connection([Nas, Router], "c1"));
    }

    [Fact]
    public void AMetricIsFoundByKeyOrLabel()
    {
        var registry = TestRegistry();

        Assert.Equal("latency_ms", ShortcodeLookup.MetricKey(registry, Nas, "LATENCY_MS", []));
        Assert.Equal("latency_ms", ShortcodeLookup.MetricKey(registry, Nas, "response time", []));
        Assert.Equal("disk_percent", ShortcodeLookup.MetricKey(registry, Nas, "Disk used", ["disk_percent"]));
        Assert.Null(ShortcodeLookup.MetricKey(registry, Nas, "nope", ["disk_percent"]));
    }

    [Fact]
    public void AWidgetIsFoundByIdOrByTitleInTabOrder()
    {
        var first = new Tab { Id = "t1", Sort = 0 };
        var second = new Tab { Id = "t2", Sort = 1 };
        var later = new Widget { Id = "w2", TabId = "t2", Title = "CPU" };
        var earlier = new Widget { Id = "w1", TabId = "t1", Title = "cpu", Sort = 5 };
        var scope = new ShortcodeScope([], [later, earlier], [first, second]);

        Assert.Same(earlier, ShortcodeLookup.Widget(scope, "CPU"));
        Assert.Same(later, ShortcodeLookup.Widget(scope, "w2"));
        Assert.Null(ShortcodeLookup.Widget(scope, "Memory"));
    }

    [Fact]
    public void AnAdHocCardHasAStableIdFromWhatWasWritten()
    {
        var a = Shortcodes.Parse("{{card: gauge connection=NAS}}")!;
        var b = Shortcodes.Parse("{{card: gauge connection=NAS}}")!;
        var c = Shortcodes.Parse("{{card: gauge connection=Router}}")!;

        Assert.Equal(ShortcodeLookup.AdHocId(a), ShortcodeLookup.AdHocId(b));
        Assert.NotEqual(ShortcodeLookup.AdHocId(a), ShortcodeLookup.AdHocId(c));
    }

    [Fact]
    public void CardSettingsStartFromTheTypesDefaultsAndLeaveOutTheDescriptiveWords()
    {
        var registry = TestRegistry();
        var gauge = ShortcodeLookup.WidgetType(registry, "Gauge")!;
        var code = Shortcodes.Parse("""{{card: gauge connection="NAS" title="x" metric=cpu_percent}}""")!;

        var settings = ShortcodeLookup.CardSettings(gauge, code);

        Assert.Equal("cpu_percent", settings["metric"]);
        Assert.False(settings.ContainsKey("connection"));
        Assert.False(settings.ContainsKey("title"));
    }

    [Fact]
    public void TheEmbedChainCatchesAnOwnerAndCountsDepth()
    {
        var chain = EmbedChain.Empty.With("note").With("w1");

        Assert.Equal(2, chain.Depth);
        Assert.True(chain.Contains("w1"));
        Assert.False(chain.Contains("w2"));
    }

    // ---------- the words ----------

    [Theory]
    [InlineData(72.345, "disk_percent")]
    [InlineData(12.0, "latency_ms")]
    [InlineData(21.46, "temp_c")]
    [InlineData(0.5, "rtt_ms")]
    public void AMetricReadsAsTheMetricTileAndTheRegistryWriteIt(double value, string key)
    {
        var spec = MetricSpec.WellKnown.Single(m => m.Key == key);
        var decimals = LiveText.Decimals(null, spec);

        // What the metric tile draws: the number, then the unit beside it.
        var tile = value.ToString($"F{Math.Clamp(spec.Decimals, 0, 4)}") + spec.Unit;

        Assert.Equal(tile, LiveText.Metric(value, decimals, spec.Unit));
        Assert.Equal(spec.Format(value), LiveText.Metric(value, decimals, spec.Unit));
    }

    [Fact]
    public void RequestedDecimalsAreKeptWithinReason()
    {
        var spec = new MetricSpec("x", "X", "", 1);

        Assert.Equal(1, LiveText.Decimals(null, spec));
        Assert.Equal(4, LiveText.Decimals(9, spec));
        Assert.Equal(0, LiveText.Decimals(-2, spec));
    }

    [Fact]
    public void StatusWordsFollowTheServiceTile()
    {
        HealthMonitor.ProbeState State(bool? up, int failures = 0) =>
            new("c1", up, "", TimeSpan.Zero, DateTimeOffset.UtcNow, null, failures,
                new Dictionary<string, double>(), new Dictionary<string, string>());

        Assert.Equal(("up", "status-up"), LiveText.Status(Nas, State(true)));
        Assert.Equal(("up", "status-flapping"), LiveText.Status(Nas, State(true, 1)));
        Assert.Equal(("down", "status-down"), LiveText.Status(Nas, State(false)));
        Assert.Equal(("checking", "status-unknown"), LiveText.Status(Nas, null));
        Assert.Equal(("paused", "status-unknown"), LiveText.Status(Nas with { Enabled = false }, State(true)));
    }

    [Fact]
    public void SinceReadsAsADurationWithoutAgo()
    {
        var now = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("3d 4h", LiveText.Since(now.AddDays(-3).AddHours(-4), now));
        Assert.Equal("under a minute", LiveText.Since(now.AddSeconds(-5), now));
    }

    [Fact]
    public void UptimeHasOneDecimalPlace()
    {
        Assert.Equal("99.9%", LiveText.Uptime(99.94));
    }
}
