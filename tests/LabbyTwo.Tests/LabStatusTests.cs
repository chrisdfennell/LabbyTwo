using System.Text.Json;
using System.Xml.Linq;
using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Services;

namespace LabbyTwo.Tests;

/// <summary>
/// The words behind {{status: tab …}}, {{status: all}}, {{who: home}} and {{who: watching}}:
/// what is counted, how it is said, and what the Plex and Tautulli probes keep for it.
/// </summary>
public sealed class LabStatusTests
{
    private static HealthMonitor.ProbeState State(string id, bool? up, int failures = 0, string? cantCheck = null,
        Dictionary<string, double>? metrics = null, Dictionary<string, string>? details = null) =>
        new(id, up, "", TimeSpan.Zero, DateTimeOffset.Now, DateTimeOffset.Now, failures, metrics ?? [], details ?? [])
        {
            CantCheck = cantCheck,
        };

    private static Connection C(string name, string provider = "http", bool enabled = true) =>
        new() { Id = name.ToLowerInvariant().Replace(' ', '-'), Provider = provider, Name = name, Enabled = enabled };

    private static StatusLine Summarize(IEnumerable<Connection> connections, Dictionary<string, HealthMonitor.ProbeState> states) =>
        LabStatus.Summarize("Media", connections, c => c.Enabled, id => states.GetValueOrDefault(id));

    // ---------- {{status: tab …}} ----------

    [Fact]
    public void TheLineCountsFineDownNamedAndChecking()
    {
        Connection[] media = [C("Plex"), C("Sonarr"), C("Radarr"), C("Overseerr")];
        var line = Summarize(media, new()
        {
            ["plex"] = State("plex", false),
            ["sonarr"] = State("sonarr", true),
            ["radarr"] = State("radarr", true, failures: 1),
        });

        Assert.Equal("Media: 2 fine, 1 down (Plex), 1 checking", line.Line);
        Assert.Equal(1, line.Down);
        Assert.Equal(["down", "up", "up", "checking"], line.Entries.Select(e => e.Word));
        Assert.Equal("status-flapping", line.Entries[2].Dot);
    }

    [Fact]
    public void AllFineSaysSoAndNothingMonitoredSaysThat()
    {
        Connection[] two = [C("Plex"), C("Sonarr")];
        Assert.Equal("Media: all 2 fine", Summarize(two, new() { ["plex"] = State("plex", true), ["sonarr"] = State("sonarr", true) }).Line);
        Assert.Equal("Media: 1 fine", Summarize([C("Plex")], new() { ["plex"] = State("plex", true) }).Line);
        Assert.Equal("Media: nothing monitored", Summarize([C("Old", enabled: false)], []).Line);
    }

    [Fact]
    public void ManyDownAreNamedUpToThreeAndCantCheckIsItsOwnWord()
    {
        Connection[] five = [C("A"), C("B"), C("C"), C("D"), C("E")];
        var line = Summarize(five, new()
        {
            ["a"] = State("a", false),
            ["b"] = State("b", false),
            ["c"] = State("c", false),
            ["d"] = State("d", false),
            ["e"] = State("e", false, cantCheck: "DNS is not answering"),
        });

        Assert.Equal("Media: 4 down (A, B, C and 1 more), 1 can't be checked", line.Line);
    }

    [Theory]
    [InlineData("{{status: all}}", "all", false)]
    [InlineData("{{status: ALL full}}", "all", true)]
    [InlineData("{{status: tab \"Media\"}}", "tab Media", false)]
    [InlineData("{{status: tab Media full}}", "tab Media", true)]
    [InlineData("{{status: tab \"Media / TV\" full=true}}", "tab Media / TV", true)]
    [InlineData("{{status: tab Media show=full}}", "tab Media", true)]
    [InlineData("{{status: NAS}}", null, false)]
    [InlineData("{{status: tab}}", null, false)]
    [InlineData("{{status: NAS / cpu}}", null, false)]
    public void TheSummaryFormsAreRecognised(string text, string? target, bool full)
    {
        var found = LabStatus.SummaryTarget(Shortcodes.Parse(text)!, [C("NAS")], out var isFull);

        Assert.Equal(target, found);
        if (target is not null)
            Assert.Equal(full, isFull);
    }

    [Fact]
    public void AConnectionActuallyCalledAllIsStillThatConnection()
    {
        Assert.Null(LabStatus.SummaryTarget(Shortcodes.Parse("{{status: all}}")!, [C("All")], out _));
        Assert.Null(LabStatus.SummaryTarget(Shortcodes.Parse("{{status: tab Media}}")!, [C("tab Media")], out _));
    }

