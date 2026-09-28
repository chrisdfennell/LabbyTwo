using LabbyTwo.Core;
using LabbyTwo.Services;

namespace LabbyTwo.Components.Shared;

public enum MapStatus
{
    Up,
    Down,

    /// <summary>Monitored, and not probed yet since the app started.</summary>
    Checking,

    /// <summary>Paused, or a kind of connection the monitor never probes.</summary>
    Disabled,
}

/// <summary>
/// One connection as the dependency map draws it. Everything the drawing needs and nothing
/// it would have to look up, so <see cref="DependencyMapView"/> takes no services and the
/// same list can be drawn on a page, a card and in a test.
/// </summary>
/// <param name="Detail">What the last probe said, for the tooltip. Empty when there is none.</param>
public sealed record MapNode(
    string Id,
    string Name,
    string Icon,
    string? ParentId,
    MapStatus Status,
    bool Silenced = false,
    string Detail = "");

public static class DependencyMapModel
{
    /// <summary>
    /// The map's nodes from what is already in memory: connections from ConfigStore's cache
    /// and states from the monitor, so building this on every sweep costs no query.
    ///
    /// Alert channels are left out. Nothing can sit behind a webhook (the editor does not
    /// offer one as a parent), and a column of "disabled" Telegram and email boxes would be
    /// clutter on a map about what takes what down.
    /// </summary>
    public static IReadOnlyList<MapNode> From(
        IEnumerable<Connection> connections, HealthMonitor health, Registry registry, DateTimeOffset now) =>
        From(connections,
            health.State,
            connection => health.IsMonitored(connection),
            connection => registry.Provider(connection.Provider) is IAlertChannel,
            now);

    /// <summary>The same, with the lookups passed in, so it can be tested without a monitor.</summary>
    public static IReadOnlyList<MapNode> From(
        IEnumerable<Connection> connections,
        Func<string, HealthMonitor.ProbeState?> state,
        Func<Connection, bool> isMonitored,
        Func<Connection, bool> isAlertChannel,
        DateTimeOffset now) =>
    [
        .. connections
            .Where(connection => !isAlertChannel(connection))
            .Select(connection =>
            {
                var probe = isMonitored(connection) ? state(connection.Id) : null;
                var status = !isMonitored(connection) ? MapStatus.Disabled
                    : probe?.IsUp switch
                    {
                        true => MapStatus.Up,
                        false => MapStatus.Down,
                        _ => MapStatus.Checking,
                    };
                return new MapNode(
                    connection.Id,
                    connection.Name,
                    string.IsNullOrWhiteSpace(connection.Icon) ? "🔌" : connection.Icon,
                    string.IsNullOrWhiteSpace(connection.DependsOn) ? null : connection.DependsOn,
                    status,
                    connection.IsSilenced(now),
                    probe?.Message ?? "");
            }),
    ];

    /// <summary>The word for a status. The map never relies on colour alone to say it.</summary>
    public static string Word(MapStatus status) => status switch
    {
        MapStatus.Up => "up",
        MapStatus.Down => "down",
        MapStatus.Checking => "checking",
        _ => "disabled",
    };

    /// <summary>A shape that differs by status as well as the colour, for the same reason.</summary>
    public static string Glyph(MapStatus status) => status switch
    {
        MapStatus.Up => "▲",
        MapStatus.Down => "▼",
        MapStatus.Checking => "…",
        _ => "■",
    };

    public static string CssClass(MapStatus status) => status switch
    {
        MapStatus.Up => "is-up",
        MapStatus.Down => "is-down",
        MapStatus.Checking => "is-checking",
        _ => "is-disabled",
    };

    /// <summary>
    /// The line under a node's name. Something down beneath a parent that is also down is
    /// described by the parent, because that is the one to go and look at; something still
    /// answering beneath a parent that is down is flagged rather than dimmed silently, since
    /// it is either about to go or the parent's probe is the thing that is wrong.
    /// </summary>
    public static string Note(MapNode node, string? causeName, BrokenLink broken)
    {
        if (causeName is not null)
        {
            return node.Status == MapStatus.Down
                ? $"down because {causeName} is down"
                : $"{Word(node.Status)}, but {causeName} is down";
        }

        return broken switch
        {
            BrokenLink.MissingParent => $"{Word(node.Status)} · its parent was deleted",
            BrokenLink.Cycle => $"{Word(node.Status)} · part of a loop",
            _ => Word(node.Status),
        };
    }

    /// <summary>The full sentence behind a note, for the tooltip and the list view.</summary>
    public static string Explain(BrokenLink broken) => broken switch
    {
        BrokenLink.MissingParent =>
            "It sits behind a connection that no longer exists, so its alerts are no longer held when anything else is down. Pick a new one in its editor.",
        BrokenLink.Cycle =>
            "Its “Sits behind” leads round in a loop back to itself, so it is drawn as a root here. Clear one of them in its editor.",
        _ => "",
    };
}
