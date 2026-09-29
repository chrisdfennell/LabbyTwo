using LabbyTwo.Core;
using LabbyTwo.Services;

namespace LabbyTwo.Tests;

/// <summary>
/// "Probably caused by" and the write-up an incident becomes — both pure, so every rule is
/// an outage described in a few lines here rather than one staged on a real lab.
/// </summary>
public sealed class ProbableCauseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 14, 0, 0, TimeSpan.Zero);

    private static long _ids;

    private static CauseConnection Conn(string id, string? parent = null, string provider = "http", params string[] hosts) =>
        new(id, id.ToUpperInvariant() == id ? id : char.ToUpperInvariant(id[0]) + id[1..], provider, parent, hosts);

    private static IncidentMember Down(string id, DateTimeOffset at, DateTimeOffset? up = null) =>
        new(IncidentMember.StatusKey(id), ChangeKinds.Status, id, char.ToUpperInvariant(id[0]) + id[1..], at, up);

    private static IncidentMember Fired(string rule, string id, DateTimeOffset at) =>
        new(IncidentMember.AlertKey(rule, id), ChangeKinds.Alert, id, $"{id} · disk above 90%", at, null);

    private static Incident Of(params IncidentMember[] members)
    {
        var start = members.Min(m => m.DownAt);
        var last = members.Max(m => m.UpAt ?? m.DownAt);
        return new Incident(1, start, members.All(m => m.UpAt is not null) ? last : null, last, false, members);
    }

    private static Change Changed(DateTimeOffset at, string kind, string action, string? connection, string subject = "", string title = "", string detail = "") =>
        new(at, kind, action, connection, subject, title.Length > 0 ? title : $"{subject} {action}", detail) { Id = Interlocked.Increment(ref _ids) };

    private static Change WentDown(string id, DateTimeOffset at, string detail = "Connection refused") =>
        Changed(at, ChangeKinds.Status, ChangeActions.Down, id, "", $"{id} went down", detail);

    // ---------- a shared parent ----------

    [Fact]
    public void ServicesBehindOneThatWentDownFirstAreBlamedOnIt()
    {
        var lab = new CauseLab([
            Conn("router"),
            Conn("nas", "router"),
            Conn("plex", "nas"), Conn("sonarr", "nas"), Conn("radarr", "nas"), Conn("photos", "nas"),
        ]);
        var incident = Of(
            Down("nas", T0),
            Down("plex", T0.AddSeconds(30)), Down("sonarr", T0.AddSeconds(30)),
            Down("radarr", T0.AddMinutes(1)), Down("photos", T0.AddMinutes(2)));
        var feed = new[] { WentDown("nas", T0), WentDown("plex", T0.AddSeconds(30)) };

        var causes = IncidentCauses.Explain(incident, feed, lab);

        var first = causes[0];
        Assert.Equal(CauseKinds.SharedParent, first.Kind);
        Assert.Equal(CauseConfidence.High, first.Confidence);
        Assert.Equal("4 services failed together; they all depend on Nas, which went down first.", first.Sentence);
        Assert.Equal("nas went down", Assert.Single(first.Evidence).Title);
    }

    [Fact]
    public void TheFurthestUpParentThatIsDownWinsAndAChildTransitivelyBehindItCounts()
    {
        var lab = new CauseLab([Conn("router"), Conn("nas", "router"), Conn("plex", "nas")]);
        var incident = Of(Down("router", T0), Down("nas", T0.AddSeconds(20)), Down("plex", T0.AddSeconds(40)));

        var cause = IncidentCauses.Explain(incident, [], lab)[0];

        Assert.Equal("2 services failed together; they all depend on Router, which went down first.", cause.Sentence);
    }

    [Fact]
    public void AParentThatWentDownAfterItsChildIsNotItsCause()
    {
        var lab = new CauseLab([Conn("nas"), Conn("plex", "nas")]);
        var incident = Of(Down("plex", T0), Down("nas", T0.AddMinutes(3)));

        Assert.DoesNotContain(IncidentCauses.Explain(incident, [], lab), c => c.Kind == CauseKinds.SharedParent);
    }

    [Fact]
    public void OneChildIsNamed()
    {
        var lab = new CauseLab([Conn("vpn"), Conn("plex", "vpn")]);
        var cause = IncidentCauses.Explain(Of(Down("vpn", T0), Down("plex", T0)), [], lab)[0];

        Assert.Equal("Plex depends on Vpn, which went down at the same moment.", cause.Sentence);
    }

    [Fact]
    public void ALoopInTheDependenciesDoesNotHangOrBlameAnything()
    {
        var lab = new CauseLab([Conn("a", "b"), Conn("b", "a")]);

        // The loop is broken at one link, as the dependency map breaks it, so at most one of
        // them is put down to the other — and it finishes.
        Assert.True(IncidentCauses.Explain(Of(Down("a", T0), Down("b", T0.AddSeconds(5))), [], lab).Count <= 1);
    }

    // ---------- the service's own container ----------

    [Fact]
    public void AServiceWhoseContainerGotANewImageJustBeforeIsBlamedOnIt()
    {
        var lab = new CauseLab([Conn("plex", hosts: "plex")]);
        var image = Changed(T0.AddMinutes(-2), ChangeKinds.Container, ChangeActions.Image, "docker", "plex", "plex is on a new image");

        var cause = Assert.Single(IncidentCauses.Explain(Of(Down("plex", T0)), [image], lab));

        Assert.Equal(CauseKinds.Container, cause.Kind);
        Assert.Equal(CauseConfidence.High, cause.Confidence);
        Assert.Equal("Plex went down 2 minutes after it got a new image.", cause.Sentence);
        Assert.Same(image, Assert.Single(cause.Evidence));
    }

    [Fact]
    public void AContainerNamedOtherwiseIsNamedAndARestartIsOnlyPossible()
    {
        var lab = new CauseLab([Conn("media", hosts: "jellyfin")]);
        var restart = Changed(T0.AddSeconds(-40), ChangeKinds.Container, ChangeActions.Restarted, "docker", "jellyfin");

        var cause = Assert.Single(IncidentCauses.Explain(Of(Down("media", T0)), [restart], lab));

        Assert.Equal("Media went down 40 seconds after its container jellyfin restarted.", cause.Sentence);
        Assert.Equal(CauseConfidence.Medium, cause.Confidence);
    }

    [Fact]
    public void AContainerChangeSeenOnTheSameSweepIsJustAs()
    {
        var lab = new CauseLab([Conn("plex", hosts: "plex")]);
        var stop = Changed(T0.AddSeconds(30), ChangeKinds.Container, ChangeActions.Stopped, "docker", "plex");

        Assert.Equal("Plex went down just as it was stopped.", IncidentCauses.Explain(Of(Down("plex", T0)), [stop], lab)[0].Sentence);
    }

    [Fact]
    public void ContainerChangesTooLongBeforeOrToOtherContainersAreIgnored()
    {
        var lab = new CauseLab([Conn("plex", hosts: "plex")]);
        var feed = new[]
        {
            Changed(T0 - IncidentCauses.Window - TimeSpan.FromMinutes(1), ChangeKinds.Container, ChangeActions.Image, "docker", "plex"),
            Changed(T0.AddMinutes(-1), ChangeKinds.Container, ChangeActions.Image, "docker", "sonarr"),
            Changed(T0.AddMinutes(-1), ChangeKinds.Container, ChangeActions.Started, "docker", "plex"),
            Changed(T0.AddMinutes(5), ChangeKinds.Container, ChangeActions.Restarted, "docker", "plex"),
        };

        Assert.Empty(IncidentCauses.Explain(Of(Down("plex", T0)), feed, lab));
    }

    // ---------- one host ----------

    [Fact]
    public void EverythingOnOneAddressFailingTogetherIsTheHost()
    {
        var lab = new CauseLab([
            Conn("pihole", hosts: "192.168.86.10"), Conn("homeassistant", hosts: "192.168.86.10"),
            Conn("grafana", hosts: "192.168.86.10"), Conn("router", hosts: "192.168.86.1"),
        ]);
        var incident = Of(Down("pihole", T0), Down("homeassistant", T0.AddSeconds(25)), Down("grafana", T0.AddSeconds(40)));

        var cause = IncidentCauses.Explain(incident, [WentDown("pihole", T0)], lab)[0];

        Assert.Equal(CauseKinds.Host, cause.Kind);
        Assert.Equal(CauseConfidence.High, cause.Confidence);
        Assert.Equal("Everything on 192.168.86.10 failed within 40 seconds — likely the host or the network to it.", cause.Sentence);
    }

    [Fact]
    public void SomeOfAHostIsCountedAndFailuresFarApartAreNotTheHost()
    {
        var lab = new CauseLab([Conn("a", hosts: "10.0.0.5"), Conn("b", hosts: "10.0.0.5"), Conn("c", hosts: "10.0.0.5")]);

        var some = IncidentCauses.Explain(Of(Down("a", T0), Down("b", T0)), [], lab)[0];
        Assert.Equal("2 services on 10.0.0.5 failed at the same moment — likely the host or the network to it.", some.Sentence);
        Assert.Equal(CauseConfidence.Medium, some.Confidence);

        Assert.Empty(IncidentCauses.Explain(Of(Down("a", T0), Down("b", T0.AddMinutes(4))), [], lab));
    }

    [Fact]
    public void MembersExplainedByAParentAreNotAlsoPutDownToTheirHost()
    {
        var lab = new CauseLab([Conn("nas", hosts: "10.0.0.2"), Conn("plex", "nas", hosts: "10.0.0.9"), Conn("sonarr", "nas", hosts: "10.0.0.9")]);
        var causes = IncidentCauses.Explain(Of(Down("nas", T0), Down("plex", T0.AddSeconds(10)), Down("sonarr", T0.AddSeconds(10))), [], lab);

        Assert.Equal(CauseKinds.SharedParent, Assert.Single(causes).Kind);
    }

    // ---------- certificates, DNS, LabbyTwo ----------

    [Fact]
    public void AnExpiredCertificateIsSaidFromWhatTheCheckReported()
    {
        var lab = new CauseLab([Conn("site", provider: "certificate", hosts: "example.lan")]);
        var down = WentDown("site", T0, "Expired 2 days ago — Let's Encrypt.");

        var cause = IncidentCauses.Explain(Of(Down("site", T0)), [down], lab)[0];

        Assert.Equal(CauseKinds.CertificateExpired, cause.Kind);
        Assert.Equal("The certificate Site checks has expired.", cause.Sentence);
    }

    [Fact]
    public void ACertificateReplacedOnTheSameHostJustBeforeIsPossible()
    {
        var lab = new CauseLab([Conn("cert", provider: "certificate", hosts: "home.lan"), Conn("ha", hosts: "home.lan")]);
        var replaced = Changed(T0.AddMinutes(-6), ChangeKinds.Certificate, ChangeActions.Replaced, "cert", "", "Cert's certificate was replaced");

        var cause = Assert.Single(IncidentCauses.Explain(Of(Down("ha", T0)), [replaced], lab));

        Assert.Equal(CauseConfidence.Medium, cause.Confidence);
        Assert.Equal("Ha went down 6 minutes after the certificate on Cert was replaced.", cause.Sentence);
    }

    [Fact]
    public void ADnsAnswerForTheServicesOwnNameIsPossibleAndAnyOtherIsOnlyWorthALook()
    {
        var lab = new CauseLab([Conn("ha", hosts: "ha.lan")]);
        var own = Changed(T0.AddMinutes(-20), ChangeKinds.Dns, ChangeActions.Changed, null, "ha.lan");
        var other = Changed(T0.AddMinutes(-3), ChangeKinds.Dns, ChangeActions.Changed, null, "printer.lan");

        var causes = IncidentCauses.Explain(Of(Down("ha", T0)), [own, other], lab);

        Assert.Equal("ha.lan started resolving to different addresses 20 minutes before Ha went down.", causes[0].Sentence);
        Assert.Equal(CauseConfidence.Medium, causes[0].Confidence);
        Assert.Equal(CauseConfidence.Low, causes[1].Confidence);
    }

    [Fact]
    public void LabbyTwoUpdatingJustBeforeIsMentioned()
    {
        var update = Changed(T0.AddMinutes(-3), ChangeKinds.Update, ChangeActions.Updated, null, "labbytwo", "LabbyTwo updated to 2.4.0");

        var cause = Assert.Single(IncidentCauses.Explain(Of(Down("nas", T0)), [update], CauseLab.Empty));

        Assert.Equal(CauseKinds.SelfUpdate, cause.Kind);
        Assert.StartsWith("LabbyTwo itself was updated 3 minutes before this started", cause.Sentence);
    }

    // ---------- alerts ----------

    [Fact]
    public void ADiskAlertOnADiskThatHasBeenFillingIsSaidToBeSlow()
    {
        var incident = Of(Fired("disk", "nas", T0));
        var filling = new CapacityForecast(ForecastState.Filling, 91, 20, 0.4, ForecastConfidence.High, 0.9, TimeSpan.FromDays(14), null);
        var forecasts = new Dictionary<string, CauseForecast> { [IncidentMember.AlertKey("disk", "nas")] = new("Disk used on NAS", filling) };

        var cause = Assert.Single(IncidentCauses.Explain(incident, [], CauseLab.Empty, forecasts));

        Assert.Equal(CauseKinds.Capacity, cause.Kind);
        Assert.Equal("Not a sudden fault: Disk used on NAS has been rising steadily and will be full in about 3 weeks at this rate. It needs room made, not a restart.",
            cause.Sentence);

        var flat = forecasts.ToDictionary(p => p.Key, p => p.Value with { Forecast = filling with { State = ForecastState.NotFilling } });
        Assert.Empty(IncidentCauses.Explain(incident, [], CauseLab.Empty, flat));
    }

    // ---------- nothing ----------

    [Fact]
    public void NothingThatFitsIsNoObviousCause()
    {
        var lab = new CauseLab([Conn("nas", hosts: "10.0.0.2"), Conn("plex", hosts: "plex")]);
        var feed = new[]
        {
            WentDown("nas", T0),
            Changed(T0.AddHours(-3), ChangeKinds.Container, ChangeActions.Image, "docker", "plex"),
            Changed(T0.AddMinutes(-1), ChangeKinds.Device, ChangeActions.Appeared, "scan", "", "A new device on Scan"),
        };

        var causes = IncidentCauses.Explain(Of(Down("nas", T0), Down("plex", T0.AddMinutes(8))), feed, lab);

        Assert.Empty(causes);
        Assert.Equal(IncidentCauses.NoObviousCause, IncidentCauses.Headline(causes));
    }

    [Fact]
    public void CausesAreRankedSurestFirstAndCapped()
    {
        var lab = new CauseLab([Conn("nas"), Conn("plex", "nas", hosts: "plex"), Conn("sonarr", "nas", hosts: "sonarr")]);
        var feed = new[]
        {
            Changed(T0.AddMinutes(-10), ChangeKinds.Update, ChangeActions.Updated, null, "labbytwo", "LabbyTwo updated"),
            Changed(T0.AddMinutes(-9), ChangeKinds.Dns, ChangeActions.Changed, null, "printer.lan"),
            Changed(T0.AddMinutes(-2), ChangeKinds.Container, ChangeActions.Restarted, "docker", "plex"),
            Changed(T0.AddMinutes(-2), ChangeKinds.Container, ChangeActions.Image, "docker", "sonarr"),
        };

        var causes = IncidentCauses.Explain(Of(Down("nas", T0), Down("plex", T0.AddSeconds(5)), Down("sonarr", T0.AddSeconds(5))), feed, lab);

        Assert.Equal(IncidentCauses.MaxCauses, causes.Count);
        Assert.Equal([CauseKinds.SharedParent, CauseKinds.Container, CauseKinds.Container, CauseKinds.SelfUpdate], causes.Select(c => c.Kind));
        Assert.Equal(CauseConfidence.High, causes[1].Confidence);
        Assert.True(causes.Zip(causes.Skip(1)).All(pair => pair.First.Confidence >= pair.Second.Confidence));
    }

    [Fact]
    public void TheLabIsBuiltFromWhereConnectionsPoint()
    {
        var lab = ProbableCauses.LabFrom([
            new Connection { Id = "plex", Name = "Plex", Provider = "plex", Settings = new SettingsBag { ["url"] = "http://plex:32400", ["token"] = "abc def" } },
            new Connection { Id = "nas", Name = "NAS", Provider = "qnap", DependsOn = "router", Settings = new SettingsBag { ["host"] = "192.168.1.5:8080" } },
        ]);

        Assert.Equal(["plex"], lab.Get("plex")!.Hosts);
        Assert.Equal(["192.168.1.5"], lab.Get("nas")!.Hosts);
        // A parent that does not exist is not something to blame.
        Assert.Empty(lab.Ancestors("nas"));
    }

    // ---------- the write-up ----------

    [Fact]
    public void AWriteUpHasEverySectionAndLiveShortcodesForTheRunbook()
    {
        var incident = Of(Down("nas", T0, T0.AddMinutes(12)), Down("plex", T0.AddMinutes(1), T0.AddMinutes(10))) with { Id = 42 };
        var cause = new ProbableCause(CauseKinds.SharedParent, CauseConfidence.High, "Plex depends on NAS, which went down first.",
            [WentDown("nas", T0)]);
        var timeline = new[] { Changed(T0.AddMinutes(-3), ChangeKinds.Container, ChangeActions.Restarted, "docker", "qnap", "qnap restarted") };
        var connections = new Dictionary<string, WriteUpConnection>
        {
            ["nas"] = new("QNAP NAS", "restart"),
            ["plex"] = new("Plex"),
        };

        var note = IncidentWriteUp.Build(incident, [cause], timeline, connections, T0.AddHours(1));

        Assert.StartsWith("Incident: Nas and Plex — ", note.Title);
        foreach (var heading in new[] { "## What happened", "## What failed", "## Probable cause", "## Timeline", "## What fixed it", "## Next time", "## Right now" })
            Assert.Contains(heading, note.Markdown);
        Assert.Contains("[this incident](incidents#incident-42)", note.Markdown);
        Assert.Contains("- **Lasted:** 12m", note.Markdown);
        Assert.Contains("Plex depends on NAS, which went down first. *(likely)*", note.Markdown);
        Assert.Contains("(−3m) qnap restarted", note.Markdown);
        Assert.Contains("{{status: QNAP NAS}}", note.Markdown);
        Assert.Contains("{{button: QNAP NAS / restart}}", note.Markdown);
        Assert.DoesNotContain("{{button: Plex", note.Markdown);
        Assert.Contains("{{changes: last=\"24h\" only=\"QNAP NAS, Plex\"}}", note.Markdown);

        // Every shortcode in it is one written here, and each reads back as it was meant.
        var codes = Shortcodes.Find(note.Markdown).Select(f => f.Code).ToList();
        Assert.All(codes, c => Assert.True(c.IsKnown));
        Assert.Contains(codes, c => c.Kind == "button" && c.Part(0) == "QNAP NAS" && c.Part(1) == "restart");
    }

    [Fact]
    public void NamesAndMessagesInAWriteUpStayText()
    {
        var hostile = "my_nas, *arr | {{button: Router / reboot}} <b>x</b> [link](http://evil)";
        var incident = Of(new IncidentMember("status:x", ChangeKinds.Status, "x", hostile, T0, null)) with { Id = 7 };
        var timeline = new[] { Changed(T0, ChangeKinds.Status, ChangeActions.Down, "x", "", $"{hostile} went down", "line one\nline {{status: two}}") };
        var connections = new Dictionary<string, WriteUpConnection> { ["x"] = new(hostile) };

        var note = IncidentWriteUp.Build(incident, [], timeline, connections, T0.AddMinutes(5));

        // The only live shortcodes are the ones written for the runbook, naming the connection
        // exactly, quoted — none from inside the name or the message.
        var codes = Shortcodes.Find(note.Markdown).Select(f => f.Code).ToList();
        Assert.All(codes, c => Assert.Contains(c.Kind, new[] { "status", "ago", "changes" }));
        Assert.Contains(codes, c => c.Kind == "status" && c.Part(0) == hostile);
        Assert.DoesNotContain(codes, c => c.Kind == "button");
        // A name with a comma in it cannot be listed in only=, so the feed is left unnarrowed rather than broken.
        Assert.DoesNotContain(codes, c => c.Kind == "changes" && c.Options.ContainsKey("only"));

        Assert.Contains(@"my\_nas, \*arr \| \{\{button: Router / reboot\}\} \<b\>x\</b\> \[link\](http://evil)", note.Markdown);
        Assert.Contains("line one line", note.Markdown);
        Assert.Contains("still going when this was written", note.Markdown);
        Assert.Contains(IncidentCauses.NoObviousCause, note.Markdown);

        // And rendered, the name is text, not markup. The shortcodes are taken out first, as
        // the live renderer takes them out before Markdown sees the rest.
        var text = note.Markdown;
        foreach (var found in Shortcodes.Find(note.Markdown).Reverse())
            text = text.Remove(found.Index, found.Length);
        var html = new Markdown().ToHtml(text);
        Assert.DoesNotContain("<b>x</b>", html);
        Assert.DoesNotContain("<em>arr", html);
        Assert.DoesNotContain(">link</a>", html);
    }

    [Fact]
    public void RestartIsTheActionWorthAButton()
    {
        Assert.Equal("restart", IncidentWriteUps.RestartAction([new("wake", "Wake"), new("restart", "Restart")]));
        Assert.Equal("reboot", IncidentWriteUps.RestartAction([new("reboot", "Restart the NAS")]));
        Assert.Null(IncidentWriteUps.RestartAction([new("restart", "Restart") { Dangerous = true }]));
        Assert.Null(IncidentWriteUps.RestartAction([new("wake", "Wake")]));
    }
}
