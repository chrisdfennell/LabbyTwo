using System.Security.Cryptography;
using System.Text;
using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>
/// What a shortcode can name: every connection, card and tab, read once by the Markdown
/// that holds the shortcodes (off the render thread) and handed down to each one.
/// </summary>
public sealed record ShortcodeScope(
    IReadOnlyList<Connection> Connections,
    IReadOnlyList<Widget> Widgets,
    IReadOnlyList<Tab> Tabs);

/// <summary>
/// The widgets and notes whose Markdown is being drawn, outermost first — so a card that
/// embeds a card that embeds the first one again stops at the second step instead of
/// drawing until the circuit runs out of stack.
/// </summary>
public sealed record EmbedChain(IReadOnlyList<string> Owners)
{
    /// <summary>
    /// How many Markdown documents deep an embed may go. Two is a runbook showing a card
    /// that has its own notes in it; beyond four is a loop the ids did not catch — an
    /// ad-hoc card whose settings change as it nests — or a page nobody could read.
    /// </summary>
    public const int MaxDepth = 4;

    public static EmbedChain Empty { get; } = new([]);

    public int Depth => Owners.Count;

    public bool Contains(string id) => Owners.Contains(id, StringComparer.Ordinal);

    public EmbedChain With(string? owner) => new([.. Owners, owner ?? ""]);
}

