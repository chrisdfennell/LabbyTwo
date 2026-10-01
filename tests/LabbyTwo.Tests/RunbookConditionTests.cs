using LabbyTwo.Core;
using LabbyTwo.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// Conditions on anything: the grammar (and / or / not, brackets, between, units, the new
/// tests), {{elif}} chains, the old forms reading exactly as they did, and deciding each
/// against a snapshot of the lab held in memory.
/// </summary>
public sealed class RunbookConditionTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;

    public RunbookConditionTests() => _services = TestHost.Build(_directory);

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private Registry Registry => _services.GetRequiredService<Registry>();

    private static RunbookExpr Read(string text)
    {
        var expression = Runbook.ParseExpression(text, out var problem);
        Assert.True(expression is not null, $"“{text}” did not read: {problem}");
        Assert.Null(problem);
        return expression!;
    }

    private static RunbookExpr Atom(RunbookTest test, string name = "") => new RunbookAtom(new RunbookCondition(test, name));

    // ---------- the old forms, exactly ----------

    [Theory]
    [InlineData("all up")]
    [InlineData("any down")]
    [InlineData("down: QNAP NAS")]
    [InlineData("up: Home Assistant")]
    [InlineData("down: \"Router / WAN\"")]
    [InlineData("metric: QNAP NAS / disk_percent > 85")]
    [InlineData("metric: Internet speed test / download_mbps < 200")]
    [InlineData("metric: QNAP NAS / firmware_update > 0")]
    [InlineData("metric: NAS / Disk used >= 90.5%")]
    [InlineData("metric: Router / latency_ms<=-3 ms")]
    [InlineData("metric: UPS / on_battery == 1")]
    [InlineData("metric: Office / temp_c > 122°F")]
    // A name with "and" in it was always one name, and still is.
    [InlineData("down: Sonarr and Radarr")]
    public void EveryOldFormReadsAsTheOneTestItAlwaysWas(string text)
    {
        var old = Runbook.ParseCondition(text, out _);
        Assert.NotNull(old);

        Assert.Equal(new RunbookAtom(old!), Read(text));
    }

    [Theory]
    [InlineData("sideways: NAS", "is not a condition")]
    [InlineData("down:", "Say which connection")]
    [InlineData("down: NAS / Router", "One connection")]
    [InlineData("metric: NAS / cpu", "Compare the metric with a number")]
    [InlineData("metric: NAS > 3", "Name a connection and a metric")]
    public void TheOldMistakesSayWhatTheyAlwaysSaid(string text, string reason)
    {
        Assert.Null(Runbook.ParseExpression(text, out var problem));
        Assert.Contains(reason, problem);
    }

    // ---------- the new tests ----------

    [Theory]
    [InlineData("any alert", RunbookTest.AnyAlert, "")]
    [InlineData("ANY  alerts", RunbookTest.AnyAlert, "")]
    [InlineData("maintenance", RunbookTest.Maintenance, "")]
    [InlineData("blind", RunbookTest.Blind, "")]
    [InlineData("backup late", RunbookTest.BackupLate, "")]
    [InlineData("backups late", RunbookTest.BackupLate, "")]
    [InlineData("backup late: \"Photos\"", RunbookTest.BackupLate, "Photos")]
    [InlineData("backup late: Family photos", RunbookTest.BackupLate, "Family photos")]
    [InlineData("incident open", RunbookTest.IncidentOpen, "")]
    [InlineData("alert: \"Disk almost full\"", RunbookTest.Alert, "Disk almost full")]
    [InlineData("alert: Disk almost full", RunbookTest.Alert, "Disk almost full")]
    [InlineData("alert on: \"QNAP NAS\"", RunbookTest.AlertOn, "QNAP NAS")]
    [InlineData("down: tab \"Media\"", RunbookTest.DownTab, "Media")]
    [InlineData("down: tab Media / TV", RunbookTest.Down, "")] // two parts: not a tab, and not one connection either
    [InlineData("up: tab \"Media / TV\"", RunbookTest.UpTab, "Media / TV")]
    [InlineData("up: TAB Media", RunbookTest.UpTab, "Media")]
    public void TheNewTestsRead(string text, RunbookTest test, string name)
    {
        var expression = Runbook.ParseExpression(text, out var problem);
        if (name.Length == 0 && test == RunbookTest.Down)
        {
            Assert.Null(expression);
            Assert.NotNull(problem);
            return;
        }
        Assert.Equal(new RunbookAtom(new RunbookCondition(test, name)), expression);
    }

    [Fact]
    public void AQuotedTabWordIsAConnectionName()
    {
        Assert.Equal(Atom(RunbookTest.Down, "tab Media"), Read("down: \"tab Media\""));
    }

    [Theory]
    [InlineData("metric: NAS / disk_percent = 85", "==", 85, "")]
    [InlineData("metric: NAS / disk_percent == 85", "==", 85, "")]
    [InlineData("metric: NAS / disk_percent != 85", "!=", 85, "")]
    [InlineData("metric: NAS / disk_percent >= 85 %", ">=", 85, "%")]
    [InlineData("metric: Speed / download_mbps < 200 Mbps", "<", 200, "Mbps")]
    [InlineData("metric: Office / temp_c > 50°C", ">", 50, "°C")]
    public void EveryComparisonReadsWithItsUnit(string text, string op, double value, string unit)
    {
        var condition = Assert.IsType<RunbookAtom>(Read(text)).Condition;

        Assert.Equal(op, condition.Operator);
        Assert.Equal(value, condition.Value);
        Assert.Equal(unit, condition.Unit);
    }

    [Theory]
    [InlineData("metric: Office / temp_c between 18 and 24", 18, 24, "")]
    [InlineData("metric: Office / temp_c between 18°C and 24°C", 18, 24, "°C")]
    [InlineData("metric: Office / temp_c between 64 and 75 °F", 64, 75, "°F")]
    [InlineData("metric: Office / temp_c between 24 and 18", 18, 24, "")]
    [InlineData("metric: NAS / disk_percent between 40 and 60 and not maintenance", 40, 60, "")]
    public void BetweenReadsBothEnds(string text, double low, double high, string unit)
    {
        var expression = Read(text);
        var condition = expression.Tests().First();

        Assert.Equal(RunbookTest.Metric, condition.Test);
        Assert.Equal("between", condition.Operator);
        Assert.Equal(low, condition.Value);
        Assert.Equal(high, condition.High);
        Assert.Equal(unit, condition.Unit);
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(50, true)]
    [InlineData(60, true)]
    [InlineData(39.9, false)]
    [InlineData(60.1, false)]
    public void BetweenIncludesBothEnds(double reading, bool holds)
    {
        Assert.Equal(holds, new RunbookCondition(RunbookTest.Metric, "NAS", "x", "between", 40, "", 60).Compare(reading));
    }

    // ---------- joining ----------

    [Fact]
    public void NotBindsTighterThanAndWhichBindsTighterThanOr()
    {
        var anyDown = Atom(RunbookTest.AnyDown);
        var maintenance = Atom(RunbookTest.Maintenance);
        var blind = Atom(RunbookTest.Blind);

        Assert.Equal(new RunbookOr(anyDown, new RunbookAnd(maintenance, blind)), Read("any down or maintenance and blind"));
        Assert.Equal(new RunbookAnd(new RunbookNot(maintenance), blind), Read("not maintenance and blind"));
        Assert.Equal(new RunbookAnd(new RunbookOr(anyDown, maintenance), blind), Read("(any down or maintenance) and blind"));
        Assert.Equal(new RunbookNot(new RunbookOr(anyDown, maintenance)), Read("not (any down or maintenance)"));
        Assert.Equal(new RunbookNot(new RunbookNot(blind)), Read("NOT not blind"));
    }

    [Fact]
    public void AndOrJoinLeftToRight()
    {
        var a = Atom(RunbookTest.AnyDown);
        var b = Atom(RunbookTest.Maintenance);
        var c = Atom(RunbookTest.Blind);

        Assert.Equal(new RunbookAnd(new RunbookAnd(a, b), c), Read("any down and maintenance and blind"));
        Assert.Equal(new RunbookOr(new RunbookOr(a, b), c), Read("any down or maintenance or blind"));
    }

    [Fact]
    public void AMetricTestJoinsWithOthers()
    {
        var expression = Read("metric: NAS / disk_percent > 85 and not maintenance");

        var and = Assert.IsType<RunbookAnd>(expression);
        Assert.Equal(new RunbookCondition(RunbookTest.Metric, "NAS", "disk_percent", ">", 85), Assert.IsType<RunbookAtom>(and.Left).Condition);
        Assert.Equal(new RunbookNot(Atom(RunbookTest.Maintenance)), and.Right);
    }

    [Fact]
    public void NamesEndAtAJoiningWordOrABracketAndQuotesKeepThemWhole()
    {
        Assert.Equal(new RunbookOr(Atom(RunbookTest.Down, "Home Assistant"), Atom(RunbookTest.Down, "Plex")),
            Read("down: Home Assistant or down: Plex"));
        Assert.Equal(new RunbookAnd(Atom(RunbookTest.Down, "Sonarr and Radarr"), Atom(RunbookTest.Maintenance)),
            Read("down: \"Sonarr and Radarr\" and maintenance"));
        Assert.Equal(new RunbookAnd(new RunbookOr(Atom(RunbookTest.Down, "NAS"), Atom(RunbookTest.Down, "Plex")), Atom(RunbookTest.Blind)),
            Read("(down: NAS or down: Plex) and blind"));
        // "android" and "notify" are words of their own, not "and" and "not".
        Assert.Equal(Atom(RunbookTest.Down, "android box"), Read("down: android box"));
        Assert.Equal(new RunbookAnd(Atom(RunbookTest.Up, "notify relay"), Atom(RunbookTest.Blind)), Read("up: notify relay and blind"));
    }

    [Theory]
    [InlineData("maintenance and", "missing at the end")]
    [InlineData("(any down or blind", "never closed")]
    [InlineData("any down) or blind", "closing bracket")]
    [InlineData("maintenance blind", "left over")]
    [InlineData("frobnicate and blind", "“frobnicate” is not a condition")]
    [InlineData("not", "missing at the end")]
    [InlineData("metric: NAS / disk_percent between 40", "between 40 and 60")]
    [InlineData("metric: Office / temp_c between 18°C and 75°F", "one unit")]
    [InlineData("alert:", "Say which alert rule")]
    [InlineData("alert on: and blind", "Say which connection")]
    public void ABadConditionSaysWhyInsteadOfThrowing(string text, string reason)
    {
        Assert.Null(Runbook.ParseExpression(text, out var problem));
        Assert.Contains(reason, problem);
    }

    [Fact]
    public void BracketsTooDeepAreAProblemNotAStackOverflow()
    {
        var text = new string('(', 500) + "blind" + new string(')', 500);

        Assert.Null(Runbook.ParseExpression(text, out var problem));
        Assert.Contains("deep", problem);
    }

    [Fact]
    public void ShortcodesTakeTheNewFormsButLeaveGoTemplatesAlone()
    {
        Assert.Equal("if", Shortcodes.Parse("{{if maintenance}}")!.Kind);
        Assert.Equal("if", Shortcodes.Parse("{{if not blind}}")!.Kind);
        Assert.Equal("if", Shortcodes.Parse("{{if (any down)}}")!.Kind);
        Assert.Equal("if", Shortcodes.Parse("{{if incident open}}")!.Kind);
        Assert.Equal(["any alert"], Shortcodes.Parse("{{elif any alert}}")!.Target);
        Assert.Equal("elif", Shortcodes.Parse("{{ ELIF down: NAS }}")!.Kind);
        Assert.True(Shortcodes.Parse("{{elif down: NAS}}")!.IsStructure);

        Assert.Null(Shortcodes.Parse("{{if .Ready}}"));
        Assert.Null(Shortcodes.Parse("{{if not .Ready}}"));
        Assert.Null(Shortcodes.Parse("{{elif .Ready}}"));
        Assert.Null(Shortcodes.Parse("{{if and .A .B}}"));
    }

    // ---------- {{elif}} ----------

    private static IReadOnlyList<RunbookPart> Parse(string text) => Runbook.Parse(text, []);

    [Fact]
    public void ElifIsTheOtherwiseOfTheOneBeforeAndOneEndClosesTheChain()
    {
        var parts = Parse("{{if down: NAS}}\nA\n{{elif down: Plex}}\nB\n{{elif any alert}}\nC\n{{else}}\nD\n{{end}}\nAfter\n");

        Assert.Equal(2, parts.Count);
        var first = Assert.IsType<RunbookIf>(parts[0]);
        Assert.Equal(Atom(RunbookTest.Down, "NAS"), first.Expression);
        Assert.Equal("A\n", Assert.IsType<RunbookText>(Assert.Single(first.Then)).Markdown);

        var second = Assert.IsType<RunbookIf>(Assert.Single(first.Else));
        Assert.Equal("{{elif down: Plex}}", second.Source);
        Assert.Equal("B\n", Assert.IsType<RunbookText>(Assert.Single(second.Then)).Markdown);

        var third = Assert.IsType<RunbookIf>(Assert.Single(second.Else));
        Assert.Equal(Atom(RunbookTest.AnyAlert), third.Expression);
        Assert.Equal("C\n", Assert.IsType<RunbookText>(Assert.Single(third.Then)).Markdown);
        Assert.Equal("D\n", Assert.IsType<RunbookText>(Assert.Single(third.Else)).Markdown);

        Assert.Equal("After\n", Assert.IsType<RunbookText>(parts[1]).Markdown);
    }

    [Fact]
    public void SectionsNestInsideElifBranches()
    {
        var parts = Parse("{{if all up}}\nA\n{{elif any down}}\n{{if down: NAS}}\nN\n{{else}}\nO\n{{end}}\nB\n{{end}}\nZ\n");

        Assert.Equal(2, parts.Count);
        var chain = Assert.IsType<RunbookIf>(Assert.Single(Assert.IsType<RunbookIf>(parts[0]).Else));
        Assert.Equal(2, chain.Then.Count);
        var inner = Assert.IsType<RunbookIf>(chain.Then[0]);
        Assert.Equal("N\n", Assert.IsType<RunbookText>(Assert.Single(inner.Then)).Markdown);
        Assert.Equal("O\n", Assert.IsType<RunbookText>(Assert.Single(inner.Else)).Markdown);
        Assert.Equal("B\n", Assert.IsType<RunbookText>(chain.Then[1]).Markdown);
    }

    [Fact]
    public void ALongElifChainIsNotTooDeep()
    {
        var text = "{{if down: A}}\na\n" + string.Concat(Enumerable.Range(0, 20).Select(i => $"{{{{elif down: B{i}}}}}\nb{i}\n")) + "{{end}}\n";

        IReadOnlyList<RunbookPart> branch = Parse(text);
        for (var i = 0; i <= 20; i++)
        {
            var section = Assert.IsType<RunbookIf>(Assert.Single(branch));
            Assert.Null(section.Problem);
            branch = section.Else;
        }
        Assert.Empty(branch);
    }

    [Fact]
    public void AStrayOrMisplacedElifIsANote()
    {
        var stray = Parse("x\n{{elif down: NAS}}\ny\n");
        Assert.Contains(stray, p => p is RunbookProblem { Message: var m } && m.Contains("has no {{if …}} above it"));

        var inFold = Parse("{{details: Steps}}\n{{elif down: NAS}}\n{{end}}\n");
        var fold = Assert.IsType<RunbookDetails>(Assert.Single(inFold));
        Assert.Contains(fold.Body, p => p is RunbookProblem { Message: var m } && m.Contains("no “otherwise”"));

        var afterElse = Assert.IsType<RunbookIf>(Assert.Single(Parse("{{if all up}}\na\n{{else}}\nb\n{{elif any down}}\nc\n{{end}}\n")));
        Assert.Contains("comes after {{else}}", Assert.Single(afterElse.Notes));
        Assert.Equal(2, afterElse.Else.Count);
    }

    [Fact]
    public void AnElifChainNeverClosedIsOneNoteAndEverythingShown()
    {
        var parts = Parse("{{if down: NAS}}\nA\n{{elif down: Plex}}\nB\n");

        Assert.Collection(parts,
            p => Assert.Contains("never closed", Assert.IsType<RunbookProblem>(p).Message),
            p => Assert.Equal("A\n", Assert.IsType<RunbookText>(p).Markdown),
            p => Assert.Equal("B\n", Assert.IsType<RunbookText>(p).Markdown));
    }

    [Fact]
    public void ABadElifConditionIsAProblemOnThatLinkOnly()
    {
        var first = Assert.IsType<RunbookIf>(Assert.Single(Parse("{{if all up}}\na\n{{elif sideways: x}}\nb\n{{end}}\n")));

        Assert.Null(first.Problem);
        var link = Assert.IsType<RunbookIf>(Assert.Single(first.Else));
        Assert.Null(link.Expression);
        Assert.Contains("{{elif sideways: x}}", link.Problem);
    }

    // ---------- deciding ----------

    [Fact]
    public void EveryTestIsAskedSoATypoShowsWhateverTheRestSays()
    {
        var asked = new List<RunbookTest>();
        var expression = Read("maintenance or down: Plexx");

        var (holds, problem) = expression.Decide(test =>
        {
            asked.Add(test.Test);
            return test.Test == RunbookTest.Maintenance ? (true, null) : (false, "No connection called “Plexx”.");
        });

        Assert.Equal([RunbookTest.Maintenance, RunbookTest.Down], asked);
        Assert.False(holds);
        Assert.Contains("Plexx", problem);
    }

    private static readonly Connection Nas = new() { Id = "nas", Provider = "http", Name = "QNAP NAS" };
    private static readonly Connection Plex = new() { Id = "plex", Provider = "http", Name = "Plex" };
    private static readonly Connection Sonarr = new() { Id = "sonarr", Provider = "http", Name = "Sonarr" };
    private static readonly Connection Office = new() { Id = "office", Provider = "http", Name = "Office" };

    private static readonly Tab Media = new() { Id = "media", Name = "Media", Slug = "media" };
    private static readonly Tab Home = new() { Id = "home", Name = "Home", Slug = "home" };

    private static readonly Widget[] Cards =
    [
        new() { Id = "w1", TabId = "media", ConnectionId = "plex" },
        new() { Id = "w2", TabId = "media", ConnectionId = "sonarr" },
        new() { Id = "w3", TabId = "home", ConnectionId = "nas" },
    ];

    private static HealthMonitor.ProbeState State(string id, bool? up, Dictionary<string, double>? metrics = null) =>
        new(id, up, "", TimeSpan.Zero, DateTimeOffset.Now, DateTimeOffset.Now, up == false ? 3 : 0, metrics ?? [], new Dictionary<string, string>());

    private RunbookFacts Facts(Dictionary<string, HealthMonitor.ProbeState> states, Dictionary<string, double>? stored = null) =>
        new(Registry, c => c.Enabled, id => states.GetValueOrDefault(id), _ => stored ?? []);

    private static RunbookContext Lab(
        IReadOnlyList<AlertRule>? rules = null,
        IReadOnlyCollection<MetricAlertService.Breach>? firing = null,
        bool maintenance = false,
        bool blind = false,
        IReadOnlyList<BackupRow>? backups = null,
        IReadOnlyList<Incident>? incidents = null) =>
        new([Nas, Plex, Sonarr, Office], [Media, Home], Cards, rules, firing, maintenance, blind, backups, incidents);

    private bool Holds(RunbookFacts facts, string text, RunbookContext context)
    {
        var result = facts.Evaluate(Read(text), context);
        Assert.Null(result.Problem);
        return result.Holds;
    }

    [Fact]
    public void TheSimpleStatesComeFromTheSnapshot()
    {
        var facts = Facts([]);

        Assert.True(Holds(facts, "maintenance", Lab(maintenance: true)));
        Assert.False(Holds(facts, "maintenance", Lab()));
        Assert.True(Holds(facts, "blind", Lab(blind: true)));
        Assert.True(Holds(facts, "not blind", Lab()));

        var open = new Incident(1, DateTimeOffset.Now.AddMinutes(-5), null, DateTimeOffset.Now, false, []);
        var closed = open with { EndedAt = DateTimeOffset.Now };
        Assert.True(Holds(facts, "incident open", Lab(incidents: [open])));
        Assert.False(Holds(facts, "incident open", Lab(incidents: [closed])));
        // Not read yet: nothing to go on, which is false, not a problem.
        Assert.False(Holds(facts, "incident open", Lab()));
    }

    [Fact]
    public void AlertsAreFoundByNameAndByConnection()
    {
        var facts = Facts([]);
        AlertRule[] rules =
        [
            new() { Id = "disk", Name = "Disk almost full", Metric = "disk_percent", ConnectionId = "nas" },
            new() { Id = "cpu", Name = "", Metric = "cpu_percent", ConnectionId = "plex", Threshold = 90 },
        ];
        MetricAlertService.Breach[] firing = [new("disk", "nas", DateTimeOffset.Now, true, 93)];

        Assert.True(Holds(facts, "alert: \"Disk almost full\"", Lab(rules, firing)));
        Assert.True(Holds(facts, "alert: disk ALMOST full", Lab(rules, firing)));
        Assert.True(Holds(facts, "any alert", Lab(rules, firing)));
        Assert.True(Holds(facts, "alert on: \"QNAP NAS\"", Lab(rules, firing)));
        Assert.False(Holds(facts, "alert on: Plex", Lab(rules, firing)));
        Assert.False(Holds(facts, "alert: \"Disk almost full\"", Lab(rules, [])));
        Assert.False(Holds(facts, "any alert", Lab(rules, [])));
        // A rule nobody named answers to the name the Alerts page gives it.
        var described = rules[1].Describe(Registry.Metric(Plex, "cpu_percent").Label, "Plex");
        Assert.False(Holds(facts, $"alert: \"{described}\"", Lab(rules, firing)));

        var unknown = facts.Evaluate(Read("alert: \"Disk almost ful\""), Lab(rules, firing));
        Assert.False(unknown.Holds);
        Assert.Contains("No alert rule called “Disk almost ful”", unknown.Problem);

        // Rules not read yet: false, quietly.
        Assert.False(Holds(facts, "alert: \"Disk almost ful\"", Lab(null, firing)));

        var noSuchConnection = facts.Evaluate(Read("alert on: Fridge"), Lab(rules, firing));
        Assert.Contains("No connection called “Fridge”", noSuchConnection.Problem);
    }

    [Fact]
    public void BackupLateMeansOverdueOrNeverAndNamesOne()
    {
        var facts = Facts([]);
        BackupRow Row(string name, BackupState state) => new(new BackupItem { Id = name.ToLowerInvariant(), Name = name },
            new BackupReading(null, null, "test"), new BackupStatus(state, null, null, null), null, false);
        BackupRow[] rows = [Row("Photos", BackupState.Late), Row("Laptop", BackupState.Ok), Row("Docs", BackupState.Missing)];

        Assert.True(Holds(facts, "backup late", Lab(backups: rows)));
        Assert.True(Holds(facts, "backup late: \"Photos\"", Lab(backups: rows)));
        Assert.False(Holds(facts, "backup late: laptop", Lab(backups: rows)));
        // Can't be checked is not late: nothing says it is.
        Assert.False(Holds(facts, "backup late: Docs", Lab(backups: rows)));
        Assert.False(Holds(facts, "backup late", Lab(backups: [rows[1], rows[2]])));
        Assert.True(Holds(facts, "backup late", Lab(backups: [Row("New", BackupState.Never)])));

        Assert.Contains("No backup called “Car”", facts.Evaluate(Read("backup late: Car"), Lab(backups: rows)).Problem);
    }

    [Fact]
    public void ATabIsDownWhenAnythingOnItIsAndUpWhenEverythingIs()
    {
        var facts = Facts(new()
        {
            ["plex"] = State("plex", false),
            ["sonarr"] = State("sonarr", true),
            ["nas"] = State("nas", true),
        });

        Assert.True(Holds(facts, "down: tab \"Media\"", Lab()));
        Assert.False(Holds(facts, "up: tab Media", Lab()));
        Assert.False(Holds(facts, "down: tab home", Lab()));
        Assert.True(Holds(facts, "up: tab Home", Lab()));

        var unknown = facts.Evaluate(Read("down: tab Garage"), Lab());
        Assert.Contains("No tab called “Garage”", unknown.Problem);
    }

    [Fact]
    public void AConnectionCalledTabSomethingStillMeansThatConnection()
    {
        var odd = new Connection { Id = "odd", Provider = "http", Name = "tab Garage" };
        var facts = Facts(new() { ["odd"] = State("odd", false) });
        var context = Lab() with { Connections = [Nas, odd] };

        Assert.True(Holds(facts, "down: tab Garage", context));
    }

    [Fact]
    public void JoinedConditionsDecideAsWritten()
    {
        var facts = Facts(new()
        {
            ["nas"] = State("nas", true, new() { ["disk_percent"] = 91 }),
            ["plex"] = State("plex", false),
        });

        Assert.True(Holds(facts, "metric: QNAP NAS / disk_percent > 85 and not maintenance", Lab()));
        Assert.False(Holds(facts, "metric: QNAP NAS / disk_percent > 85 and not maintenance", Lab(maintenance: true)));
        Assert.True(Holds(facts, "metric: QNAP NAS / disk_percent between 90 and 95", Lab()));
        Assert.False(Holds(facts, "metric: QNAP NAS / disk_percent between 40 and 60", Lab()));
        Assert.True(Holds(facts, "down: Plex and (maintenance or not blind)", Lab()));
        Assert.True(Holds(facts, "metric: QNAP NAS / disk_percent != 90", Lab()));
        Assert.True(Holds(facts, "metric: QNAP NAS / disk_percent = 91", Lab()));

        var broken = facts.Evaluate(Read("maintenance or down: Plexx"), Lab(maintenance: true));
        Assert.False(broken.Holds);
        Assert.Contains("No connection called “Plexx”", broken.Problem);
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(17.5, false)]
    [InlineData(24.5, false)]
    public void BetweenConvertsBothEndsFromTheUnitWritten(double celsius, bool holds)
    {
        var facts = Facts([], stored: new() { ["temp_c"] = celsius });

        // 64.4–75.2 °F is 18–24 °C.
        Assert.Equal(holds, Holds(facts, "metric: Office / temp_c between 64.4°F and 75.2°F", Lab()));
        Assert.Equal(holds, Holds(facts, "metric: Office / temp_c between 18 and 24", Lab()));
    }
}
