using LabbyTwo.Core;
using LabbyTwo.Services;

namespace LabbyTwo.Tests;

/// <summary>
/// How outages are grouped into incidents, and how a Docker host's container list is read
/// into changes — both pure, so every rule is a line here rather than an outage staged by
/// hand or a Watchtower run waited for.
/// </summary>
public sealed class IncidentRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 14, 0, 0, TimeSpan.Zero);

    private static IncidentSignal Down(string id, DateTimeOffset at, bool maintenance = false) =>
        new(IncidentMember.StatusKey(id), ChangeKinds.Status, id, id.ToUpperInvariant(), true, at, maintenance);

    private static IncidentSignal Up(string id, DateTimeOffset at) =>
        new(IncidentMember.StatusKey(id), ChangeKinds.Status, id, id.ToUpperInvariant(), false, at);

    private static IncidentSignal Fires(string rule, string id, DateTimeOffset at) =>
        new(IncidentMember.AlertKey(rule, id), ChangeKinds.Alert, id, $"{id} · disk above 90", true, at);

    /// <summary>Applies signals in order the way the tracker does, giving each new incident an id.</summary>
    private static List<Incident> Run(params IncidentSignal[] signals)
    {
        var incidents = new List<Incident>();
        foreach (var signal in signals)
        {
            if (IncidentRules.Apply(incidents, signal) is not { } changed)
                continue;
            if (changed.Id == 0)
                changed = changed with { Id = incidents.Count + 1 };
            incidents.RemoveAll(i => i.Id == changed.Id);
            incidents.Add(changed);
        }
        return [.. incidents.OrderBy(i => i.Id)];
    }

    [Fact]
    public void AFailureStartsAnIncident()
    {
        var incident = Assert.Single(Run(Down("nas", T0)));

        Assert.True(incident.IsOpen);
        Assert.Equal(T0, incident.StartedAt);
        Assert.Equal("NAS", incident.Title);
        Assert.False(incident.Maintenance);
    }

    [Fact]
    public void FailuresWithinTheWindowOfTheLastActivityJoinIt()
    {
        // A cascade: each one within fifteen minutes of the one before, though the last is
        // twenty-four minutes after the first.
        var incident = Assert.Single(Run(
            Down("nas", T0),
            Down("plex", T0.AddMinutes(12)),
            Fires("disk", "nas", T0.AddMinutes(24))));

        Assert.Equal(3, incident.Members.Count);
        Assert.Equal("NAS and 2 others", incident.Title);
        Assert.Equal(T0.AddMinutes(24), incident.LastActivity);
    }

    [Fact]
    public void AFailureLongAfterTheLastActivityStartsAnotherEvenWhileOneIsOpen()
    {
        // A disk that has been full for a day must not swallow every outage that follows.
        var incidents = Run(
            Fires("disk", "nas", T0),
            Down("router", T0.AddHours(3)));

        Assert.Equal(2, incidents.Count);
        Assert.All(incidents, i => Assert.True(i.IsOpen));
        Assert.Single(incidents[1].Members);
    }

    [Fact]
    public void AFailureAlreadyCountedChangesNothing()
    {
        var incidents = new List<Incident> { Assert.Single(Run(Down("nas", T0))) with { Id = 1 } };

        Assert.Null(IncidentRules.Apply(incidents, Down("nas", T0.AddMinutes(1))));
    }

    [Fact]
    public void ItEndsWhenTheLastMemberRecovers()
    {
        var incident = Assert.Single(Run(
            Down("nas", T0),
            Down("plex", T0.AddMinutes(2)),
            Up("plex", T0.AddMinutes(5)),
            Up("nas", T0.AddMinutes(20))));

        Assert.False(incident.IsOpen);
        Assert.Equal(T0.AddMinutes(20), incident.EndedAt);
        Assert.Equal(TimeSpan.FromMinutes(20), incident.Duration(T0.AddDays(1)));
        Assert.All(incident.Members, m => Assert.True(m.IsRecovered));
    }

    [Fact]
    public void APartialRecoveryKeepsItOpen()
    {
        var incident = Assert.Single(Run(Down("nas", T0), Down("plex", T0.AddMinutes(2)), Up("plex", T0.AddMinutes(5))));

        Assert.True(incident.IsOpen);
        Assert.Equal("NAS", Assert.Single(incident.StillDown).Name);
    }

    [Fact]
    public void FallingOverAgainSoonAfterReopensTheSameIncident()
    {
        var incident = Assert.Single(Run(
            Down("nas", T0),
            Up("nas", T0.AddMinutes(3)),
            Down("nas", T0.AddMinutes(10))));

        Assert.True(incident.IsOpen);
        Assert.Equal(T0, incident.StartedAt);

        // Two spans of the one service: the first keeps its recovery.
        Assert.Collection(incident.Members.OrderBy(m => m.DownAt),
            first =>
            {
                Assert.Equal(1, first.Span);
                Assert.Equal(T0.AddMinutes(3), first.UpAt);
            },
            second =>
            {
                Assert.Equal(2, second.Span);
                Assert.Equal(T0.AddMinutes(10), second.DownAt);
                Assert.Null(second.UpAt);
            });
        Assert.Equal("NAS", incident.Title);
    }

    [Fact]
    public void AServiceBackIn28SecondsIsNotListedAsDownForHoursWhenItFailsAgainLater()
    {
        // What the NAS showed: NZBGet went down with the update and came back 28 seconds
        // later, while Sonarr stayed down. Later NZBGet failed again, and when that ended the
        // incident said "NZBGet — went down at 21:28, down for 14h 33m".
        var incidents = Run(
            Down("sonarr", T0),
            Down("nzbget", T0.AddSeconds(30)),
            Up("nzbget", T0.AddSeconds(58)),
            Down("nzbget", T0.AddMinutes(5)),
            Up("nzbget", T0.AddMinutes(6)),
            Up("sonarr", T0.AddMinutes(8)));

        var incident = Assert.Single(incidents);
        var nzbget = incident.Members.Where(m => m.ConnectionId == "nzbget").OrderBy(m => m.DownAt).ToList();
        Assert.Equal(2, nzbget.Count);
        Assert.Equal(TimeSpan.FromSeconds(28), nzbget[0].UpAt - nzbget[0].DownAt);
        Assert.Equal(TimeSpan.FromMinutes(1), nzbget[1].UpAt - nzbget[1].DownAt);
        Assert.False(incident.IsOpen);
        Assert.Equal(T0.AddMinutes(8), incident.EndedAt);
    }

    [Fact]
    public void AFailureLongAfterTheLastFailureStartsANewIncidentEvenWhileTheOldOneIsOpen()
    {
        // Sonarr stays down; its members trickle back; three hours later something else fails.
        var incidents = Run(
            Down("sonarr", T0),
            Down("plex", T0.AddMinutes(1)),
            Up("plex", T0.AddMinutes(40)),
            Down("github", T0.AddHours(3)));

        Assert.Equal(2, incidents.Count);
        Assert.True(incidents[0].IsOpen);
        Assert.Equal(["SONARR", "PLEX"], incidents[0].Members.Select(m => m.Name));
        Assert.Equal("GITHUB", Assert.Single(incidents[1].Members).Name);
    }

    [Fact]
    public void ARecoveryDoesNotKeepAnIncidentOpenForNewFailures()
    {
        // Twenty minutes after the last failure, though only five after a recovery: joining
        // is measured from the last failure, so this is a new incident.
        var incidents = Run(
            Down("sonarr", T0),
            Up("sonarr", T0.AddMinutes(15)),
            Down("radarr", T0.AddMinutes(20)));

        Assert.Equal(2, incidents.Count);
    }

    [Fact]
    public void AnIncidentStopsGrowingTwoHoursAfterItStarted()
    {
        // A cascade that keeps spreading every ten minutes: joins until two hours, then not.
        var signals = Enumerable.Range(0, 15).Select(i => Down($"svc{i}", T0.AddMinutes(10 * i))).ToArray();
        var incidents = Run(signals);

        Assert.Equal(2, incidents.Count);
        Assert.Equal(13, incidents[0].Members.Count);   // 0 … 120 minutes
        Assert.Equal(2, incidents[1].Members.Count);    // 130 and 140
        Assert.True(IncidentRules.CanJoin(incidents[0], T0.AddHours(2)));
        Assert.False(IncidentRules.CanJoin(incidents[0], T0.AddHours(2).AddMinutes(1)));
    }

    [Fact]
    public void SpansAreStoredUnderTheirOwnKeysAndReadBack()
    {
        var first = new IncidentMember("status:nzbget", ChangeKinds.Status, "nzbget", "NZBGet", T0, T0.AddSeconds(28));
        var second = first with { DownAt = T0.AddHours(3), UpAt = null, Span = 2 };

        Assert.Equal("status:nzbget", LabbyTwo.Storage.IncidentStore.StoredKey(first));
        Assert.Equal("status:nzbget#span2", LabbyTwo.Storage.IncidentStore.StoredKey(second));
        Assert.Equal(("status:nzbget", 2), LabbyTwo.Storage.IncidentStore.ParseKey("status:nzbget#span2"));
        Assert.Equal(("alert:r1:nas", 1), LabbyTwo.Storage.IncidentStore.ParseKey("alert:r1:nas"));
        Assert.Equal(("status:odd#spanx", 1), LabbyTwo.Storage.IncidentStore.ParseKey("status:odd#spanx"));
    }

    [Fact]
    public void FallingOverAgainMuchLaterIsANewIncident()
    {
        var incidents = Run(
            Down("nas", T0),
            Up("nas", T0.AddMinutes(3)),
            Down("nas", T0.AddHours(2)));

        Assert.Equal(2, incidents.Count);
        Assert.False(incidents[0].IsOpen);
        Assert.True(incidents[1].IsOpen);
    }

    [Fact]
    public void ARecoveryWithNoIncidentChangesNothing()
    {
        Assert.Null(IncidentRules.Apply([], Up("nas", T0)));
    }

    [Fact]
    public void MaintenanceMarksRatherThanDrops()
    {
        var incident = Assert.Single(Run(
            Down("nas", T0, maintenance: true),
            Down("plex", T0.AddMinutes(1))));

        Assert.True(incident.Maintenance);
        Assert.Equal(2, incident.Members.Count);

        // Once marked, it stays marked, whatever joins later.
        Assert.True(Assert.Single(Run(Down("nas", T0), Down("plex", T0.AddMinutes(1), maintenance: true))).Maintenance);
    }

    [Fact]
    public void ReconcileClosesWhatIsFineAndLeavesWhatCannotBeTold()
    {
        var incident = Assert.Single(Run(Down("nas", T0), Fires("disk", "nas", T0.AddMinutes(1)))) with { Id = 1 };
        var later = T0.AddMinutes(30);

        // The alert is known to be over; the status cannot be told yet.
        var partly = IncidentRules.Reconcile(incident, m => m.Kind == ChangeKinds.Alert ? false : null, later);
        Assert.NotNull(partly);
        Assert.True(partly.IsOpen);
        Assert.Equal(later, partly.Members.Single(m => m.Kind == ChangeKinds.Alert).UpAt);

        // Everything fine: closed, at that moment.
        var closed = IncidentRules.Reconcile(partly, _ => false, later.AddMinutes(1));
        Assert.NotNull(closed);
        Assert.Equal(later.AddMinutes(1), closed.EndedAt);

        // Nothing to close is no change at all.
        Assert.Null(IncidentRules.Reconcile(incident, _ => true, later));
        Assert.Null(IncidentRules.Reconcile(closed, _ => false, later));
    }

    [Fact]
    public void TheTimelineReachesBackHalfAnHourAndJustPastTheEnd()
    {
        var incident = new Incident(1, T0, T0.AddMinutes(20), T0.AddMinutes(20), false, []);

        Assert.Equal(T0.AddMinutes(-30), IncidentRules.TimelineFrom(incident));
        Assert.Equal(T0.AddMinutes(25), IncidentRules.TimelineTo(incident, T0.AddDays(1)));
        // Still open: up to now.
        Assert.Equal(T0.AddMinutes(7), IncidentRules.TimelineTo(incident with { EndedAt = null }, T0.AddMinutes(7)));
    }

    [Fact]
    public void ChangesBecomeSignalsOnlyForOutagesAndAlerts()
    {
        var down = IncidentTracker.SignalFor(
            new Change(T0, ChangeKinds.Status, ChangeActions.Down, "nas", "", "QNAP NAS went down", "refused"), maintenance: true);
        Assert.NotNull(down);
        Assert.True(down.Bad);
        Assert.Equal("QNAP NAS", down.Name);
        Assert.Equal(IncidentMember.StatusKey("nas"), down.Key);
        Assert.True(down.Maintenance);

        var cleared = IncidentTracker.SignalFor(
            new Change(T0, ChangeKinds.Alert, ChangeActions.Cleared, "nas", "rule1", "Alert cleared: NAS · Disk above 90"), false);
        Assert.NotNull(cleared);
        Assert.False(cleared.Bad);
        Assert.Equal("NAS · Disk above 90", cleared.Name);
        Assert.Equal(IncidentMember.AlertKey("rule1", "nas"), cleared.Key);

        Assert.Null(IncidentTracker.SignalFor(
            new Change(T0, ChangeKinds.Container, ChangeActions.Restarted, "docker", "plex", "plex restarted"), false));
    }

    // ---------- containers ----------

    private static ContainerSnapshot Snap(string name, string id = "c1", string imageId = "sha256:aaaaaaaaaaaaaaaa", string state = "running") =>
        new(name, id, "lscr.io/linuxserver/plex:latest", imageId, state);

    private static Dictionary<string, ContainerSnapshot> Before(params ContainerSnapshot[] containers) =>
        containers.ToDictionary(c => c.Name);

    [Fact]
    public void NewAndVanishedContainersAreCreatedAndRemoved()
    {
        var changes = ContainerChanges.Diff(Before(Snap("old")), [(Snap("new", "c2"), "Up 5 seconds")], TimeSpan.FromSeconds(30));

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c is { Action: ChangeActions.Created, Name: "new", Title: "new was created" });
        Assert.Contains(changes, c => c is { Action: ChangeActions.Removed, Name: "old" });
    }

    [Fact]
    public void ANewImageUnderTheSameNameIsAnUpdate()
    {
        var changes = ContainerChanges.Diff(Before(Snap("plex")),
            [(Snap("plex", "c2", "sha256:bbbbbbbbbbbbbbbbbb"), "Up 3 seconds")], TimeSpan.FromSeconds(30));

        var change = Assert.Single(changes);
        Assert.Equal(ChangeActions.Image, change.Action);
        Assert.Equal("plex is on a new image", change.Title);
        Assert.Contains("was aaaaaaaaaaaa, now bbbbbbbbbbbb", change.Detail);
    }

    [Fact]
    public void ANewContainerOnTheSameImageIsARecreate()
    {
        var change = Assert.Single(ContainerChanges.Diff(Before(Snap("plex")), [(Snap("plex", "c2"), "Up 3 seconds")], null));
        Assert.Equal(ChangeActions.Recreated, change.Action);
    }

    [Theory]
    [InlineData("running", "exited", "Exited (137) 5 seconds ago", ChangeActions.Stopped, "code 137")]
    [InlineData("running", "exited", "Exited (0) 5 seconds ago", ChangeActions.Stopped, "cleanly")]
    [InlineData("exited", "running", "Up 4 seconds", ChangeActions.Started, "")]
    [InlineData("running", "restarting", "Restarting (1) 2 seconds ago", ChangeActions.Restarted, "restart policy")]
    [InlineData("running", "paused", "Up 2 hours (Paused)", ChangeActions.Paused, "")]
    [InlineData("paused", "running", "Up 2 hours", ChangeActions.Unpaused, "")]
    public void StateChangesAreNamed(string was, string now, string status, string action, string detail)
    {
        var change = Assert.Single(ContainerChanges.Diff(Before(Snap("plex", state: was)), [(Snap("plex", state: now), status)], TimeSpan.FromSeconds(30)));

        Assert.Equal(action, change.Action);
        Assert.Contains(detail, change.Detail);
    }

    [Fact]
    public void ARestartBetweenLooksIsCaughtFromTheUptime()
    {
        // Running at both looks, same container — but up for less than the time between them.
        var restarted = ContainerChanges.Diff(Before(Snap("plex")), [(Snap("plex"), "Up 12 seconds (healthy)")], TimeSpan.FromSeconds(30));
        Assert.Equal(ChangeActions.Restarted, Assert.Single(restarted).Action);

        // Up for longer than that: nothing happened.
        Assert.Empty(ContainerChanges.Diff(Before(Snap("plex")), [(Snap("plex"), "Up 3 hours")], TimeSpan.FromSeconds(30)));
        Assert.Empty(ContainerChanges.Diff(Before(Snap("plex")), [(Snap("plex"), "Up 29 seconds")], TimeSpan.FromSeconds(30)));

        // After a restart of LabbyTwo the gap is unknown, so the uptime proves nothing.
        Assert.Empty(ContainerChanges.Diff(Before(Snap("plex")), [(Snap("plex"), "Up 2 seconds")], null));
    }

    [Theory]
    [InlineData("Up Less than a second", 1)]
    [InlineData("Up 1 second", 2)]
    [InlineData("Up 45 seconds", 46)]
    [InlineData("Up About a minute", 120)]
    [InlineData("Up 5 minutes (healthy)", 360)]
    [InlineData("Up About an hour", 5400)]
    [InlineData("Up 3 hours (unhealthy)", 12600)]
    [InlineData("Up 2 days", 259200)]
    public void UptimeIsReadAsTheMostItCouldBe(string status, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), ContainerChanges.UptimeAtMost(status));
    }

    [Theory]
    [InlineData("Exited (0) 2 days ago")]
    [InlineData("Created")]
    [InlineData("Up for a while")]
    [InlineData("")]
    public void AnythingElseIsNoUptime(string status) => Assert.Null(ContainerChanges.UptimeAtMost(status));

    [Fact]
    public void ABaselineRoundTripsAndIsOnlyRewrittenWhenSomethingItKeepsChanged()
    {
        var containers = new[] { Snap("plex"), Snap("sonarr", "c2") };
        var read = ContainerChanges.Deserialise(ContainerChanges.Serialise(containers));

        Assert.NotNull(read);
        Assert.True(ContainerChanges.SameAs(read, containers));
        Assert.False(ContainerChanges.SameAs(read, [Snap("plex"), Snap("sonarr", "c3")]));
        Assert.Null(ContainerChanges.Deserialise("not json"));
    }

    // ---------- certificates ----------

    private static Dictionary<string, string> Cert(string serial, DateTimeOffset notAfter, string issuer = "R11") => new()
    {
        [Providers.CertificateProvider.SerialDetail] = serial,
        [Providers.CertificateProvider.NotAfterDetail] = notAfter.ToString("o"),
        [Providers.CertificateProvider.IssuerDetail] = issuer,
    };

    [Fact]
    public void ACertificateIsRememberedFirstThenARenewalOrReplacementIsRecorded()
    {
        var connection = new Connection { Id = "c", Name = "cloud.example.com", Provider = "certificate" };

        var (first, none) = ChangeWatcher.CertificateChange(connection, null, Cert("01", T0.AddDays(30)), T0);
        Assert.Null(none);

        var (same, unchanged) = ChangeWatcher.CertificateChange(connection, first, Cert("01", T0.AddDays(30)), T0);
        Assert.Null(unchanged);
        Assert.Equal(first, same);

        var (renewedBaseline, renewed) = ChangeWatcher.CertificateChange(connection, first, Cert("02", T0.AddDays(90)), T0);
        Assert.NotNull(renewed);
        Assert.Equal(ChangeActions.Renewed, renewed.Action);
        Assert.Equal("cloud.example.com's certificate was renewed", renewed.Title);
        Assert.NotEqual(first, renewedBaseline);

        // Another issuer is not a renewal, whatever the dates say.
        var (_, replaced) = ChangeWatcher.CertificateChange(connection, renewedBaseline, Cert("03", T0.AddDays(365), "Traefik default"), T0);
        Assert.NotNull(replaced);
        Assert.Equal(ChangeActions.Replaced, replaced.Action);
        Assert.Contains("came from R11", replaced.Detail);
    }

    // ---------- shortcode options ----------

    private static readonly IReadOnlyList<Connection> Connections =
    [
        new() { Id = "nas", Name = "QNAP NAS" },
        new() { Id = "plex", Name = "Plex" },
    ];

    private static Shortcode Code(string text) => Shortcodes.Parse(text)!;

    [Theory]
    [InlineData("{{changes}}", "changes")]
    [InlineData("{{incidents}}", "incidents")]
    [InlineData("{{incidents: open}}", "incidents")]
    [InlineData("{{changes: containers last=7d limit=5}}", "changes")]
    public void BothAreBlocksThatMayBeWrittenBare(string text, string kind)
    {
        var code = Code(text);
        Assert.Equal(kind, code.Kind);
        Assert.True(code.IsBlock);
        Assert.True(code.IsKnown);
    }

    [Fact]
    public void ChangesDefaultsToADayOfEverythingTenLines()
    {
        var options = ChangeLists.Changes(Code("{{changes}}"), Connections, out var problem);

        Assert.Null(problem);
        Assert.NotNull(options);
        Assert.Equal(TimeSpan.FromHours(24), options.Window);
        Assert.Empty(options.Kinds);
        Assert.Equal(10, options.Limit);
        Assert.Null(options.Only);
    }

    [Fact]
    public void ChangesReadsKindsWindowLimitAndConnections()
    {
        var options = ChangeLists.Changes(Code("{{changes: containers kind=\"alerts, certs\" last=2w limit=500 only=\"QNAP NAS\"}}"),
            Connections, out var problem);

        Assert.Null(problem);
        Assert.NotNull(options);
        Assert.Equal([ChangeKinds.Container, ChangeKinds.Alert, ChangeKinds.Certificate], options.Kinds);
        Assert.Equal(TimeSpan.FromDays(14), options.Window);
        Assert.Equal(100, options.Limit);
        Assert.Equal(["nas"], options.Only!);
    }

    [Theory]
    [InlineData("{{changes: gremlins}}", "not a kind of change")]
    [InlineData("{{changes: last=forever}}", "not a length of time")]
    [InlineData("{{changes: last=2y}}", "not a length of time")]
    [InlineData("{{changes: last=400d}}", "outside what is kept")]
    [InlineData("{{changes: limit=lots}}", "not a number of lines")]
    [InlineData("{{changes: only=Nobody}}", "No connection called")]
    [InlineData("{{incidents: closed}}", "Use open or all")]
    [InlineData("{{incidents: last=1s}}", "not a length of time")]
    public void AMistakeIsSaidRatherThanGuessed(string text, string expected)
    {
        var code = Code(text);
        string? problem;
        if (code.Kind == "changes")
            Assert.Null(ChangeLists.Changes(code, Connections, out problem));
        else
            Assert.Null(ChangeLists.Incidents(code, out problem));
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void IncidentsDefaultsToAMonthAndFiveAndOpenNarrowsIt()
    {
        var all = ChangeLists.Incidents(Code("{{incidents}}"), out _);
        Assert.NotNull(all);
        Assert.False(all.OpenOnly);
        Assert.Equal(TimeSpan.FromDays(30), all.Window);
        Assert.Equal(5, all.Limit);

        var open = ChangeLists.Incidents(Code("{{incidents: open limit=2}}"), out _);
        Assert.NotNull(open);
        Assert.True(open.OpenOnly);
        Assert.Equal(2, open.Limit);
    }

    [Fact]
    public void NamingConnectionsLeavesOutChangesThatBelongToNone()
    {
        IReadOnlyList<Change> changes =
        [
            new(T0, ChangeKinds.Status, ChangeActions.Down, "nas", "", "QNAP NAS went down"),
            new(T0, ChangeKinds.Update, ChangeActions.Updated, null, "labbytwo", "LabbyTwo updated to v2"),
            new(T0, ChangeKinds.Status, ChangeActions.Down, "plex", "", "Plex went down"),
        ];

        Assert.Equal(3, ChangeLists.Narrow(changes, null, 10).Count);
        Assert.Equal("QNAP NAS went down", Assert.Single(ChangeLists.Narrow(changes, ["nas"], 10)).Title);
        Assert.Single(ChangeLists.Narrow(changes, null, 1));
    }

    [Fact]
    public void AnIncidentSaysWhenAndHowLong()
    {
        var now = new DateTimeOffset(2026, 9, 29, 15, 0, 0, TimeSpan.FromHours(1));
        var start = now.AddMinutes(-55);
        var closed = new Incident(1, start, start.AddMinutes(12), start.AddMinutes(12), false,
            [new IncidentMember("status:nas", ChangeKinds.Status, "nas", "NAS", start, start.AddMinutes(12))]);

        Assert.EndsWith("back after 12m", ChangeLists.IncidentWhen(closed, now));
        Assert.EndsWith("ongoing — 55m so far", ChangeLists.IncidentWhen(closed with { EndedAt = null }, now));
        Assert.Equal("down for 12m", ChangeLists.MemberWhen(closed.Members[0], now));
        Assert.Equal("still down, 55m so far", ChangeLists.MemberWhen(closed.Members[0] with { UpAt = null }, now));
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(600, "10m")]
    [InlineData(3600 * 3 + 300, "3h 5m")]
    [InlineData(86400 * 2 + 3600 * 4, "2d 4h")]
    public void DurationsReadInTwoUnits(int seconds, string expected) =>
        Assert.Equal(expected, Ago.Duration(TimeSpan.FromSeconds(seconds)));
}
