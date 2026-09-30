using LabbyTwo.Core;
using LabbyTwo.Services;

namespace LabbyTwo.Tests;

/// <summary>
/// Finding the one-shot Watchtower LabbyTwo started, and saying when it has run too long —
/// the helper on the NAS that sat there for fifteen hours after "Update all" — and the
/// incident timeline that no longer draws sixteen hours of changes at once.
/// </summary>
public sealed class UpdateHelperTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 21, 6, 0, TimeSpan.Zero);

    private static string List(params string[] entries) => "[" + string.Join(",", entries) + "]";

    private static string Entry(string id, string name, string image, string command, DateTimeOffset created, string state,
        string labels = "{}") =>
        $$"""
        { "Id": "{{id}}", "Names": ["/{{name}}"], "Image": "{{image}}", "Command": "{{command}}",
          "Created": {{created.ToUnixTimeSeconds()}}, "State": "{{state}}", "Labels": {{labels}} }
        """;

    [Fact]
    public void LabbyTwosHelpersAreFoundByLabelAndOlderOnesByTheirCommand()
    {
        var payload = List(
            Entry("a1", "labbytwo-update-helper-20260929-210600", "containrrr/watchtower:latest",
                "/watchtower --run-once sonarr radarr", T0, "running",
                $$"""{ "{{UpdateHelperRules.Label}}": "true", "{{UpdateHelperRules.TargetsLabel}}": "15" }"""),
            // Started by an older LabbyTwo: no label, a name Docker made up.
            Entry("b2", "jolly_bardeen", "containrrr/watchtower", "/watchtower --run-once --cleanup sonarr radarr plex", T0, "running"),
            // Somebody's own Watchtower on a schedule: not ours.
            Entry("c3", "watchtower", "containrrr/watchtower", "/watchtower --schedule 0 0 4 * * *", T0, "running"),
            Entry("d4", "sonarr", "lscr.io/linuxserver/sonarr", "/init", T0, "running"));

        var helpers = UpdateHelperRules.Find(payload);

        Assert.Equal(["a1", "b2"], helpers.Select(h => h.Id));
        Assert.True(helpers[0].Labelled);
        Assert.Equal(15, helpers[0].Targets);
        Assert.False(helpers[1].Labelled);
        Assert.Equal("jolly_bardeen", helpers[1].Name);
        Assert.Equal(3, helpers[1].Targets);
        Assert.Equal(T0, helpers[1].Created);
    }

    [Fact]
    public void AHelperIsStuckOnlyWellPastWhatUpdatingThatManyTakes()
    {
        var helper = new UpdateHelper("b2", "jolly_bardeen", T0, "running", 15, false);

        Assert.Equal(TimeSpan.FromMinutes(45), helper.Limit);
        Assert.False(helper.IsStuck(T0.AddMinutes(40)));
        Assert.True(helper.IsStuck(T0.AddMinutes(46)));
        Assert.True(helper.IsStuck(T0.AddHours(15)));

        // One that has exited is on its way out, not stuck.
        Assert.False((helper with { State = "exited" }).IsStuck(T0.AddHours(15)));

        Assert.Equal(TimeSpan.FromMinutes(31), UpdateHelperRules.Limit(0));
        Assert.Equal("labbytwo-update-helper-20260929-210600", UpdateHelperRules.NameFor(T0));
    }

    // ---- incident timelines --------------------------------------------------------------

    private static List<Change> Timeline(int count) =>
    [
        .. Enumerable.Range(1, count).Select(i =>
            new Change(T0.AddMinutes(i), ChangeKinds.Status, ChangeActions.Down, "c", "", $"change {i}") { Id = i }),
    ];

    [Fact]
    public void AShortTimelineIsDrawnWhole()
    {
        var rows = ChangeLists.TimelineRows(Timeline(ChangeLists.TimelineShown), new HashSet<long>(), all: false);
        Assert.Equal(ChangeLists.TimelineShown, rows.Count);
        Assert.DoesNotContain(null, rows);
    }

    [Fact]
    public void ALongTimelineShowsItsStartAndEndAndEveryPieceOfEvidence()
    {
        var timeline = Timeline(300);
        var rows = ChangeLists.TimelineRows(timeline, new HashSet<long> { 150 }, all: false);

        // 25 from the start, a gap, the evidence, a gap, 25 from the end.
        Assert.Equal(53, rows.Count);
        Assert.Equal(1, rows[0]!.Id);
        Assert.Equal(25, rows[24]!.Id);
        Assert.Null(rows[25]);
        Assert.Equal(150, rows[26]!.Id);
        Assert.Null(rows[27]);
        Assert.Equal(276, rows[28]!.Id);
        Assert.Equal(300, rows[^1]!.Id);

        Assert.Equal(300, ChangeLists.TimelineRows(timeline, new HashSet<long>(), all: true).Count);
    }
}
