using LabbyTwo.Core;
using LabbyTwo.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// The rules behind the second batch of Markdown features, without a component in sight:
/// the grammar of each new shortcode, {{details}} folds nesting with {{if}}, callouts from
/// the renderer, the words for dates and times, sparkline windows, the uptime strip's
/// colours, where a {{link}} goes, and what {{alerts}}, {{containers}} and {{renewals}}
/// list. The components are drawn in MarkdownExtrasRenderTests.
/// </summary>
public sealed class MarkdownExtrasTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly Markdown _markdown = new();

    public MarkdownExtrasTests() => _services = TestHost.Build(_directory);

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private Registry Registry => _services.GetRequiredService<Registry>();

    private static Shortcode Code(string text) => Shortcodes.Parse(text) ?? throw new InvalidOperationException(text);

    // ---------- the grammar ----------

    [Theory]
    [InlineData("{{alerts}}", "alerts", true)]
    [InlineData("{{alerts: only=\"NAS, Plex\"}}", "alerts", true)]
    [InlineData("{{containers: stopped}}", "containers", true)]
    [InlineData("{{containers}}", "containers", true)]
    [InlineData("{{renewals}}", "renewals", true)]
    [InlineData("{{renewals: days=30 limit=3}}", "renewals", true)]
    [InlineData("{{sparkline: QNAP NAS / cpu_percent 24h}}", "sparkline", false)]
    [InlineData("{{uptimebar: QNAP NAS days=14}}", "uptimebar", false)]
    [InlineData("{{link: Plex Media Server}}", "link", false)]
    [InlineData("{{today}}", "today", false)]
    [InlineData("{{today: format=iso}}", "today", false)]
    [InlineData("{{countdown: 2026-12-25}}", "countdown", false)]
    [InlineData("{{ago: QNAP NAS}}", "ago", false)]
    public void EachNewKindIsReadAndKnown(string text, string kind, bool block)
    {
        var code = Code(text);
        Assert.Equal(kind, code.Kind);
        Assert.True(code.IsKnown);
        Assert.Equal(block, code.IsBlock);
        Assert.False(code.IsStructure);
    }

    [Fact]
    public void TheArgumentsComeApartWhereTheyShould()
    {
        var spark = Code("{{sparkline: QNAP NAS / cpu_percent 24h}}");
        Assert.Equal(["QNAP NAS", "cpu_percent 24h"], spark.Target);

        var countdown = Code("{{countdown: 2026-12-25 18:00 label=\"Christmas dinner\"}}");
        Assert.Equal("2026-12-25 18:00", countdown.Part(0));
        Assert.Equal("Christmas dinner", countdown.Option("label"));

        var today = Code("{{today: format=\"dddd d MMMM\"}}");
        Assert.Equal("dddd d MMMM", today.Option("format"));

        var details = Code("{{details: Restart / recover open=true}}");
        Assert.True(details.IsStructure);
        Assert.Equal(["Restart", "recover"], details.Target);
        Assert.Equal("true", details.Option("open"));

        // Bare words that take nothing are written without a colon.
        Assert.Equal("{{alerts}}", Shortcodes.Write("alerts", []));
        Assert.Equal("{{today}}", Shortcodes.Write("today", []));
        Assert.Equal("{{containers: stopped}}", Shortcodes.Write("containers", ["stopped"]));
    }

    [Fact]
    public void OnlyTheListedBareWordsBecomeShortcodes()
    {
        Assert.Null(Shortcodes.Parse("{{sparkline}}"));
        Assert.Null(Shortcodes.Parse("{{details}}"));
        Assert.Null(Shortcodes.Parse("{{ .Today }}"));
        Assert.NotNull(Shortcodes.Parse("{{ today }}"));
    }

    // ---------- {{details}} ----------

    // Line endings normalised: these inputs are raw string literals, so they carry whatever
    // endings git checked this file out with (CRLF on a Windows clone) while the expected
    // text below is written with plain newlines. The parser keeps what it is given, as it should.
    private static IReadOnlyList<RunbookPart> Parse(string text) => Runbook.Parse(text.ReplaceLineEndings("\n"), []);

    [Fact]
    public void ADetailsFoldHoldsItsLinesUntilEnd()
    {
        var parts = Parse("Before.\n\n{{details: Full restart procedure}}\n1. Stop it.\n2. Start it.\n{{end}}\n\nAfter.\n");

        Assert.Collection(parts,
            p => Assert.Equal("Before.\n\n", Assert.IsType<RunbookText>(p).Markdown),
            p =>
            {
                var fold = Assert.IsType<RunbookDetails>(p);
                Assert.Equal("Full restart procedure", fold.Title);
                Assert.False(fold.Open);
                Assert.Equal("1. Stop it.\n2. Start it.\n", Assert.IsType<RunbookText>(Assert.Single(fold.Body)).Markdown);
            },
            p => Assert.Equal("\nAfter.\n", Assert.IsType<RunbookText>(p).Markdown));
    }

    [Theory]
    [InlineData("{{details: Steps open=true}}", true)]
    [InlineData("{{details: Steps open=\"yes\"}}", true)]
    [InlineData("{{details: Steps open=false}}", false)]
    [InlineData("{{details: Steps}}", false)]
    public void OpenIsAnOption(string line, bool open)
    {
        var fold = Assert.IsType<RunbookDetails>(Assert.Single(Parse(line + "\nx\n{{end}}\n")));
        Assert.Equal(open, fold.Open);
        Assert.Equal("Steps", fold.Title);
    }

    [Fact]
    public void EndClosesTheInnermostOfIfAndDetails()
    {
        var parts = Parse("""
            {{if down: NAS}}
            Down.
            {{details: How to fix it}}
            {{if up: Router}}
            Router fine.
            {{else}}
            Router down too.
            {{end}}
            Steps.
            {{end}}
            Still down.
            {{else}}
            Fine.
            {{end}}
            """);

        var outer = Assert.IsType<RunbookIf>(Assert.Single(parts));
        Assert.Collection(outer.Then,
            p => Assert.Equal("Down.\n", Assert.IsType<RunbookText>(p).Markdown),
            p =>
            {
                var fold = Assert.IsType<RunbookDetails>(p);
                Assert.Equal("How to fix it", fold.Title);
                Assert.Collection(fold.Body,
                    q =>
                    {
                        var inner = Assert.IsType<RunbookIf>(q);
                        Assert.Equal("Router fine.\n", Assert.IsType<RunbookText>(Assert.Single(inner.Then)).Markdown);
                        Assert.Equal("Router down too.\n", Assert.IsType<RunbookText>(Assert.Single(inner.Else)).Markdown);
                    },
                    q => Assert.Equal("Steps.\n", Assert.IsType<RunbookText>(q).Markdown));
            },
            p => Assert.Equal("Still down.\n", Assert.IsType<RunbookText>(p).Markdown));
        Assert.Equal("Fine.\n", Assert.IsType<RunbookText>(Assert.Single(outer.Else)).Markdown);
    }

    [Fact]
    public void ElseInsideAFoldIsAMistakeShownInside()
    {
        var parts = Parse("{{if down: NAS}}\n{{details: Steps}}\nA\n{{else}}\nB\n{{end}}\n{{end}}\n");

        var section = Assert.IsType<RunbookIf>(Assert.Single(parts));
        var fold = Assert.IsType<RunbookDetails>(Assert.Single(section.Then));
        var problem = Assert.IsType<RunbookProblem>(fold.Body[1]);
        Assert.Contains("{{else}} on line 4 is inside {{details: Steps}}", problem.Message);
        // The fold still ends at the first {{end}} and the section at the second.
        Assert.Empty(section.Else);
        Assert.Equal("B\n", Assert.IsType<RunbookText>(fold.Body[2]).Markdown);
    }

    [Fact]
    public void AnUnclosedFoldIsShownUnfoldedWithANote()
    {
        var parts = Parse("{{details: Steps}}\nEverything after.\n");

        Assert.Collection(parts,
            p => Assert.Contains("{{details: Steps}} on line 1 is never closed", Assert.IsType<RunbookProblem>(p).Message),
            p => Assert.Equal("Everything after.\n", Assert.IsType<RunbookText>(p).Markdown));
    }

    [Fact]
    public void AFoldTooDeepIsShownUnfolded()
    {
        var text = string.Concat(Enumerable.Repeat("{{details: D}}\n", Runbook.MaxDepth + 1)) + "Inside\n"
                   + string.Concat(Enumerable.Repeat("{{end}}\n", Runbook.MaxDepth + 1));
        var parts = Parse(text);

        var fold = Assert.IsType<RunbookDetails>(Assert.Single(parts));
        for (var i = 1; i < Runbook.MaxDepth; i++)
            fold = Assert.IsType<RunbookDetails>(Assert.Single(fold.Body));
        Assert.Contains("not folded", Assert.IsType<RunbookProblem>(fold.Body[0]).Message);
        Assert.Equal("Inside\n", Assert.IsType<RunbookText>(fold.Body[1]).Markdown);
    }

    [Fact]
    public void AFoldInsideCodeIsAnExample()
    {
        var page = _markdown.PreparePage("```\n{{details: Steps}}\nx\n{{end}}\n```\n");
        Assert.DoesNotContain(page.Parts, p => p is LiveDetails);
    }

    [Fact]
    public void ConditionsInsideAFoldAreStillDecided()
    {
        var page = _markdown.PreparePage("{{details: Steps}}\n{{if down: NAS}}\nx\n{{end}}\n{{end}}\n");

        Assert.True(page.HasSections);
        Assert.Single(page.Sections());
    }

    // ---------- callouts ----------

    [Theory]
    [InlineData("NOTE", "note", "Note")]
    [InlineData("TIP", "tip", "Tip")]
    [InlineData("IMPORTANT", "important", "Important")]
    [InlineData("WARNING", "warning", "Warning")]
    [InlineData("CAUTION", "caution", "Caution")]
    [InlineData("warning", "warning", "Warning")]
    public void EachCalloutKindIsABoxWithItsTitle(string written, string kind, string title)
    {
        var html = _markdown.ToHtml($"> [!{written}]\n> Mind the **gap**.\n");

        Assert.StartsWith($"<div class=\"md-callout md-callout-{kind}\" role=\"note\">", html);
        Assert.Contains($"<svg class=\"md-callout-icon\"", html);
        Assert.Contains($"</svg>{title}</p>", html);
        Assert.Contains("<p>Mind the <strong>gap</strong>.</p>", html);
        Assert.DoesNotContain("[!", html);
        Assert.DoesNotContain("markdown-alert", html);
    }

    [Fact]
    public void AnUnknownKindStaysAnOrdinaryQuote()
    {
        var html = _markdown.ToHtml("> [!FOO]\n> Just a quote.\n");

        Assert.StartsWith("<blockquote>", html);
        Assert.Contains("[!FOO]", html);
        Assert.DoesNotContain("md-callout", html);
        Assert.DoesNotContain("foo\"", html);
    }

    [Fact]
    public void ACalloutHoldsListsCodeAndLiveValues()
    {
        var doc = _markdown.Prepare("> [!WARNING]\n> Before restarting {{status: NAS}}:\n> - stop `{{status: Plex}}`\n> - check {{metric: NAS / disk_percent}}\n");

        Assert.Contains("md-callout-warning", doc.Html);
        Assert.Contains("<ul>", doc.Html);
        Assert.Equal(["status", "metric"], doc.Shortcodes.Select(s => s.Kind));
        Assert.Contains("<code>{{status: Plex}}</code>", doc.Html);
    }

    [Fact]
    public void HtmlInACalloutStaysText()
    {
        var html = _markdown.ToHtml("> [!CAUTION]\n> <script>alert(1)</script>\n");

        Assert.Contains("md-callout-caution", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public void AMarkerNotAtTheStartOfAQuoteIsText()
    {
        Assert.DoesNotContain("md-callout", _markdown.ToHtml("[!NOTE] not in a quote\n"));
        Assert.DoesNotContain("md-callout", _markdown.ToHtml("> Something first\n> [!NOTE]\n"));
    }

    // ---------- dates and times ----------

    private static readonly DateTime Tuesday = new(2026, 9, 29, 10, 30, 0);

    [Fact]
    public void TodayHasADefaultAndNamedFormats()
    {
        var now = new DateTimeOffset(Tuesday);
        Assert.Equal("Tuesday, 29 September 2026", LiveTime.Today(now, LiveTime.Format(null, out _)!));
        Assert.Equal("2026-09-29", LiveTime.Today(now, LiveTime.Format("iso", out _)!));
        Assert.Equal("29 Sep 2026", LiveTime.Today(now, LiveTime.Format("short", out _)!));
        Assert.Equal("10:30", LiveTime.Today(now, LiveTime.Format("time", out _)!));
        Assert.Equal("Tue 29", LiveTime.Today(now, LiveTime.Format("ddd d", out _)!));
        // One letter is the custom format, not .NET's standard one.
        Assert.Equal("29", LiveTime.Today(now, LiveTime.Format("d", out _)!));
        Assert.Equal("week of 29 Sep", LiveTime.Today(now, LiveTime.Format("'week of' d MMM", out _)!));
    }

    [Theory]
    [InlineData("<script>")]
    [InlineData("%d")]
    [InlineData("dddd \\n")]
    [InlineData("Kuala Lumpur")]
    [InlineData("--")]
    public void AFormatThatIsNotADateIsRefused(string format)
    {
        Assert.Null(LiveTime.Format(format, out var problem));
        Assert.Contains("is not a date format", problem);
    }

    [Theory]
    [InlineData("2026-09-29", "today")]
    [InlineData("2026-09-30", "tomorrow")]
    [InlineData("2026-09-28", "yesterday")]
    [InlineData("2026-12-25", "in 87 days")]
    [InlineData("2026-09-26", "3 days ago")]
    [InlineData("2027-09-29", "in 365 days")]
    public void ACountdownCountsCalendarDays(string date, string expected)
    {
        Assert.True(LiveTime.TryParseMoment(date, out var target, out var hasTime));
        Assert.False(hasTime);
        Assert.Equal(expected, LiveTime.Countdown(target, hasTime, Tuesday));
    }

    [Fact]
    public void TheEveningBeforeIsTomorrowNotToday()
    {
        LiveTime.TryParseMoment("2026-09-30", out var target, out _);
        Assert.Equal("tomorrow", LiveTime.Countdown(target, false, new DateTime(2026, 9, 29, 23, 59, 0)));
    }

    [Theory]
    [InlineData("2026-09-29 13:50", "in 3 h 20 min")]
    [InlineData("2026-09-29 10:55", "in 25 min")]
    [InlineData("2026-09-29 10:30", "now")]
    [InlineData("2026-09-29 10:05", "25 min ago")]
    [InlineData("2026-09-29T22:30", "in 12 h")]
    [InlineData("2026-10-01 09:00", "in 2 days")]
    [InlineData("2026-09-27 18:00", "2 days ago")]
    public void ACountdownWithATimeCountsHoursInsideADay(string moment, string expected)
    {
        Assert.True(LiveTime.TryParseMoment(moment, out var target, out var hasTime));
        Assert.True(hasTime);
        Assert.Equal(expected, LiveTime.Countdown(target, hasTime, Tuesday));
    }

    [Theory]
    [InlineData("")]
    [InlineData("25/12/2026")]
    [InlineData("2026-02-30")]
    [InlineData("2026-12-25 25:00")]
    [InlineData("Christmas")]
    [InlineData("2026-12-25<script>")]
    public void AnythingButAnIsoDateIsNotADate(string written) =>
        Assert.False(LiveTime.TryParseMoment(written, out _, out _));

    [Theory]
    [InlineData(0, "0 s ago")]
    [InlineData(12, "12 s ago")]
    [InlineData(59, "59 s ago")]
    [InlineData(60, "1 min ago")]
    [InlineData(3599, "59 min ago")]
    [InlineData(3 * 3600 + 5 * 60, "3h 5m ago")]
    [InlineData(-5, "0 s ago")]
    public void AgoCountsSecondsThenMinutesThenTheUsualShape(int seconds, string expected)
    {
        var now = new DateTimeOffset(Tuesday);
        Assert.Equal(expected, LiveTime.Probed(now.AddSeconds(-seconds), now));
    }

    // ---------- sparkline windows ----------

    [Theory]
    [InlineData("cpu_percent 24h", "cpu_percent", "24h")]
    [InlineData("cpu_percent", "cpu_percent", "")]
    [InlineData("Disk used 7d", "Disk used", "7d")]
    [InlineData("7d", "", "7d")]
    [InlineData("", "", "")]
    public void TheWindowIsTheLastWordWhenItLooksLikeOne(string part, string metric, string window) =>
        Assert.Equal((metric, window), SparkWindow.Split(part));

    [Theory]
    [InlineData("1h", 1)]
    [InlineData("24h", 24)]
    [InlineData("90min", 1.5)]
    [InlineData("7d", 168)]
    [InlineData("2w", 336)]
    [InlineData("30d", 720)]
    public void WindowsFromAnHourToThirtyDaysAreRead(string text, double hours)
    {
        Assert.Equal(TimeSpan.FromHours(hours), SparkWindow.Parse(text, out var problem));
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("30m", "between 1h and 30d")]
    [InlineData("31d", "between 1h and 30d")]
    [InlineData("5w", "between 1h and 30d")]
    [InlineData("forever", "is not a window")]
    public void OtherWindowsAreRefused(string text, string why)
    {
        Assert.Null(SparkWindow.Parse(text, out var problem));
        Assert.Contains(why, problem);
    }

    [Fact]
    public void ALongSeriesIsThinnedToAverages()
    {
        var values = Enumerable.Range(0, 2880).Select(i => (double)(i / 30)).ToList();
        var thin = SparkWindow.Thin(values);

        Assert.Equal(96, thin.Count);
        Assert.Equal(0, thin[0]);
        Assert.Equal(95, thin[^1]);
        Assert.Same(values, SparkWindow.Thin(values, 5000));
    }

    // ---------- the uptime strip ----------

    [Theory]
    [InlineData(null, "is-unknown")]
    [InlineData(100.0, "is-good")]
    [InlineData(99.9, "is-good")]
    [InlineData(99.89, "is-warn")]
    [InlineData(95.0, "is-warn")]
    [InlineData(94.99, "is-bad")]
    [InlineData(0.0, "is-bad")]
    public void DaysAreColouredAsTheStatusPageColoursThem(double? percent, string expected) =>
        Assert.Equal(expected, UptimeStrip.DayClass(percent));

    [Theory]
    [InlineData(null, 30)]
    [InlineData("14", 14)]
    [InlineData("3", 7)]
    [InlineData("365", 90)]
    [InlineData("lots", 30)]
    public void TheStripIsAWeekToNinetyDays(string? written, int days) =>
        Assert.Equal(days, UptimeStrip.Days(written));

    // ---------- links ----------

    private static Connection With(string name, params (string Key, string Value)[] settings)
    {
        var bag = new SettingsBag();
        foreach (var (key, value) in settings)
            bag[key] = value;
        return new Connection { Provider = "http", Name = name, Settings = bag };
    }

    [Fact]
    public void TheLinkOpensWhereTheTileDoes()
    {
        Assert.Equal("https://plex.example.com", ShortcodeLookup.WebAddress(
            With("Plex", ("url", "http://plex:32400"), ("open_url", "https://plex.example.com")), out _));
        Assert.Equal("http://plex:32400", ShortcodeLookup.WebAddress(With("Plex", ("url", "http://plex:32400")), out _));
        Assert.Equal("http://192.168.1.5", ShortcodeLookup.WebAddress(With("NAS", ("host", "192.168.1.5")), out _));
        // A blank "Link opens" is not an address.
        Assert.Equal("http://plex:32400", ShortcodeLookup.WebAddress(With("Plex", ("url", "http://plex:32400"), ("open_url", "  ")), out _));
    }

    [Fact]
    public void NoAddressOrNotAWebAddressIsAQuestion()
    {
        Assert.Null(ShortcodeLookup.WebAddress(With("Ping"), out var none));
        Assert.Contains("has no web address", none);

        Assert.Null(ShortcodeLookup.WebAddress(With("Evil", ("url", "javascript:alert(1)")), out var script));
        Assert.Contains("not a web page", script);

        Assert.Null(ShortcodeLookup.WebAddress(With("Broker", ("url", "mqtt://broker:1883")), out _));
        Assert.Null(ShortcodeLookup.WebAddress(With("Files", ("url", "file:///etc/passwd")), out _));
    }

    // ---------- {{alerts}} ----------

    [Fact]
    public void AlertsAreTheFiringBreachesLongestFirst()
    {
        var nas = new Connection { Provider = "http", Name = "NAS" };
        var plex = new Connection { Provider = "http", Name = "Plex" };
        var slow = new AlertRule { Name = "Slow", Metric = "latency_ms", Threshold = 500 };
        var unnamed = new AlertRule { Metric = "latency_ms", Threshold = 900, ConnectionId = plex.Id };
        var now = DateTimeOffset.Now;

        var lines = MarkdownLists.Alerts(
            [
                new MetricAlertService.Breach(slow.Id, nas.Id, now.AddMinutes(-5), true, 812),
                new MetricAlertService.Breach(unnamed.Id, plex.Id, now.AddHours(-2), true, 1250),
                // Breaching but not yet sustained: not firing, not listed.
                new MetricAlertService.Breach(slow.Id, plex.Id, now, false, 700),
                // A rule since deleted.
                new MetricAlertService.Breach("gone", nas.Id, now, true, 1),
            ],
            [slow, unnamed], [nas, plex], Registry, only: null, Units.Preferences.Default);

        Assert.Collection(lines,
            l =>
            {
                Assert.Equal("Plex · Response time above 900 ms", l.Name);
                Assert.Equal("1250 ms", l.Value);
                Assert.Equal("above 900 ms", l.Limit);
            },
            l =>
            {
                Assert.Equal("Slow", l.Name);
                Assert.Equal("NAS", l.Connection.Name);
                Assert.Equal("812 ms", l.Value);
            });

        var only = MarkdownLists.Only(Code("{{alerts: only=\"nas, Nowhere\"}}"), [nas, plex], out var problem);
        Assert.Contains("“Nowhere”", problem);
        var filtered = MarkdownLists.Alerts([new(slow.Id, nas.Id, now, true, 812), new(unnamed.Id, plex.Id, now, true, 1250)],
            [slow, unnamed], [nas, plex], Registry, only, Units.Preferences.Default);
        Assert.Equal("NAS", Assert.Single(filtered).Connection.Name);
    }

    // ---------- {{containers}} ----------

    [Fact]
    public void ContainersAreFilteredWithTroubleFirst()
    {
        ContainerRow[] rows =
        [
            ContainerListTests.Row("web", project: "site"),
            ContainerListTests.Row("db", status: "Up 3 hours (unhealthy)", project: "site"),
            ContainerListTests.Row("old", state: "exited", status: "Exited (137) 2 days ago"),
            ContainerListTests.Row("done", state: "exited", status: "Exited (0) 1 hour ago"),
            ContainerListTests.Row("flappy", state: "restarting", status: "Restarting (1) 5 seconds ago"),
        ];

        Assert.Equal(["db", "flappy", "web", "done", "old"], MarkdownLists.Containers(rows, "all").Select(r => r.Name));
        Assert.Equal(["done", "old"], MarkdownLists.Containers(rows, "stopped").Select(r => r.Name));
        Assert.Equal(["db"], MarkdownLists.Containers(rows, "unhealthy").Select(r => r.Name));
        Assert.Equal(["db", "web"], MarkdownLists.Containers(rows, "running").Select(r => r.Name));

        Assert.Equal("status-down", MarkdownLists.ContainerDot(rows[1]));
        Assert.Equal("status-up", MarkdownLists.ContainerDot(rows[0]));
        Assert.Equal("status-down", MarkdownLists.ContainerDot(rows[2]));
        Assert.Equal("status-unknown", MarkdownLists.ContainerDot(rows[3]));
        Assert.Equal("status-flapping", MarkdownLists.ContainerDot(rows[4]));
    }

    [Theory]
    [InlineData("{{containers}}", "all")]
    [InlineData("{{containers: Stopped}}", "stopped")]
    [InlineData("{{containers: show=unhealthy}}", "unhealthy")]
    public void TheFilterIsOneOfTheTabsWords(string text, string expected) =>
        Assert.Equal(expected, MarkdownLists.ContainerFilter(Code(text), out _));

    [Fact]
    public void AnUnknownFilterIsAQuestion()
    {
        Assert.Null(MarkdownLists.ContainerFilter(Code("{{containers: sleeping}}"), out var problem));
        Assert.Contains("“sleeping”", problem);
    }

    [Fact]
    public void TheDockerConnectionIsNamedOrTheFirstEnabled()
    {
        var paused = new Connection { Provider = "docker", Name = "Old Docker", Enabled = false };
        var docker = new Connection { Provider = "docker", Name = "Docker" };
        var nas = new Connection { Provider = "http", Name = "NAS" };

        Assert.Same(docker, MarkdownLists.Docker(Code("{{containers}}"), [nas, paused, docker], out _));
        Assert.Same(paused, MarkdownLists.Docker(Code("{{containers: connection=\"Old Docker\"}}"), [nas, paused, docker], out _));
        Assert.Null(MarkdownLists.Docker(Code("{{containers: connection=NAS}}"), [nas, docker], out var notDocker));
        Assert.Contains("not a Docker connection", notDocker);
        Assert.Null(MarkdownLists.Docker(Code("{{containers}}"), [nas], out var none));
        Assert.Contains("no Docker connection", none);
    }

    // ---------- {{renewals}} ----------

    [Fact]
    public void RenewalsAreSoonestFirstWithOverdueOnTop()
    {
        var site = new Connection { Provider = "certificate", Name = "example.com" };
        var shop = new Connection { Provider = "certificate", Name = "shop.example.com" };
        var old = new Connection { Provider = "certificate", Name = "old.example.com" };
        var far = new Connection { Provider = "certificate", Name = "far.example.com" };
        var list = new Connection { Provider = "renewals", Name = "Renewals" };
        var off = new Connection { Provider = "certificate", Name = "paused.example.com", Enabled = false };

        var readings = new Dictionary<string, IReadOnlyDictionary<string, double>>
        {
            [site.Id] = new Dictionary<string, double> { ["cert_days_left"] = 40.6 },
            [shop.Id] = new Dictionary<string, double> { ["cert_days_left"] = 12 },
            [old.Id] = new Dictionary<string, double> { ["cert_days_left"] = -3 },
            [far.Id] = new Dictionary<string, double> { ["cert_days_left"] = 200 },
            [list.Id] = new Dictionary<string, double> { ["days_until_next"] = 1, ["overdue"] = 2 },
            [off.Id] = new Dictionary<string, double> { ["cert_days_left"] = 1 },
        };
        HealthMonitor.ProbeState? State(string id) => id == list.Id
            ? new HealthMonitor.ProbeState(id, true, "2 overdue: Domain, Insurance", TimeSpan.Zero, DateTimeOffset.Now, null, 0,
                new Dictionary<string, double>(), new Dictionary<string, string>())
            : null;

        var lines = MarkdownLists.Renewals([site, shop, old, far, list, off], id => readings[id], State, days: 60, limit: 10);

        Assert.Equal(
            ["Renewals: 2 overdue", "old.example.com: expired 3 days ago", "Renewals: tomorrow", "shop.example.com: in 12 days", "example.com: in 40 days"],
            lines.Select(l => $"{l.Connection.Name}: {MarkdownLists.RenewalWhen(l)}"));
        Assert.True(lines[0].IsOverdue);
        Assert.True(lines[1].IsOverdue);
        Assert.False(lines[2].IsOverdue);
        Assert.Equal("2 overdue: Domain, Insurance", lines[0].Detail);
        Assert.Null(lines[3].Detail);

        var few = MarkdownLists.Renewals([site, shop, old, far, list], id => readings[id], State, days: 30, limit: 3);
        Assert.Equal(3, few.Count);
        Assert.DoesNotContain(few, l => l.Connection == site);
    }

    [Fact]
    public void ALiveReadingBeatsAStoredOne()
    {
        var site = new Connection { Provider = "certificate", Name = "example.com" };
        var stored = new Dictionary<string, double> { ["cert_days_left"] = 30 };
        var live = new HealthMonitor.ProbeState(site.Id, true, "OK", TimeSpan.Zero, DateTimeOffset.Now, null, 0,
            new Dictionary<string, double> { ["cert_days_left"] = 89 }, new Dictionary<string, string>());

        var line = Assert.Single(MarkdownLists.Renewals([site], _ => stored, _ => live, days: 100, limit: 5));
        Assert.Equal(89, line.DaysLeft);
    }

    [Theory]
    [InlineData(0.4, "today")]
    [InlineData(-0.4, "expired today")]
    [InlineData(-1.5, "expired yesterday")]
    [InlineData(1.9, "tomorrow")]
    [InlineData(12.7, "in 12 days")]
    public void RenewalDaysReadAsWords(double days, string expected) =>
        Assert.Equal(expected, MarkdownLists.RenewalWhen(new RenewalLine(new Connection(), "certificate", days, 0, null)));
}
