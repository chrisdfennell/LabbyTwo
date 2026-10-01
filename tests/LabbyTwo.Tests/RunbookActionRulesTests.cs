using LabbyTwo.Core;
using LabbyTwo.Services;

namespace LabbyTwo.Tests;

/// <summary>
/// The rules behind the runbook action shortcodes, with no lab behind them: the copy buttons
/// Markdown grows, how <c>{{ssh}}</c>, <c>{{logs}}</c> and <c>{{run}}</c> are read, which
/// commands are dangerous, and what is kept of a command's output.
/// </summary>
public sealed class RunbookActionRulesTests
{
    private readonly Markdown _markdown = new();

    private static Shortcode Code(string source) => Shortcodes.Parse(source) ?? throw new InvalidOperationException(source);

    // ---------- copy buttons ----------

    [Fact]
    public void ACodeBlockGetsACopyButtonInsideAWrapper()
    {
        var html = _markdown.ToHtml("```powershell\nGet-Service NTDS\n```\n");

        Assert.Contains("<div class=\"md-code\">\n<pre><code class=\"language-powershell\">Get-Service NTDS\n</code></pre>", html);
        Assert.Contains(CopyButtonExtension.Button + "</div>", html);
    }

    [Fact]
    public void ACodeSpanGetsASmallButtonBesideIt()
    {
        var html = _markdown.ToHtml("Run `dcdiag /v` first.");

        Assert.Equal("<p>Run <span class=\"md-code-inline\"><code>dcdiag /v</code>" + CopyButtonExtension.Button + "</span> first.</p>\n", html);
    }

    [Fact]
    public void CodeInsideALinkOrAnImageGetsNoButton()
    {
        Assert.DoesNotContain("md-copy", _markdown.ToHtml("[`docs`](https://example.com)"));
        Assert.DoesNotContain("md-copy", _markdown.ToHtml("![`x`](https://example.com/a.png)"));
    }

    [Fact]
    public void ADiagramIsNotCode()
    {
        var html = _markdown.ToHtml("```mermaid\ngraph TD; A-->B;\n```\n");

        Assert.Contains("class=\"mermaid\"", html);
        Assert.DoesNotContain("md-copy", html);
    }

