using System.Collections.Concurrent;
using System.Globalization;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.WakePlugin;

/// <summary>
/// Wakes what is due. The backup box has to be on before the backup starts, and nobody wants
/// that to be a person remembering at half past one in the morning.
///
/// This is what <see cref="IBackgroundJob"/> is for. There is no page anyone opens at 02:00,
/// so before this extension point existed the only way to run it was to hide it inside a
/// probe and hope the connection was being polled.
/// </summary>
public sealed class WakeScheduleJob(
    ConfigStore config,
    HealthMonitor health,
    ILogger<WakeScheduleJob> log) : IBackgroundJob
{
    public string Name => "wake-schedule";

    /// <summary>
    /// The floor, and the right value: the schedule is written to the minute, so checking
    /// less often than that would mean quietly rounding somebody's 02:00 to 02:05.
    /// </summary>
    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    /// <summary>
    /// No. A restart is not a reason to wake anything, and a container that flaps would
    /// otherwise send a packet every time it came up.
    /// </summary>
    public bool RunAtStartup => false;

    /// <summary>
    /// The last date each connection was woken on, so a job that runs every minute does not
    /// send sixty packets during the minute the clock says 02:00. In memory rather than in
    /// the database because the consequence of forgetting across a restart is one extra
    /// magic packet, which costs nothing — a table would be more machinery than the problem.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateOnly> _fired = new();

    public async Task RunAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.DateTime);

        var due = (await config.ConnectionsAsync(ct))
            .Where(c => c.Provider == WakeProvider.ProviderType && c.Enabled)
            .Where(c => IsDue(c, now))
            .Where(c => _fired.GetValueOrDefault(c.Id) != today)
            .ToList();

        foreach (var connection in due)
        {
            // Claim the day before sending. If the send throws, the machine stays asleep
            // until tomorrow rather than being retried every minute until dawn — a wake
            // that failed at 02:00 for a reason like "no route to the broadcast address"
            // will fail identically at 02:01, and 480 log lines is how a real fault gets
            // buried.
            _fired[connection.Id] = today;

            if (health.State(connection.Id)?.Metrics.GetValueOrDefault("awake") is 1)
            {
                log.LogInformation("{Name} is already awake; no packet sent", connection.Name);
                continue;
            }

            if (WakeProvider.MacAddress(connection) is not { } mac)
            {
                log.LogWarning("{Name} is scheduled to wake but has no usable MAC address", connection.Name);
                continue;
            }

            try
            {
                await WakeProvider.SendAsync(mac,
                    connection.Settings.Get("broadcast", "255.255.255.255"),
                    connection.Settings.GetInt("port", 9), ct);

                log.LogInformation("Sent a wake packet to {Name}", connection.Name);
            }
            catch (Exception ex)
            {
                // Thrown, not swallowed, would take out the whole job and every other
                // machine due in the same minute with it.
                log.LogError(ex, "Could not send a wake packet to {Name}", connection.Name);
            }
        }
    }

    /// <summary>
    /// Whether this connection's schedule lands in the minute we are in.
    ///
    /// A missed minute is a missed wake: if LabbyTwo was restarting at 02:00 the packet is
    /// simply not sent, and the machine stays asleep. That is the honest behaviour for a
    /// one-shot at a wall-clock time, and the alternative — firing late for anything missed
    /// — means a container that starts at 09:00 waking the box that was meant to be on at
    /// two in the morning and off by six.
    /// </summary>
    internal static bool IsDue(Connection connection, DateTimeOffset now)
    {
        if (ParseTime(connection.Settings.Get("wake_at")) is not { } at)
            return false;

        if (!RunsOn(connection.Settings.Get("wake_days"), now.DayOfWeek))
            return false;

        return now.Hour == at.Hour && now.Minute == at.Minute;
    }

    /// <summary>"02:00", "2:00" and "0200" all mean the same thing to a person typing it.</summary>
    internal static TimeOnly? ParseTime(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0)
            return null;

        string[] formats = ["HH:mm", "H:mm", "HHmm", "HH:mm:ss"];
        if (TimeOnly.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return exact;

        // The user's own format last, so "2:00 pm" works on a machine whose culture has it.
        return TimeOnly.TryParse(value, CultureInfo.CurrentCulture, out var loose) ? loose : null;
    }

    /// <summary>
    /// Blank means every day. Anything unrecognised is ignored rather than treated as a
    /// match, so a typo means one day missed instead of a machine woken every morning.
    /// </summary>
    internal static bool RunsOn(string raw, DayOfWeek day)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        var wanted = raw.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = day.ToString();

        return wanted.Any(entry =>
            name.StartsWith(entry, StringComparison.OrdinalIgnoreCase) && entry.Length >= 3);
    }
}