    [Fact]
    public void ATabIsFoundByNameSlugOrIdAndHoldsWhatItsCardsAreBoundTo()
    {
        Tab[] tabs = [new() { Id = "t1", Name = "Media Room", Slug = "media" }];
        Assert.Equal("t1", LabStatus.FindTab(tabs, "media room")!.Id);
        Assert.Equal("t1", LabStatus.FindTab(tabs, "MEDIA")!.Id);
        Assert.Equal("t1", LabStatus.FindTab(tabs, "t1")!.Id);
        Assert.Null(LabStatus.FindTab(tabs, "Garage"));

        Connection[] all = [C("Plex"), C("NAS"), C("Sonarr")];
        Widget[] cards =
        [
            new() { TabId = "t1", ConnectionId = "sonarr" },
            new() { TabId = "t1", ConnectionId = "plex" },
            new() { TabId = "t1", ConnectionId = "plex" },
            new() { TabId = "t1" },
            new() { TabId = "t2", ConnectionId = "nas" },
        ];
        Assert.Equal(["Plex", "Sonarr"], LabStatus.OnTab(tabs[0], cards, all).Select(c => c.Name));
    }

    // ---------- {{who: home}} ----------

    /// <summary>Stands in for the Who's home plugin: its type name, and one metric per device.</summary>
    private sealed class Presence : IConnectionProvider
    {
        public string Type => "presence";
        public string DisplayName => "Who's home";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];
        public IReadOnlyList<MetricSpec> Metrics => [new("devices_home", "Devices home")];

