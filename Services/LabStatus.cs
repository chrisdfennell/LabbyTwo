using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>One connection in a <c>{{status: tab …}}</c> summary: its dot and its word.</summary>
public sealed record StatusEntry(Connection Connection, string Word, string Dot);

/// <summary>
/// What <c>{{status: tab "Media"}}</c> or <c>{{status: all}}</c> says: the one line, and the
/// connections behind it for <c>full</c>.
/// </summary>
/// <param name="Line">"Media: 7 fine, 1 down (Plex), 1 checking".</param>
public sealed record StatusLine(string Line, IReadOnlyList<StatusEntry> Entries, int Down);

/// <summary>What <c>{{who: home}}</c> says.</summary>
/// <param name="Problem">Why it cannot say: presence is not set up. Shown as a "?" with these words.</param>
/// <param name="Home">The names of the devices that answered, in the order they are listed.</param>
/// <param name="Watched">How many devices are watched, for the tooltip.</param>
/// <param name="Waiting">Set up, but not checked yet since the app started.</param>
public sealed record WhoHome(string? Problem, IReadOnlyList<string> Home, int Watched, bool Waiting);

/// <summary>What <c>{{who: watching}}</c> says.</summary>
/// <param name="Problem">Why it cannot say: no Plex or Tautulli connection.</param>
/// <param name="Waiting">Set up, but no answer yet since the app started.</param>
public sealed record WhoWatching(string? Problem, IReadOnlyList<NowPlayingStream> Streams, bool Waiting);

/// <summary>
/// The words for a tab's or the whole lab's state, and for who is in and what is playing —
/// worked out from what the monitor already holds, so a note can say them on every sweep
/// without asking anything. Pure: handed the connections, the layout and a way to look up
/// a verdict, it reads nothing else, which is what lets every case be a test.
/// </summary>
public static class LabStatus
{
    /// <summary>The provider type of the Who's home plugin, whose metrics <c>{{who: home}}</c> reads.</summary>
    public const string PresenceProvider = "presence";

    /// <summary>The providers whose probes carry <see cref="NowPlaying"/> — Tautulli first, since it says more.</summary>
    public static readonly IReadOnlyList<string> MediaProviders = ["tautulli", "plex"];

    /// <summary>How many down connections a summary names before "and 2 more".</summary>
    public const int NamedDown = 3;

