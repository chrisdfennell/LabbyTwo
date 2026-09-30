using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>What a pinned quick action on the phone view does.</summary>
public enum PhonePinKind
{
    /// <summary>One of a connection's own action buttons — "wake the PC", "reboot the router".</summary>
    Action,

    /// <summary>Restart a container, by name, on a Docker connection — "restart Plex".</summary>
    Container,
}

/// <summary>
/// One quick action pinned to the phone view. Stored as a line of text in one app setting
/// rather than a table: there are a handful, they are a property of the installation, and a
/// setting comes back with a config export for free.
/// </summary>
/// <param name="ConnectionId">The connection whose action runs, or the Docker connection the container is on.</param>
/// <param name="Target">The action's id, or the container's name.</param>
/// <param name="Label">What the button says, or empty for the action's own label.</param>
public sealed record PhonePin(PhonePinKind Kind, string ConnectionId, string Target, string Label = "")
{
    /// <summary>
    /// <c>action|{connection}|{action}|{label}</c> or <c>container|{docker}|{name}|{label}</c>.
    /// A bar is not something an id, an action id or a container name can hold, and a label
    /// that contains one simply loses what follows it.
    /// </summary>
    public string Stored => string.Join('|', Kind == PhonePinKind.Container ? "container" : "action", ConnectionId, Target, Label.Replace('|', '/'));

    /// <summary>The pin a stored line describes, or null for one that is not a pin.</summary>
    public static PhonePin? Parse(string line)
    {
        var parts = line.Trim().Split('|', 4);
        if (parts.Length < 3 || parts[1].Trim().Length == 0 || parts[2].Trim().Length == 0)
            return null;
        PhonePinKind? kind = parts[0].Trim().ToLowerInvariant() switch
        {
            "action" => PhonePinKind.Action,
            "container" => PhonePinKind.Container,
            _ => null,
        };
        return kind is { } known
            ? new PhonePin(known, parts[1].Trim(), parts[2].Trim(), parts.Length > 3 ? parts[3].Trim() : "")
            : null;
    }

    /// <summary>Every pin in the setting, in the order they were pinned, without repeats.</summary>
    public static IReadOnlyList<PhonePin> ParseAll(string? stored) =>
    [
        .. (stored ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Parse)
            .OfType<PhonePin>()
            .DistinctBy(p => (p.Kind, p.ConnectionId, p.Target.ToLowerInvariant())),
    ];

    public static string Store(IEnumerable<PhonePin> pins) => string.Join('\n', pins.Select(p => p.Stored));

    /// <summary>Whether this pin is the same button as another, whatever either is labelled.</summary>
    public bool SameAs(PhonePin other) =>
        Kind == other.Kind && ConnectionId == other.ConnectionId && string.Equals(Target, other.Target, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The phone view's settings and the words it uses more than once. Pure, so the one-line
/// summary at the top — the sentence somebody reads before anything else — is pinned by a
/// test rather than by looking at a phone.
/// </summary>
public static class PhoneView
{
    /// <summary>The pinned quick actions, one <see cref="PhonePin.Stored"/> per line.</summary>
    public const string PinsKey = "phone_pins";

    /// <summary>"phone" to have the installed app open on the phone view; anything else opens the dashboard.</summary>
    public const string StartKey = "phone_start";

    public const string StartPhone = "phone";

    /// <summary>The route, in one place, because the manifest and the nav both name it.</summary>
    public const string Route = "m";

    /// <summary>
    /// A connection setting holding a link to its runbook note — <c>t/runbooks#note-abc123</c>.
    /// Kept to a path inside LabbyTwo (see <see cref="RunbookLink"/>), which is also what keeps
    /// it out of the host matching that finds which connections share a machine: a path has
    /// no host in it.
    /// </summary>
    public const string RunbookKey = "runbook_link";

    /// <summary>
    /// Which manifest the page links to. Two static files rather than one generated per
    /// request: a browser fetches the manifest without the login cookie, so it has to be
    /// something anybody may read, and a file is the least there is to get wrong.
    /// </summary>
    public static string Manifest(SettingsBag settings) =>
        OpensOnPhone(settings) ? "manifest-phone.webmanifest" : "manifest.webmanifest";

    public static bool OpensOnPhone(SettingsBag settings) =>
        string.Equals(settings.Get(StartKey), StartPhone, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A runbook link as it may be stored and followed: a path inside LabbyTwo, with no
    /// leading slash so it resolves against the base href. A full URL to this same page is
    /// cut down to its path, which is what somebody copying a note's link from the address
    /// bar pastes. Anything else — another site, a <c>javascript:</c> — is null: a link on
    /// the page that fixes outages must never be one that goes somewhere unexpected.
    /// </summary>
    public static string? RunbookLink(string? written)
    {
        var text = (written ?? "").Trim();
        if (text.Length == 0 || text.Length > 500 || text.Any(char.IsWhiteSpace))
            return null;

        // Protocol-relative: another site, however it is spelled.
        if (text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith(@"\\", StringComparison.Ordinal))
            return null;

        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return null;
            text = uri.PathAndQuery + uri.Fragment;
        }

        text = text.TrimStart('/');
        // A path of ours starts with a letter and has no scheme; "javascript:" does not survive this.
        if (text.Length == 0 || !char.IsAsciiLetter(text[0]) || text.Contains(':') || text.Contains('\\'))
            return null;
        return text;
    }

    /// <summary>The runbook link stored on a connection, if it has a usable one.</summary>
    public static string? RunbookFor(Connection connection) => RunbookLink(connection.Settings.Get(RunbookKey));

    /// <summary>
    /// The line at the top: "All 24 fine", or what is wrong in as few words as it takes —
    /// "2 down, 1 alert". Counts only what is red; late backups, incidents and family
    /// reports are listed underneath, and saying them here too would make the line long
    /// enough that nobody reads it.
    /// </summary>
    /// <param name="total">Monitored connections.</param>
    /// <param name="down">Of those, down.</param>
    /// <param name="checking">Of those, not yet checked since LabbyTwo started.</param>
    /// <param name="alerts">Alert rules firing.</param>
    /// <param name="other">Everything else listed as needing attention: late backups, open incidents, reports.</param>
    public static string Headline(int total, int down, int checking, int alerts, int other)
    {
        var parts = new List<string>();
        if (down > 0)
            parts.Add($"{down} down");
        if (alerts > 0)
            parts.Add(alerts == 1 ? "1 alert" : $"{alerts} alerts");
        if (parts.Count > 0)
            return string.Join(", ", parts);

        if (other > 0)
            return other == 1 ? "1 thing to look at" : $"{other} things to look at";
        if (total == 0)
            return "Nothing is monitored yet";
        if (checking == total)
            return "Checking…";
        if (checking > 0)
            return $"{(total - checking).ToString(CultureInfo.InvariantCulture)} fine, {checking} still checking";
        return total == 1 ? "All fine" : $"All {total} fine";
    }
}
