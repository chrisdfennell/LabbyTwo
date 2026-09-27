using System.Collections.Concurrent;
using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Pushes National Weather Service warnings to the alert channels as they are issued.
///
/// Everything else in this app alerts on a number crossing a line, which is the right shape
/// for a disk filling up and the wrong one for a tornado: by the time you have picked a
/// threshold you have lost the thing that matters, which is what the warning actually says
/// and how long it runs for. So this reads the warnings and sends them in their own words.
///
/// It runs on its own five-minute interval rather than off the probe sweep, because a
/// warning that waits for the next dashboard refresh is a warning that arrived late.
/// </summary>
public sealed class WeatherAlertJob(
    ConfigStore config,
    WeatherAlertsProvider provider,
    AlertService alerts,
    ILogger<WeatherAlertJob> log) : IBackgroundJob
{
    public string Name => "weather-warnings";

    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    /// <summary>
    /// Yes — and the first run is where the "was this already in force?" question gets
    /// settled. See <see cref="RunAsync"/>.
    /// </summary>
    public bool RunAtStartup => true;

    // Warning ids already announced, so a warning in force for six hours is announced once
    // rather than seventy-two times. In memory only: the cost of losing it is one repeat
    // after a restart, and the alternative is a database table for a set of strings.
    private readonly Announced _announced = new();

    private bool _primed;

    public async Task RunAsync(CancellationToken ct)
    {
        var connections = (await config.ConnectionsAsync(ct))
            .Where(c => c.Provider == "nws" && c.Enabled)
            .ToList();

        if (connections.Count == 0)
            return;

        var now = DateTimeOffset.Now;

        foreach (var connection in connections)
        {
            IReadOnlyList<WeatherAlertsProvider.Warning> warnings;
            try
            {
                warnings = await provider.FetchAsync(connection, ct);
            }
            catch (Exception ex)
            {
                // The connection's own probe reports this properly on the Connections page;
                // no point saying it twice, and certainly not to somebody's phone.
                log.LogWarning(ex, "Could not read weather warnings for {Connection}", connection.Name);
                continue;
            }

            foreach (var warning in warnings)
            {
                if (!_announced.Saw(Key(connection, warning), now))
                    continue;

                // On the first run after a restart, anything already under way is recorded
                // silently: re-announcing a four-hour-old flood watch on every container
                // update is how people learn to ignore the channel. Something issued in the
                // last quarter of an hour is still news, though — a restart during a storm
                // must not swallow the warning that came with it.
                if (!_primed && warning.Onset is { } onset && onset < now.AddMinutes(-15))
                    continue;

                await SendAsync(connection, warning, ct);
            }
        }

        _primed = true;
        _announced.Forget(now);
    }

    private async Task SendAsync(Connection connection, WeatherAlertsProvider.Warning warning, CancellationToken ct)
    {
        var headline = warning.Headline is { Length: > 0 } text ? text : warning.Event;
        var body = string.Join("\n", new[]
        {
            warning.Area,
            warning.Window,
            warning.Instruction,
        }.Where(part => part is { Length: > 0 }));

        var alert = new Alert(
            warning.Rank >= 3 ? AlertLevel.Down : AlertLevel.Info,
            $"{warning.Icon} {headline}",
            body.Length > 0 ? body : "No further detail was given.")
        {
            // The whole reason this job exists. A tornado warning at 2am is precisely the
            // alert quiet hours must not hold — and only for severe and above, so a frost
            // advisory still waits until morning like everything else.
            Urgent = warning.Rank >= 3,
        };

        log.LogWarning("Weather warning for {Connection}: {Event} ({Severity})",
            connection.Name, warning.Event, warning.Severity);

        // Deliberately not routed through AlertService.SuppressedAsync's dependency check —
        // a warning is not "down because something upstream is down". A silence on the
        // connection is still honoured, because that is somebody explicitly saying stop.
        if (connection.IsSilenced(DateTimeOffset.Now))
        {
            log.LogInformation("Weather warning not sent: {Connection} is silenced", connection.Name);
            return;
        }

        await alerts.BroadcastAsync(alert, ct);
    }

    private static string Key(Connection connection, WeatherAlertsProvider.Warning warning) =>
        $"{connection.Id}|{warning.Id}";

    /// <summary>
    /// The warnings already announced, by when each was last seen in the feed. Its own
    /// type so the remembering can be tested without a weather service to ask.
    /// </summary>
    public sealed class Announced
    {
        private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new();

        /// <summary>
        /// Notes that this warning is in the feed now, and says whether it is new. Every
        /// sighting moves the time forward — not only the first — because what
        /// <see cref="Forget"/> needs to know is whether the warning is still around, not
        /// how long ago it began. Keeping the first sighting instead forgot a winter storm
        /// warning a day after it was issued while it was still in force, and announced it
        /// again as news; again the day after, and past quiet hours each time if severe.
        /// </summary>
        public bool Saw(string key, DateTimeOffset now)
        {
            var isNew = true;
            _lastSeen.AddOrUpdate(key, now, (_, _) =>
            {
                isNew = false;
                return now;
            });
            return isNew;
        }

        /// <summary>
        /// Ids are only worth remembering while they could still be in the feed. One that
        /// has not appeared for a day has left it, and dropping those stops this growing
        /// for as long as the app runs.
        /// </summary>
        public void Forget(DateTimeOffset now)
        {
            foreach (var (key, at) in _lastSeen)
            {
                if (now - at > TimeSpan.FromDays(1))
                    _lastSeen.TryRemove(key, out _);
            }
        }

        public bool Contains(string key) => _lastSeen.ContainsKey(key);
    }
}