/// <summary>
/// Finding what a shortcode names. Kept apart from the components so the rules — which
/// wins when an id and a name collide, what case matters — are pinned by tests rather than
/// by what a component happened to do.
/// </summary>
public static class ShortcodeLookup
{
    /// <summary>
    /// A connection by id or by name. The id wins: it is what a shortcode written by the
    /// insert helper for a renamed connection would still have, and no two connections
    /// share one. Names are compared ignoring case and surrounding spaces, since "nas" and
    /// "NAS " are plainly the same thing to whoever wrote them.
    /// </summary>
    public static Connection? Connection(IReadOnlyList<Connection> connections, string nameOrId)
    {
        var wanted = nameOrId.Trim();
        if (wanted.Length == 0)
            return null;
        return connections.FirstOrDefault(c => string.Equals(c.Id, wanted, StringComparison.Ordinal))
            ?? connections.FirstOrDefault(c => string.Equals(c.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The address a browser opens for a connection — the service tile's rule, so a link in a
    /// runbook goes where clicking the tile goes: <c>open_url</c> ("Link opens") first, since
    /// that is the browser-reachable one when the probe uses a name only Docker can resolve;
    /// then <c>url</c>; then <c>http://</c> and the host. Only http and https come back — a
    /// <c>javascript:</c> or <c>file:</c> address in a setting must not become a link — and
    /// otherwise null, with why.
    /// </summary>
    public static string? WebAddress(Connection connection, out string? problem)
    {
        problem = null;
        var settings = connection.Settings;
        var written = settings.Get("open_url", settings.Get("url")).Trim();
        if (written.Length == 0 && settings.Get("host").Trim() is { Length: > 0 } host)
            written = "http://" + host;
        if (written.Length == 0)
        {
            problem = $"{connection.Name} has no web address. Set its URL, or “Link opens”, on the Connections page.";
            return null;
        }
        if (!Uri.TryCreate(written, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            problem = $"{connection.Name}’s address, “{written}”, is not a web page a browser can open.";
            return null;
        }
        return written;
    }

    /// <summary>
    /// A metric on a connection by key or by label, as its key. Keys first, from what the
    /// provider declares and what the connection has actually reported; then labels, so
    /// "Disk used" works as well as disk_percent. Null when neither matches.
    /// </summary>
    public static string? MetricKey(Registry registry, Connection connection, string keyOrLabel, IEnumerable<string> reported)
    {
        var wanted = keyOrLabel.Trim();
        if (wanted.Length == 0)
            return null;

        var keys = registry.MetricsFor(connection).Select(m => m.Key)
            .Concat(reported)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return keys.FirstOrDefault(k => string.Equals(k, wanted, StringComparison.OrdinalIgnoreCase))
            ?? keys.FirstOrDefault(k => string.Equals(registry.Metric(connection, k).Label, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A card by id or title, from any tab. Titles are compared ignoring case; when two
    /// cards share one, the first in tab order is the one meant, which is at least the
    /// same one every time.
    /// </summary>
    public static Widget? Widget(ShortcodeScope scope, string idOrTitle)
    {
        var wanted = idOrTitle.Trim();
        if (wanted.Length == 0)
            return null;

        var tabOrder = scope.Tabs.Select((t, i) => (t.Id, i)).ToDictionary(p => p.Id, p => p.i);
        return scope.Widgets.FirstOrDefault(w => string.Equals(w.Id, wanted, StringComparison.Ordinal))
            ?? scope.Widgets
                .Where(w => string.Equals(w.Title.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                .OrderBy(w => tabOrder.GetValueOrDefault(w.TabId, int.MaxValue))
                .ThenBy(w => w.Sort)
                .FirstOrDefault();
    }

    /// <summary>
    /// A provider action by its key ("restart") or the label on its button ("Restart"),
    /// ignoring case — from <paramref name="actions"/>, which should be what the connection
    /// offers right now, so an action its settings rule out is not found.
    /// </summary>
    public static ProviderAction? Action(IReadOnlyList<ProviderAction> actions, string keyOrLabel)
    {
        var wanted = keyOrLabel.Trim();
        if (wanted.Length == 0)
            return null;
        return actions.FirstOrDefault(a => string.Equals(a.Id, wanted, StringComparison.OrdinalIgnoreCase))
            ?? actions.FirstOrDefault(a => string.Equals(a.Label.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A card type by its key ("gauge") or the name the picker shows ("Gauge").</summary>
    public static IWidgetType? WidgetType(Registry registry, string typeOrName)
    {
        var wanted = typeOrName.Trim();
        return registry.Widgets.FirstOrDefault(w => string.Equals(w.Type, wanted, StringComparison.OrdinalIgnoreCase))
            ?? registry.Widgets.FirstOrDefault(w => string.Equals(w.DisplayName, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The settings for a card written out in a shortcode: the type's own defaults, then
    /// whatever the shortcode says. Everything except the handful of words that describe
    /// the card rather than configure it.
    /// </summary>
    public static SettingsBag CardSettings(IWidgetType type, Shortcode code)
    {
        var settings = new SettingsBag();
        foreach (var field in type.Fields)
        {
            if (field.Default is { Length: > 0 } fallback)
                settings[field.Key] = fallback;
        }
        foreach (var (key, value) in code.Options)
        {
            if (key.ToLowerInvariant() is not ("connection" or "title" or "type"))
                settings[key] = value;
        }
        return settings;
    }

    /// <summary>
    /// A stable id for a card that exists only in a shortcode. Stable so a card that keys
    /// anything by its widget id sees the same one across redraws, and derived from what
    /// was written so the recursion guard recognises the same card nested in itself.
    /// </summary>
    public static string AdHocId(Shortcode code)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code.Source));
        return "md-" + Convert.ToHexStringLower(hash)[..10];
    }
}

/// <summary>
/// The words a live value is drawn as. Pure, so a runbook reads exactly what the card next
/// to it says — the same decimals, the same unit, the same "about 6 weeks".
/// </summary>
public static class LiveText
{
    /// <summary>
    /// Decimal places for a metric: the one asked for, or the metric's own, kept to what a
    /// reading can honestly carry. The metric tile uses this too, which is what keeps a
    /// number in a sentence identical to the same number on a card.
    /// </summary>
    public static int Decimals(int? requested, MetricSpec spec) => Math.Clamp(requested ?? spec.Decimals, 0, 4);

    /// <summary>The number as the metric tile draws it, before its unit.</summary>
    public static string Number(double value, int decimals) => value.ToString($"F{decimals}");

    /// <summary>A reading with its unit: "42%", "12 ms", "21.5°C".</summary>
    public static string Metric(double value, int decimals, string unit) => Number(value, decimals) + unit;

    /// <summary>
    /// A stored reading as a sentence shows it: in the reader's units, or in the unit (or
    /// with the label) written in <paramref name="written"/> — see
    /// <see cref="Units.Display(double, string, Units.Preferences, string?)"/>. The metric
    /// tile goes through the same call, so a number in prose and on a card cannot disagree
    /// about Fahrenheit.
    /// </summary>
    public static string Metric(double stored, int decimals, string unit, Units.Preferences prefs, string? written = null)
    {
        var (value, shown) = Units.Display(stored, unit, prefs, written);
        return Metric(value, decimals, shown);
    }

    /// <summary>The word and dot colour for a connection's state, matching the service tile.</summary>
    public static (string Word, string Dot) Status(Connection connection, HealthMonitor.ProbeState? state) =>
        !connection.Enabled ? ("paused", "status-unknown")
        : state switch
        {
            // Still counted as up, but its last probe failed: amber, as the tile shows it.
            { IsUp: true, ConsecutiveFailures: > 0 } => ("up", "status-flapping"),
            { IsUp: true } => ("up", "status-up"),
            { IsUp: false } => ("down", "status-down"),
            _ => ("checking", "status-unknown"),
        };

    /// <summary>
    /// When a capacity runs out, written to follow the word "full" — "full in about 6
    /// weeks", "full now" — since that is how people write it in a sentence.
    /// </summary>
    public static string Forecast(CapacityForecast forecast) => forecast.State switch
    {
        ForecastState.Full => "now",
        ForecastState.Filling => CapacityForecast.Humanize(forecast.DaysLeft ?? 0),
        ForecastState.NotFilling => "not at the current rate",
        _ => "not known yet",
    };

    /// <summary>
    /// How long a connection has been as it is, written to follow "for" — "up for 3d 4h" —
    /// in the same two-unit shape as every "ago" on the dashboard, without the "ago".
    /// </summary>
    public static string Since(DateTimeOffset changed, DateTimeOffset now) => Ago.Since(changed, now) switch
    {
        "just now" => "under a minute",
        var ago when ago.EndsWith(" ago", StringComparison.Ordinal) => ago[..^4],
        var other => other,
    };

    /// <summary>Uptime as the uptime card writes it: one decimal place.</summary>
    public static string Uptime(double percent) => percent.ToString("0.0") + "%";
}
