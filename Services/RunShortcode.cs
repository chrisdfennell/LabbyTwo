using System.Globalization;
using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>
/// The words <c>{{run: "Weekly Plex restart"}}</c> is drawn with. Pure, with "now" and the zone
/// passed in, so "next run tomorrow at 04:00" is a test rather than a wait for Sunday.
/// </summary>
public static class RunShortcode
{
    /// <summary>The action a <c>{{run: …}}</c> names — its name or id — or null with why.</summary>
    public static string? Name(Shortcode code, out string? problem)
    {
        problem = null;
        var name = code.Part(0) is { Length: > 0 } part ? part : code.Option("action");
        if (name.Length == 0)
            problem = "Say which scheduled action: {{run: \"Weekly Plex restart\"}}.";
        return name.Length > 0 ? name : null;
    }

    /// <summary>
    /// "next run in 3 days, Sun 4 Oct 04:00"; "off — it only runs when somebody presses Run now";
    /// "not set up completely, so it never runs on its own".
    /// </summary>
    public static string Next(ScheduledActions.Status status, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (!status.Action.Enabled)
            return "switched off, so it only runs when somebody presses Run now";
        if (status.Action.Problem() is not null)
            return "not set up completely, so it never runs on its own";
        if (status.Next is not { } next)
            return "no run coming up";

        var local = TimeZoneInfo.ConvertTime(next, zone);
        var here = TimeZoneInfo.ConvertTime(now, zone);
        var words = LiveTime.Countdown(local.DateTime, hasTime: true, here.DateTime);
        return $"next run {words}, {local.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)}";
    }

    /// <summary>"last ok 2d ago (Sun 04:00)", "last failed 3h ago (Sat 20:00)", "never run yet".</summary>
    public static string Last(ScheduledRun? run, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (run is null)
            return "never run yet";
        var local = TimeZoneInfo.ConvertTime(run.At, zone);
        return $"last {Outcome(run)} {Ago.Since(run.At, now)} ({local.ToString("ddd HH:mm", CultureInfo.InvariantCulture)})";
    }

    /// <summary>ok, failed or skipped — the three words a run can end with.</summary>
    public static string Outcome(ScheduledRun run) => run.Outcome switch
    {
        ScheduledOutcomes.Ok => "ok",
        ScheduledOutcomes.Failed => "failed",
        _ => "skipped",
    };
}