        public IReadOnlyList<MetricSpec> MetricsFor(Connection connection) =>
            [.. Metrics, new("home_chris", "Chris"), new("home_sam", "Sam"), new("home_alex", "Alex")];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) => Task.FromResult(ProbeResult.Up(TimeSpan.Zero));
    }

    private static Registry Registry(params IConnectionProvider[] providers) => new(providers, [], []);

    [Fact]
    public void WhoIsHomeComesFromThePresenceMetrics()
    {
        var house = C("House", "presence");
        var states = new Dictionary<string, HealthMonitor.ProbeState>
        {
            ["house"] = State("house", true, metrics: new() { ["home_chris"] = 1, ["home_sam"] = 0, ["home_alex"] = 1, ["devices_home"] = 2 }),
        };

        var who = LabStatus.Home([house], Registry(new Presence()), id => states.GetValueOrDefault(id), _ => new Dictionary<string, double>());

        Assert.Null(who.Problem);
        Assert.False(who.Waiting);
        Assert.Equal(["Chris", "Alex"], who.Home);
        Assert.Equal(3, who.Watched);
        Assert.Equal("Chris and Alex", LabStatus.Join(who.Home));
    }

    [Fact]
    public void BeforeTheFirstSweepTheStoredReadingsAnswerAndWithNeitherItWaits()
    {
        var house = C("House", "presence");
        var registry = Registry(new Presence());

        var stored = LabStatus.Home([house], registry, _ => null, _ => new Dictionary<string, double> { ["home_sam"] = 1 });
        Assert.Equal(["Sam"], stored.Home);

        var nothing = LabStatus.Home([house], registry, _ => null, _ => new Dictionary<string, double>());
        Assert.True(nothing.Waiting);
    }

    [Fact]
    public void WithoutThePluginOrAConnectionPresenceIsNotSetUp()
    {
        var notInstalled = LabStatus.Home([C("House", "presence")], Registry(), _ => null, _ => new Dictionary<string, double>());
        Assert.Contains("install the Who's home plugin", notInstalled.Problem);

        var noConnection = LabStatus.Home([C("NAS")], Registry(new Presence()), _ => null, _ => new Dictionary<string, double>());
        Assert.Contains("add a Who's home connection", noConnection.Problem);
    }

    [Theory]
    [InlineData(new string[0], "")]
    [InlineData(new[] { "Chris" }, "Chris")]
    [InlineData(new[] { "Chris", "Sam", "Alex" }, "Chris, Sam and Alex")]
    public void NamesAreJoinedTheWayPeopleSayThem(string[] names, string expected)
    {
        Assert.Equal(expected, LabStatus.Join(names));
    }

    // ---------- {{who: watching}} ----------

    private static readonly NowPlayingStream Office = new("Chris", "The Office", "Dinner Party", "Living room TV", 42.4, true);
    private static readonly NowPlayingStream Dune = new("Sam", "Dune", "2021", "iPad", 90, false);

    [Fact]
    public void WatchingReadsWhatTheProbesKeptOnceEach()
    {
        var plex = C("Plex", "plex");
        var tautulli = C("Tautulli", "tautulli");
        var states = new Dictionary<string, HealthMonitor.ProbeState>
        {
            // Plex does not know Dune is direct; Tautulli does, and is read first.
            ["plex"] = State("plex", true, details: new() { [NowPlaying.DetailKey] = NowPlaying.Encode([Office with { Transcoding = null }, Dune]) }),
            ["tautulli"] = State("tautulli", true, details: new() { [NowPlaying.DetailKey] = NowPlaying.Encode([Office]) }),
        };

        var watching = LabStatus.Watching([plex, tautulli], id => states.GetValueOrDefault(id));

        Assert.Null(watching.Problem);
        Assert.Equal([Office, Dune], watching.Streams);
    }

    [Fact]
    public void NothingSetUpNothingPlayingAndNotYetAnsweredAreThreeAnswers()
    {
        Assert.Contains("add a Plex or Tautulli connection", LabStatus.Watching([C("NAS")], _ => null).Problem);

        var plex = C("Plex", "plex");
        Assert.True(LabStatus.Watching([plex], _ => null).Waiting);

        var idle = LabStatus.Watching([plex], _ => State("plex", true, details: new() { [NowPlaying.DetailKey] = "[]" }));
        Assert.False(idle.Waiting);
        Assert.Empty(idle.Streams);

        // Down: what it last said is not playing now.
        Assert.Empty(LabStatus.Watching([plex], _ => State("plex", false, details: new() { [NowPlaying.DetailKey] = NowPlaying.Encode([Dune]) })).Streams);
    }

    [Fact]
    public void AStreamIsDescribedWithWhatIsKnown()
    {
        Assert.Equal("Chris — The Office (Dinner Party) on Living room TV, 42%, transcoding", LabStatus.Describe(Office));
        Assert.Equal("Sam — Dune (2021) on iPad, 90%, direct", LabStatus.Describe(Dune));
        Assert.Equal("Something, 0%", LabStatus.Describe(new NowPlayingStream("", "", "", "", 0, null)));
    }

    [Fact]
    public void NowPlayingSurvivesTheTripAndIgnoresWhatItCannotRead()
    {
        Assert.Equal([Office, Dune], NowPlaying.Decode(NowPlaying.Encode([Office, Dune])));
        Assert.Empty(NowPlaying.Decode("not json"));
        Assert.Empty(NowPlaying.Decode("{\"a\":1}"));
        Assert.Empty(NowPlaying.Decode(null));
        Assert.Empty(NowPlaying.From(null));
        Assert.Equal(100, Assert.Single(NowPlaying.Decode("[{\"title\":\"x\",\"percent\":250}]")).Percent);
        Assert.Equal(NowPlaying.MaxStreams, NowPlaying.Decode(NowPlaying.Encode(Enumerable.Repeat(Dune, 100))).Count);
    }

    [Fact]
    public void PlexSessionsSayWhoWhatWhereAndWhetherItTranscodes()
    {
        var root = XElement.Parse("""
            <MediaContainer size="2">
              <Video title="Dinner Party" grandparentTitle="The Office" duration="1000" viewOffset="250">
                <User title="Chris" /><Player title="Living room TV" />
                <TranscodeSession videoDecision="transcode" audioDecision="copy" />
              </Video>
              <Video title="Dune" year="2021" duration="100" viewOffset="90">
                <User title="Sam" /><Player title="iPad" />
              </Video>
            </MediaContainer>
            """);

        var sessions = PlexProvider.ReadSessions(root);

        Assert.Equal(2, sessions.Count);
        Assert.Equal(("Dinner Party", "The Office", "Chris", "Living room TV", 25.0, true),
            (sessions[0].Title, sessions[0].Subtitle, sessions[0].User, sessions[0].Player, sessions[0].PercentDone, sessions[0].Transcoding));
        Assert.False(sessions[1].Transcoding);
        Assert.Equal("2021", sessions[1].Subtitle);

        // Kept for {{who: watching}} the way Tautulli says it: series, then episode.
        Assert.Equal(new NowPlayingStream("Chris", "The Office", "Dinner Party", "Living room TV", 25, true), sessions[0].ToStream());
        Assert.Equal(new NowPlayingStream("Sam", "Dune", "2021", "iPad", 90, false), sessions[1].ToStream());
    }

    [Fact]
    public void TautulliSessionsReadItsStringlyNumbersAndItsDecision()
    {
        using var document = JsonDocument.Parse("""
            {"stream_count":"2","sessions":[
              {"friendly_name":"Chris","user":"chris99","grandparent_title":"The Office","title":"Dinner Party",
               "player":"Living room TV","progress_percent":"42","transcode_decision":"transcode"},
              {"user":"sam","title":"Dune","year":2021,"platform":"iOS","progress_percent":90,"transcode_decision":"direct play"},
              "garbage"
            ]}
            """);

        var streams = TautulliProvider.ReadSessions(document.RootElement);

        Assert.Equal(
            [
                new NowPlayingStream("Chris", "The Office", "Dinner Party", "Living room TV", 42, true),
                new NowPlayingStream("sam", "Dune", "2021", "iOS", 90, false),
            ],
            streams);
        Assert.Empty(TautulliProvider.ReadSessions(default));
    }
}
