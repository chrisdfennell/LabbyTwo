using System.Globalization;
using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>
/// The reading a gauge or a table cell shows, from memory only: the monitor's last probe,
/// then the newest stored reading — the metric tile's order, so a gauge in a sentence and
/// the tile beside it never disagree about the same number. Never the database: a table of
/// ten connections by four metrics is forty lookups on every sweep, and each one is a
/// dictionary read.
/// </summary>
public static class LiveReading
{
    /// <summary>How far back a stored reading still counts — the metric tile's window.</summary>
    public static readonly TimeSpan StoredWindow = TimeSpan.FromHours(6);

    /// <summary>Every key the connection has reported, live or stored, for matching a metric by name.</summary>
    public static IEnumerable<string> Reported(HealthMonitor health, LatestReadings latest, string connectionId) =>
        (health.State(connectionId)?.Metrics.Keys ?? Enumerable.Empty<string>())
            .Concat(latest.Get(connectionId, StoredWindow).Keys);

    public static double? Value(HealthMonitor health, LatestReadings latest, string connectionId, string key)
    {
        if (health.State(connectionId)?.Metrics is { } live && live.TryGetValue(key, out var now))
            return now;
        return latest.Get(connectionId, StoredWindow).TryGetValue(key, out var stored) ? stored : null;
    }

    /// <summary>
    /// The window word a {{sparkline}} reads back as the same window: whole hours as "24h",
    /// anything else in minutes. Kept to four digits, which is what the sparkline accepts.
    /// </summary>
    public static string WindowWord(TimeSpan window) =>
        window.TotalHours % 1 == 0
            ? ((int)window.TotalHours).ToString(CultureInfo.InvariantCulture) + "h"
            : ((int)window.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "min";
}

/// <summary>How a box on a diagram is coloured: by the connection it names, or not at all.</summary>
public enum NodeStatus
{
    /// <summary>Matches no connection: drawn plain, since "Internet" is not something LabbyTwo watches.</summary>
    Neutral,
    Up,
    Down,

    /// <summary>Monitored and not probed yet since the app started.</summary>
    Checking,

    /// <summary>Silenced while somebody works on it — the per-connection "maintenance" from the Connections list.</summary>
    Maintenance,

    /// <summary>Paused, or a kind the monitor never probes.</summary>
    Paused,
}

public static class DiagramStatus
{
    /// <summary>
    /// A connection's status as a diagram box shows it. Silenced comes first: somebody said
    /// they are working on it, so its being down is expected, and a red box would send the
    /// next person to look at the wrong thing.
    /// </summary>
    public static NodeStatus Of(Connection connection, HealthMonitor.ProbeState? state, bool monitored, DateTimeOffset now) =>
        !connection.Enabled || !monitored ? NodeStatus.Paused
        : connection.IsSilenced(now) ? NodeStatus.Maintenance
        : state?.IsUp switch
        {
            true => NodeStatus.Up,
            false => NodeStatus.Down,
            _ => NodeStatus.Checking,
        };

    public static string Word(NodeStatus status) => status switch
    {
        NodeStatus.Up => "up",
        NodeStatus.Down => "down",
        NodeStatus.Checking => "checking",
        NodeStatus.Maintenance => "maintenance",
        NodeStatus.Paused => "paused",
        _ => "",
    };

    public static string CssClass(NodeStatus status) => status switch
    {
        NodeStatus.Up => "is-up",
        NodeStatus.Down => "is-down",
        NodeStatus.Checking => "is-checking",
        NodeStatus.Maintenance => "is-maintenance",
        NodeStatus.Paused => "is-paused",
        _ => "is-neutral",
    };

    /// <summary>A shape as well as a colour, as on the dependency map, so the status survives colour blindness.</summary>
    public static string Glyph(NodeStatus status) => status switch
    {
        NodeStatus.Up => "▲",
        NodeStatus.Down => "▼",
        NodeStatus.Checking => "…",
        NodeStatus.Maintenance => "◆",
        NodeStatus.Paused => "■",
        _ => "",
    };
}
