using System.Collections.Concurrent;
using System.Diagnostics;
using LabbyTwo.Core;
using LabbyTwo.Providers;

namespace LabbyTwo.CalendarPlugin;

/// <summary>
/// Any published calendar — Google, Nextcloud, iCloud, a bin collection schedule, a sports
/// fixture list. One URL, no API key, no OAuth: an .ics feed is just a file over HTTP.
///
/// Monitoring a calendar sounds odd until you notice what it gives you for free: the feed
/// going stale or 404ing is a real failure worth an alert, and "events today" is a number
/// like any other, so it charts.
/// </summary>
public sealed class CalendarProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "ics-calendar";
    public string DisplayName => "Calendar (ICS feed)";
    public string Icon => "📅";
    public string Category => "Home";
    public string Description =>
        "A published .ics calendar — what is on today, what is next, and an agenda for the week.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Feed URL", FieldKind.Url, "https://calendar.google.com/calendar/ical/…/basic.ics",
            Required: true,
            Help: "The secret address in iCalendar format. In Google Calendar it is Settings → your calendar → " +
                  "\"Secret address in iCal format\". Anyone with that link can read the calendar, so treat it " +
                  "like a password — it is stored encrypted here."),

        new("days", "Days to look ahead", FieldKind.Number, Default: "14"),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("events_today", "Events today"),
        new("events_ahead", "Events coming up"),
        new("hours_to_next", "Until the next one", " h", 1),
    ];

    // ---- the shared fetch ---------------------------------------------------------------

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (IReadOnlyList<Ics.Event> Events, DateTimeOffset At)> _cache = new();

    /// <summary>
    /// Events in the window, cached briefly per connection. A calendar feed is a whole file
    /// — often a few hundred kilobytes — so the widget, the agenda page and the probe share
    /// one download rather than each pulling it.
    /// </summary>
    public async Task<IReadOnlyList<Ics.Event>> UpcomingAsync(Connection connection, int days, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;

        if (_cache.TryGetValue(connection.Id, out var cached) && now - cached.At < Ttl)
            return Within(cached.Events, now, days);

        var url = connection.Settings.Get("url");
        if (url.Length == 0)
            throw new InvalidOperationException("No feed URL configured.");

        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        var text = await http.GetStringAsync(url, ct);

        if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            // Nearly always a share page rather than the feed, and "no events" would send
            // someone looking for a parsing bug that is not there.
            throw new InvalidOperationException(
                "That URL answered, but not with an iCalendar file. Check it is the iCal/ICS address rather " +
                "than the page you view the calendar on.");
        }

        // Parsed once over a generous window and cached, so changing a widget's range does
        // not re-download and re-expand every recurrence.
        var events = Ics.Read(text, now.AddDays(-1), now.AddDays(400));
        _cache[connection.Id] = (events, now);
        return Within(events, now, days);
    }

    private static IReadOnlyList<Ics.Event> Within(IReadOnlyList<Ics.Event> events, DateTimeOffset now, int days)
    {
        var horizon = now.AddDays(Math.Clamp(days, 1, 365));
        return [.. events.Where(e => e.End > now && e.Start < horizon)];
    }

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var days = Math.Clamp(connection.Settings.GetInt("days", 14), 1, 365);
            var events = await UpcomingAsync(connection, days, ct);
            stopwatch.Stop();

            var now = DateTimeOffset.Now;
            var today = now.Date;
            var next = events.FirstOrDefault(e => e.Start > now);

            var metrics = new Dictionary<string, double>
            {
                ["events_today"] = events.Count(e => e.Start.Date == today),
                ["events_ahead"] = events.Count,
                ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
            };

            if (next is not null)
                metrics["hours_to_next"] = (next.Start - now).TotalHours;

            var details = new Dictionary<string, string>();
            if (next is not null)
                details["Next"] = $"{next.Summary} — {next.Start:ddd d MMM HH:mm}";

            return ProbeResult.Up(
                stopwatch.Elapsed,
                events.Count == 0
                    ? $"Nothing in the next {days} days"
                    : $"{metrics["events_today"]:0} today, {events.Count} in the next {days} days",
                metrics,
                details.Count > 0 ? details : null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }
}