    /// <summary>A tab by id, name or slug, names and slugs ignoring case — the way a person would write it.</summary>
    public static Tab? FindTab(IReadOnlyList<Tab> tabs, string nameOrId)
    {
        var wanted = nameOrId.Trim();
        if (wanted.Length == 0)
            return null;
        return tabs.FirstOrDefault(t => string.Equals(t.Id, wanted, StringComparison.Ordinal))
            ?? tabs.FirstOrDefault(t => string.Equals(t.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            ?? tabs.FirstOrDefault(t => string.Equals(t.Slug, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The connections a tab shows: those its cards (or a custom page's blocks, which are
    /// cards too) are bound to, once each, in the connections' own order. The phone view
    /// groups the lab the same way, so "Media" means the same thing on both.
    /// </summary>
    public static IReadOnlyList<Connection> OnTab(Tab tab, IReadOnlyList<Widget> widgets, IReadOnlyList<Connection> connections)
    {
        var ids = widgets
            .Where(w => w.TabId == tab.Id && w.ConnectionId is not null)
            .Select(w => w.ConnectionId!)
            .ToHashSet(StringComparer.Ordinal);
        return [.. connections.Where(c => ids.Contains(c.Id)).OrderBy(c => c.Sort).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// What a <c>{{status:}}</c> names when it names more than one connection: <c>all</c>, or
    /// <c>tab Media</c> (the shortcode grammar has already taken any quotes off). Null for an
    /// ordinary connection name — and also when a connection is actually called "all" or
    /// "tab Media", since that is what it always meant before.
    /// </summary>
    /// <param name="full">Whether it asked for every connection to be listed: a last word <c>full</c>, or <c>full=true</c>.</param>
    public static string? SummaryTarget(Shortcode code, IReadOnlyList<Connection> connections, out bool full)
    {
        full = Runbook.IsYes(code.Option("full")) || string.Equals(code.Option("show"), "full", StringComparison.OrdinalIgnoreCase);
        if (code.Kind != "status" || code.Target.Count != 1)
            return null;
        var written = code.Part(0).Trim();
        if (ShortcodeLookup.Connection(connections, written) is not null)
            return null;

        if (written.EndsWith(" full", StringComparison.OrdinalIgnoreCase))
        {
            full = true;
            written = written[..^5].TrimEnd();
        }
        if (string.Equals(written, "all", StringComparison.OrdinalIgnoreCase))
            return "all";
        return written.StartsWith("tab ", StringComparison.OrdinalIgnoreCase) && written[4..].Trim().Length > 0
            ? written
            : null;
    }

    /// <summary>
    /// The line for <paramref name="title"/>: counts of fine, down (named), can't check and
    /// checking, leaving out what is zero. Only monitored connections count, as on the
    /// dashboard; a connection whose last probe failed only because LabbyTwo could not see
    /// is "can't check", not down, as the phone view says it.
    /// </summary>
    public static StatusLine Summarize(string title, IEnumerable<Connection> connections,
        Func<Connection, bool> isMonitored, Func<string, HealthMonitor.ProbeState?> state)
    {
        var entries = new List<StatusEntry>();
        var down = new List<string>();
        int fine = 0, cantCheck = 0, checking = 0;
        foreach (var connection in connections.Where(isMonitored))
        {
            var current = state(connection.Id);
            switch (current)
            {
                case { CantCheck: not null }:
                    cantCheck++;
                    entries.Add(new StatusEntry(connection, "can't check", "status-unknown"));
                    break;
                case { IsUp: true }:
                    fine++;
                    entries.Add(new StatusEntry(connection, "up", current.ConsecutiveFailures > 0 ? "status-flapping" : "status-up"));
                    break;
                case { IsUp: false }:
                    down.Add(connection.Name);
                    entries.Add(new StatusEntry(connection, "down", "status-down"));
                    break;
                default:
                    checking++;
                    entries.Add(new StatusEntry(connection, "checking", "status-unknown"));
                    break;
            }
        }

        if (entries.Count == 0)
            return new StatusLine($"{title}: nothing monitored", entries, 0);

        var parts = new List<string>();
        if (fine > 0)
            parts.Add(fine == entries.Count && fine > 1 ? $"all {fine} fine" : $"{fine} fine");
        if (down.Count > 0)
        {
            var named = down.Count <= NamedDown
                ? Join(down)
                : $"{string.Join(", ", down.Take(NamedDown))} and {down.Count - NamedDown} more";
            parts.Add($"{down.Count} down ({named})");
        }
        if (cantCheck > 0)
            parts.Add($"{cantCheck} can't be checked");
        if (checking > 0)
            parts.Add($"{checking} checking");
        return new StatusLine($"{title}: {string.Join(", ", parts)}", entries, down.Count);
    }

    /// <summary>
    /// Who is home, from the Who's home plugin's connections: each device it watches is a
    /// metric of its own, <c>home_…</c>, labelled with the device's name, 1 while it answers.
    /// Read through the registry rather than the plugin, so this knows nothing about the
    /// plugin but its type name and works whether it is installed or not.
    /// </summary>
    /// <param name="stored">The latest stored readings, for the moment after a restart before the first sweep.</param>
    public static WhoHome Home(IReadOnlyList<Connection> connections, Registry registry,
        Func<string, HealthMonitor.ProbeState?> state, Func<string, IReadOnlyDictionary<string, double>> stored)
    {
        var presence = connections.Where(c => c.Enabled && c.Provider == PresenceProvider).ToList();
        if (presence.Count == 0 || registry.Provider(PresenceProvider) is null)
        {
            return new WhoHome(registry.Provider(PresenceProvider) is null
                ? "Presence isn't set up: install the Who's home plugin and add a connection listing the phones to watch."
                : "Presence isn't set up: add a Who's home connection listing the phones to watch.", [], 0, false);
        }

        var home = new List<string>();
        var watched = 0;
        var answered = false;
        foreach (var connection in presence)
        {
            var live = state(connection.Id)?.Metrics;
            var readings = live is { Count: > 0 } ? live : stored(connection.Id);
            answered |= readings.Count > 0;
            foreach (var device in registry.MetricsFor(connection).Where(m => m.Key.StartsWith("home_", StringComparison.Ordinal)))
            {
                watched++;
                if (readings.TryGetValue(device.Key, out var value) && value > 0 && !home.Contains(device.Label))
                    home.Add(device.Label);
            }
        }
        return new WhoHome(null, home, watched, !answered);
    }

    /// <summary>
    /// What is playing, from the streams the Plex and Tautulli probes keep (see
    /// <see cref="NowPlaying"/>). With both set up for one server the same stream is
    /// reported twice, so it is listed once — Tautulli's, which knows whether it transcodes.
    /// </summary>
    public static WhoWatching Watching(IReadOnlyList<Connection> connections, Func<string, HealthMonitor.ProbeState?> state)
    {
        var servers = connections
            .Where(c => c.Enabled && MediaProviders.Contains(c.Provider))
            .OrderBy(c => MediaProviders.ToList().IndexOf(c.Provider))
            .ToList();
        if (servers.Count == 0)
            return new WhoWatching("Nothing to ask: add a Plex or Tautulli connection to see what is playing.", [], false);

        var streams = new List<NowPlayingStream>();
        var seen = new HashSet<(string, string, string)>();
        var answered = false;
        foreach (var server in servers)
        {
            if (state(server.Id) is not { IsUp: true } current)
                continue;
            answered = true;
            foreach (var stream in NowPlaying.From(current.Details))
            {
                if (seen.Add((stream.User.ToLowerInvariant(), stream.Title.ToLowerInvariant(), stream.Subtitle.ToLowerInvariant())))
                    streams.Add(stream);
            }
        }
        return new WhoWatching(null, streams, !answered && servers.All(s => state(s.Id) is null));
    }

    /// <summary>"Chris", "Chris and Sam", "Chris, Sam and Alex".</summary>
    public static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
    };

    /// <summary>
    /// One stream in words: "Chris — The Office (Dinner Party) on Living room TV, 42%, transcoding".
    /// Every part is optional except the title, and a missing one is left out rather than
    /// shown as a blank.
    /// </summary>
    public static string Describe(NowPlayingStream stream)
    {
        var what = stream.Title.Length > 0 ? stream.Title : "Something";
        if (stream.Subtitle.Length > 0)
            what += $" ({stream.Subtitle})";
        var line = stream.User.Length > 0 ? $"{stream.User} — {what}" : what;
        if (stream.Device.Length > 0)
            line += $" on {stream.Device}";
        line += $", {stream.Percent:0}%";
        return stream.Transcoding switch
        {
            true => line + ", transcoding",
            false => line + ", direct",
            null => line,
        };
    }
}