    [Fact]
    public void CopyButtonsLeaveNumberedListsAndTablesAsTheyWere()
    {
        var html = _markdown.ToHtml("""
            1. Check: `Get-Service NTDS,Netlogon,Kdc,DNS`
            2. Then: `dcdiag /v`
            3. Done.

            | Command | Does |
            |---|---|
            | `repadmin /replsummary` | replication |
            """);

        // One list of three items, not three lists or a list broken by a block.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<ol>"));
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "<li>").Count);
        Assert.Contains("<li>Check: <span class=\"md-code-inline\"><code>Get-Service NTDS,Netlogon,Kdc,DNS</code>", html);
        Assert.Contains("<td><span class=\"md-code-inline\"><code>repadmin /replsummary</code>", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<table>"));
    }

    [Fact]
    public void CodeIsStillEscapedAndNothingInItBecomesMarkup()
    {
        var html = _markdown.ToHtml("`<script>alert(1)</script>`\n\n```\n<img src=x onerror=alert(1)>\n```\n");

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void AShortcodeInCodeIsStillAnExampleWithAButton()
    {
        var document = _markdown.Prepare("Write `{{ssh: NAS / uptime}}` to run it.");

        Assert.Empty(document.Shortcodes);
        Assert.Contains("<code>{{ssh: NAS / uptime}}</code>" + CopyButtonExtension.Button, document.Html);
    }

    // ---------- {{ssh}} ----------

    [Fact]
    public void TheCommandIsEverythingAfterTheFirstSlashAsWritten()
    {
        var request = RunbookCommands.Read(Code("{{ssh: \"NAS\" / docker restart plex}}"), out var problem);
        Assert.Null(problem);
        Assert.Equal("NAS", request!.Connection);
        Assert.Equal("docker restart plex", request.Command);
        Assert.Equal(RunbookCommands.DefaultTimeout, request.Timeout);

        // Slashes, equals signs and quotes in the command are the command's.
        request = RunbookCommands.Read(Code("{{ssh: Home Server / FOO=1 tail -n 5 /var/log/syslog | grep \"a/b\"}}"), out _);
        Assert.Equal("Home Server", request!.Connection);
        Assert.Equal("FOO=1 tail -n 5 /var/log/syslog | grep \"a/b\"", request.Command);

        // A name with a slash in it is quoted; options go with the name.
        request = RunbookCommands.Read(Code("{{ssh: \"Rack / Pi\" timeout=2m label=\"Restart Plex\" / uptime}}"), out _);
        Assert.Equal("Rack / Pi", request!.Connection);
        Assert.Equal("uptime", request.Command);
        Assert.Equal(TimeSpan.FromMinutes(2), request.Timeout);
        Assert.Equal("Restart Plex", request.Label);

        // A command quoted whole loses the quotes — the way to write braces.
        request = RunbookCommands.Read(Code("{{ssh: NAS / \"docker ps --format '{{.Names}}'\"}}"), out _);
        Assert.Equal("docker ps --format '{{.Names}}'", request!.Command);
    }

    [Theory]
    [InlineData("{{ssh: NAS}}", "Say which machine and which command")]
    [InlineData("{{ssh: / uptime}}", "Say which machine")]
    [InlineData("{{ssh: NAS / }}", "Say what to run on NAS")]
    [InlineData("{{ssh: NAS timeout=1 / uptime}}", "between 5 seconds and 10 minutes")]
    [InlineData("{{ssh: NAS timeout=soon / uptime}}", "is not a timeout")]
    public void AnSshShortcodeMissingSomethingSaysWhat(string source, string expected)
    {
        Assert.Null(RunbookCommands.Read(Code(source), out var problem));
        Assert.Contains(expected, problem);
    }

    [Theory]
    [InlineData("rm -rf /srv/media")]
    [InlineData("sudo rm -r old")]
    [InlineData("rm -f -R /tmp/x")]
    [InlineData("rm --recursive x")]
    [InlineData("mkfs.ext4 /dev/sdb1")]
    [InlineData("dd if=/dev/zero of=/dev/sda bs=1M")]
    [InlineData("sudo shutdown -h now")]
    [InlineData("reboot")]
    [InlineData("systemctl poweroff")]
    [InlineData(":(){ :|:& };:")]
    [InlineData("zfs destroy tank/media")]
    [InlineData("docker system prune -af")]
    [InlineData("echo hi > /dev/sda")]
    public void DangerousCommandsAreNoticed(string command) => Assert.NotNull(RunbookCommands.Danger(command));

    [Theory]
    [InlineData("docker restart plex")]
    [InlineData("rm -f /tmp/lockfile")]
    [InlineData("rm --force /tmp/lockfile")]
    [InlineData("df -h")]
    [InlineData("systemctl restart smbd")]
    [InlineData("tail -n 50 /var/log/syslog")]
    [InlineData("add-user ddclient")]
    public void OrdinaryCommandsAreNot(string command) => Assert.Null(RunbookCommands.Danger(command));

    [Fact]
    public void OutputIsTheLastLinesCleanedAndMasked()
    {
        var output = string.Join("\n", Enumerable.Range(1, 80).Select(i => $"line {i}"))
                     + "\n\u001b[31mred\u001b[0m and DB_PASSWORD=hunter2\rprogress 100%\n\n\n";
        var lines = RunbookCommands.Tail(output, LogSearch.Mask);

        Assert.Equal(RunbookCommands.OutputLines, lines.Count);
        Assert.Equal("line 32", lines[0]);
        // The carriage return redrew the line; the colour codes are gone; the secret is masked.
        Assert.Equal("progress 100%", lines[^1]);
        Assert.Equal("line 80", lines[^2]);

        var secret = RunbookCommands.Tail("\u001b[1mDB_PASSWORD=hunter2\u001b[0m", LogSearch.Mask);
        Assert.DoesNotContain("hunter2", secret[0]);
        Assert.False(secret[0].Contains('\u001b'));
        Assert.StartsWith("DB_PASSWORD=", secret[0]);
    }

    // ---------- {{logs}} ----------

    [Fact]
    public void LogsReadTheContainerAndItsOptions()
    {
        var request = NoteLogs.Read(Code("{{logs: plex last=15m errors}}"), out var problem);
        Assert.Null(problem);
        Assert.Equal(new NoteLogs.Request("plex", "", TimeSpan.FromMinutes(15), true, "", NoteLogs.DefaultLines), request);

        request = NoteLogs.Read(Code("{{logs: sonarr connection=\"NAS docker\" last=2h match=\"database is locked\" lines=25}}"), out _);
        Assert.Equal(new NoteLogs.Request("sonarr", "NAS docker", TimeSpan.FromHours(2), false, "database is locked", 25), request);

        request = NoteLogs.Read(Code("{{logs: radarr}}"), out _);
        Assert.Equal(new NoteLogs.Request("radarr", "", NoteLogs.DefaultWindow, false, "", 10), request);
    }

    [Theory]
    [InlineData("{{logs: plex last=2m}}", "between 5m and 24h")]
    [InlineData("{{logs: plex last=2d}}", "between 5m and 24h")]
    [InlineData("{{logs: plex last=soon}}", "is not a length of time")]
    [InlineData("{{logs: plex lines=51}}", "from 1 to 50")]
    [InlineData("{{logs: plex warnings}}", "“warnings” is not something")]
    [InlineData("{{logs: last=1h}}", "Say which container")]
    public void LogsMissingSomethingSayWhat(string source, string expected)
    {
        Assert.Null(NoteLogs.Read(Code(source), out var problem));
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void AContainerIsFoundByNameThenByComposeService()
    {
        var rows = new[]
        {
            ContainerListTests.Row("media-plex-1", state: "exited", service: "plex", id: "a"),
            ContainerListTests.Row("media-plex-2", service: "plex", id: "b"),
            ContainerListTests.Row("sonarr", id: "c"),
        };

        Assert.Equal("c", NoteLogs.Find(rows, "Sonarr")?.Id);
        Assert.Equal("b", NoteLogs.Find(rows, "plex")?.Id); // the running one
        Assert.Equal("a", NoteLogs.Find(rows, "media-plex-1")?.Id);
        Assert.Null(NoteLogs.Find(rows, "radarr"));
    }

    [Fact]
    public void TheLogsPageLinkIsTheSameSearch()
    {
        var request = new NoteLogs.Request("plex", "", TimeSpan.FromMinutes(30), true, "a b", 10);

        Assert.Equal("logs?connection=d1&container=plex&last=1h&quick=error,exception,fatal,failed&q=a%20b&run=1",
            NoteLogs.PageLink(request, "d1", "plex"));
        Assert.Equal("logs?connection=d1&container=plex&last=24h",
            NoteLogs.PageLink(request with { Errors = false, Match = "", Window = TimeSpan.FromHours(20) }, "d1", "plex"));
        Assert.Equal("no errors in the last 15 minutes", NoteLogs.Nothing(new NoteLogs.Request("plex", "", TimeSpan.FromMinutes(15), true, "", 10)));
    }

    // ---------- {{run}} ----------

    [Fact]
    public void ARunLineSaysWhenNextAndHowLastWent()
    {
        var now = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var action = new ScheduledAction
        {
            Name = "Weekly Plex restart",
            TargetConnectionId = "c1",
            Container = "plex",
            Schedule = new ActionSchedule { Kind = ScheduleKind.Weekly, Days = [DayOfWeek.Sunday], Times = [new TimeOnly(4, 0)] },
        };
        var next = new DateTimeOffset(2026, 10, 4, 4, 0, 0, TimeSpan.Zero);
        var last = new ScheduledRun(action.Id, now.AddDays(-4).AddHours(-5), ScheduledTriggers.Schedule, ScheduledOutcomes.Ok, "", TimeSpan.Zero);

        Assert.Equal("next run in 3 days, Sun 4 Oct 04:00",
            RunShortcode.Next(new ScheduledActions.Status(action, next, last, false), TimeZoneInfo.Utc, now));
        Assert.Equal("last ok 4d 5h ago (Sun 04:00)", RunShortcode.Last(last, TimeZoneInfo.Utc, now));
        Assert.Equal("never run yet", RunShortcode.Last(null, TimeZoneInfo.Utc, now));
        Assert.StartsWith("switched off",
            RunShortcode.Next(new ScheduledActions.Status(action with { Enabled = false }, null, null, false), TimeZoneInfo.Utc, now));
        Assert.Equal("failed", RunShortcode.Outcome(last with { Outcome = ScheduledOutcomes.Failed }));
        Assert.Equal("skipped", RunShortcode.Outcome(last with { Outcome = ScheduledOutcomes.Skipped }));
    }

    [Fact]
    public void TheNewKindsAreKnown()
    {
        Assert.True(Code("{{ssh: NAS / uptime}}").IsKnown);
        Assert.True(Code("{{run: \"Weekly Plex restart\"}}").IsKnown);
        Assert.True(Code("{{logs: plex}}").IsBlock);
        Assert.Equal(ChangeKinds.Command, ChangeKinds.Parse("commands"));
        Assert.Contains(ChangeKinds.All, k => k.Key == ChangeKinds.Command);
    }
}
