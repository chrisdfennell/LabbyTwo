using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabbyTwo.Core;

/// <summary>
/// The family status page as the owner set it up: whether it is on, the link that opens it,
/// and which connections it shows under what friendly names.
///
/// The page exists for the people in the house who do not care what a probe is — "is Plex
/// broken or is it the TV?" — and so everything on it is chosen, not derived. A connection
/// the owner has not added is not on the page at all, whatever state it is in, and one that
/// has been added is shown by the name the owner gave it, never by the connection's own name,
/// which is routinely a container or a host name.
///
/// Stored as one JSON app setting rather than a table: it is a dozen rows at most, always
/// read and written whole, and a property of the installation rather than of any connection.
/// </summary>
/// <param name="Enabled">Off means every family URL answers 404, as if it had never existed.</param>
/// <param name="Token">The secret in the share link. Empty until the owner creates one.</param>
/// <param name="LanAccess">Whether <c>/family</c> without a token also works, for devices on the home network.</param>
/// <param name="Title">The page's heading. Empty uses <see cref="DefaultTitle"/>.</param>
/// <param name="Items">What the page shows, in the order it shows them.</param>
public sealed record FamilyStatusSettings(
    bool Enabled,
    string Token,
    bool LanAccess,
    string Title,
    IReadOnlyList<FamilyItem> Items)
{
    public const string Key = "family_status";

    public const string DefaultTitle = "Is everything working?";

    /// <summary>The longest friendly name or group. Long enough for "Kids' tablet games", short enough to fit a phone.</summary>
    public const int MaxName = 40;

    /// <summary>An emoji or two; anything longer is not an icon.</summary>
    public const int MaxIcon = 16;

    /// <summary>More than anybody would want to read on a phone, and a bound on the page's size.</summary>
    public const int MaxItems = 50;

    public static FamilyStatusSettings Default => new(false, "", false, "", []);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Reads the stored settings. Anything unreadable is treated as "never set up" rather than
    /// thrown: a page that anonymous visitors hit must not become a 500 because of a bad row.
    /// </summary>
    public static FamilyStatusSettings From(SettingsBag settings) => Parse(settings.Get(Key));

    public static FamilyStatusSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Default;
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json, Json);
            if (stored is null)
                return Default;
            return new FamilyStatusSettings(
                stored.Enabled,
                stored.Token ?? "",
                stored.LanAccess,
                stored.Title ?? "",
                [.. (stored.Items ?? []).Where(i => i is not null).Select(i => i!.Normalised()).Take(MaxItems)]);
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(
        new Stored(Enabled, Token, LanAccess, Title, [.. Items]), Json);

    /// <summary>The heading the page shows.</summary>
    public string Heading => string.IsNullOrWhiteSpace(Title) ? DefaultTitle : Title.Trim();

    /// <summary>
    /// A fresh link secret: 128 random bits, URL-safe base64 without padding — 22 characters,
    /// short enough to paste into a family chat and far past guessing, even unthrottled.
    /// </summary>
    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>
    /// Whether <paramref name="presented"/> is this page's token. Never true for an empty
    /// token on either side — a feature with no link must not open to an empty path segment.
    /// Compared over hashes so the time taken says nothing about how much of a guess was right.
    /// </summary>
    public bool Matches(string? presented)
    {
        if (Token.Length == 0 || string.IsNullOrEmpty(presented) || presented.Length > 200)
            return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(Token)));
    }

    /// <summary>The group names in the order they first appear, for the owner's picker and the page.</summary>
    public IReadOnlyList<string> Groups => [.. Items.Select(i => i.Group).Distinct(StringComparer.OrdinalIgnoreCase)];

    private sealed record Stored(bool Enabled, string? Token, bool LanAccess, string? Title, List<FamilyItem?>? Items);
}

/// <summary>
/// One line on the family page.
/// </summary>
/// <param name="Id">
/// The item's own id, which is what the report form sends back. Its own rather than the
/// connection's, so the page names nothing inside LabbyTwo — not even an opaque key a
/// visitor could try elsewhere.
/// </param>
/// <param name="ConnectionId">The connection whose state it shows. Never rendered.</param>
/// <param name="Name">What the family calls it: "Plex", "The internet", "Front door camera".</param>
/// <param name="Icon">An emoji, or empty.</param>
/// <param name="Group">The heading it sits under; empty sits under no heading.</param>
public sealed record FamilyItem(string Id, string ConnectionId, string Name, string Icon, string Group)
{
    /// <summary>Trimmed and cut to length, so a hand-edited row cannot stretch the page.</summary>
    public FamilyItem Normalised() => new(
        string.IsNullOrWhiteSpace(Id) ? Ids.New() : Id.Trim(),
        ConnectionId?.Trim() ?? "",
        FamilyText.Clean(Name, FamilyStatusSettings.MaxName),
        FamilyText.Clean(Icon, FamilyStatusSettings.MaxIcon),
        FamilyText.Clean(Group, FamilyStatusSettings.MaxName));
}

/// <summary>The four things a family member is told. Nothing finer, on purpose.</summary>
public enum FamilyState
{
    /// <summary>Not checked yet since LabbyTwo started, or the connection has gone. Shown as "Checking…".</summary>
    Unknown,
    Working,
    Trouble,
    Down,
    Maintenance,
}

/// <summary>
/// One row as the page draws it. This record is the whole of what an anonymous visitor
/// can learn about a service: a name and an icon the owner typed, a state and when it
/// began. It is deliberately built from nothing else, so a probe message, a host name or
/// a metric cannot reach the page by somebody adding a line to the markup.
/// </summary>
public sealed record FamilyRow(string Id, string Name, string Icon, FamilyState State, DateTimeOffset? Since)
{
    public string Word => FamilyWords.State(State);
}

/// <summary>A heading and its rows. An empty heading is the rows the owner left ungrouped.</summary>
public sealed record FamilyGroup(string Name, IReadOnlyList<FamilyRow> Rows);

/// <summary>Everything the family page shows.</summary>
/// <param name="MaintenanceUntil">When a maintenance window with an end is on, when it ends.</param>
public sealed record FamilyView(
    string Title,
    IReadOnlyList<FamilyGroup> Groups,
    bool MaintenanceOn,
    DateTimeOffset? MaintenanceUntil,
    DateTimeOffset At)
{
    public IEnumerable<FamilyRow> Rows => Groups.SelectMany(g => g.Rows);

    /// <summary>The worst thing on the page, which is what the big line at the top says.</summary>
    public FamilyState Overall
    {
        get
        {
            var states = Rows.Select(r => r.State).ToList();
            if (states.Contains(FamilyState.Down))
                return FamilyState.Down;
            if (states.Contains(FamilyState.Trouble))
                return FamilyState.Trouble;
            if (states.Contains(FamilyState.Maintenance))
                return FamilyState.Maintenance;
            if (states.Count > 0 && states.All(s => s == FamilyState.Unknown))
                return FamilyState.Unknown;
            return FamilyState.Working;
        }
    }

    /// <summary>
    /// What the page knows about each connection, reduced to the one question it asks. Only
    /// these facts go into a view — a state lookup rather than the monitor's own records, so
    /// nothing else the monitor knows is in reach.
    /// </summary>
    /// <param name="IsUp">Null when not checked yet.</param>
    /// <param name="Failing">Failing checks that have not yet reached "down".</param>
    /// <param name="ChangedAt">When it last went up or down.</param>
    public sealed record Probe(bool? IsUp, bool Failing, DateTimeOffset? ChangedAt);

    /// <summary>
    /// Builds the page from the owner's list. Items whose connection no longer exists are
    /// dropped rather than shown as broken: somebody deleted it on purpose.
    /// </summary>
    /// <param name="connections">Every connection, by id — only those the owner listed are read.</param>
    /// <param name="probe">What the monitor last found for a connection id, or null.</param>
    public static FamilyView Build(
        FamilyStatusSettings settings,
        IReadOnlyDictionary<string, Connection> connections,
        Func<string, Probe?> probe,
        Maintenance maintenance,
        DateTimeOffset now)
    {
        var groups = new List<FamilyGroup>();
        foreach (var group in settings.Items.GroupBy(i => i.Group, StringComparer.OrdinalIgnoreCase))
        {
            var rows = new List<FamilyRow>();
            foreach (var item in group)
            {
                if (!connections.TryGetValue(item.ConnectionId, out var connection))
                    continue;
                var (state, since) = StateOf(connection, probe(connection.Id), maintenance, now);
                rows.Add(new FamilyRow(item.Id, item.Name.Length > 0 ? item.Name : "Unnamed", item.Icon, state, since));
            }
            if (rows.Count > 0)
                groups.Add(new FamilyGroup(group.First().Group, rows));
        }
        return new FamilyView(settings.Heading, groups, maintenance.On, maintenance.Until, now);
    }

    /// <summary>
    /// One connection's state in the family's words.
    ///
    /// Maintenance only covers something that is not working: a service that stayed up
    /// through the owner's tinkering is simply working, and saying "under maintenance" about
    /// it would have somebody not even try. A switched-off connection or one the owner has
    /// silenced counts as maintenance too — both mean "I know, I'm on it".
    /// </summary>
    public static (FamilyState State, DateTimeOffset? Since) StateOf(
        Connection connection, Probe? probe, Maintenance maintenance, DateTimeOffset now)
    {
        var working = probe is { IsUp: true, Failing: false };
        var held = maintenance.On || connection.IsSilenced(now) || !connection.Enabled;

        if (held && !working)
            return (FamilyState.Maintenance, probe?.IsUp == false ? probe.ChangedAt : null);

        return probe switch
        {
            null or { IsUp: null } => (FamilyState.Unknown, null),
            { IsUp: false } => (FamilyState.Down, probe.ChangedAt),
            { Failing: true } => (FamilyState.Trouble, null),
            _ => (FamilyState.Working, probe.ChangedAt),
        };
    }
}

/// <summary>The words the page uses. In one place so the page, the tests and the README agree.</summary>
public static class FamilyWords
{
    public static string State(FamilyState state) => state switch
    {
        FamilyState.Working => "Working",
        FamilyState.Trouble => "Having trouble",
        FamilyState.Down => "Down",
        FamilyState.Maintenance => "Under maintenance",
        _ => "Checking…",
    };

    /// <summary>The line at the top of the page.</summary>
    public static string Overall(FamilyState state) => state switch
    {
        FamilyState.Down => "Something isn't working",
        FamilyState.Trouble => "Something is having trouble",
        FamilyState.Maintenance => "Some things are being worked on",
        FamilyState.Unknown => "Checking…",
        _ => "Everything is working",
    };

    /// <summary>
    /// "since 20 min ago", "since 3 hours ago". Coarse on purpose: nobody in the family needs
    /// the second, and a precise time is one more thing to read.
    /// </summary>
    public static string Since(DateTimeOffset at, DateTimeOffset now)
    {
        var elapsed = now - at;
        if (elapsed < TimeSpan.FromMinutes(1))
            return "since just now";
        if (elapsed < TimeSpan.FromHours(1))
            return $"since {(int)elapsed.TotalMinutes} min ago";
        if (elapsed < TimeSpan.FromDays(1))
        {
            var hours = (int)elapsed.TotalHours;
            return hours == 1 ? "since an hour ago" : $"since {hours} hours ago";
        }
        var days = (int)elapsed.TotalDays;
        return days == 1 ? "since yesterday" : $"since {days} days ago";
    }

    /// <summary>"Back by about 14:30", for a maintenance window with an end.</summary>
    public static string BackBy(DateTimeOffset until) =>
        "Back by about " + until.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// Text typed by somebody who is not signed in: the report message and name, and the
/// owner's own friendly names. Treated as text and nothing else, everywhere it goes.
/// </summary>
public static class FamilyText
{
    /// <summary>The report message: a sentence or two, not an essay.</summary>
    public const int MaxMessage = 280;

    /// <summary>The reporter's name: "Mum", "Sam's iPad".</summary>
    public const int MaxReporter = 40;

    /// <summary>
    /// Trimmed, on one line, without control or formatting characters, and at most
    /// <paramref name="max"/> characters — cut on a whole character, never through the
    /// middle of a surrogate pair or an emoji's joiner sequence into invalid UTF-16.
    ///
    /// Newlines are folded to spaces because every place this is shown is one line, and a
    /// notification channel treats a newline as structure. Bidirectional overrides and
    /// zero-width characters go too: they are how a short message is made to read as
    /// something other than what it says.
    /// </summary>
    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var builder = new StringBuilder(Math.Min(text.Length, max * 2));
        var lastWasSpace = true;
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var isSpace = Rune.IsWhiteSpace(rune) || category is UnicodeCategory.Control;
            if (isSpace)
            {
                if (!lastWasSpace)
                    builder.Append(' ');
                lastWasSpace = true;
                continue;
            }
            // Format characters are invisible — bidi overrides, zero-width spaces — except
            // the zero-width joiner, which is what holds a family emoji together.
            if (category is UnicodeCategory.Format && rune.Value != 0x200D)
                continue;
            builder.Append(rune.ToString());
            lastWasSpace = false;
        }

        var cleaned = builder.ToString().Trim();
        if (cleaned.Length <= max)
            return cleaned;

        // Cut on a text-element boundary so an emoji is never split into half a character.
        var cut = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(cleaned);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            if (cut.Length + element.Length > max)
                break;
            cut.Append(element);
        }
        return cut.ToString().TrimEnd();
    }

    /// <summary>
    /// Report text for a notification that is shown as plain text — email, a phone push,
    /// ntfy, Slack's attachment. Still text; the changes are only to what chat services
    /// act on by themselves: an @ that would ping a whole server, and the angle brackets
    /// Slack uses for its <c>&lt;!channel&gt;</c> links.
    /// </summary>
    public static string ForPlainNotification(string text) =>
        text.Replace("@", "@​").Replace('<', '‹').Replace('>', '›');

    /// <summary>
    /// Report text for a notification that renders Markdown (a Discord embed): every
    /// character Markdown gives meaning to is escaped, so what arrives is exactly what was
    /// typed — "**urgent**" shows its asterisks, and a [link](…) is not a link.
    /// </summary>
    public static string ForMarkdownNotification(string text)
    {
        var builder = new StringBuilder(text.Length + 16);
        foreach (var c in ForPlainNotification(text))
        {
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '[' or ']' or '(' or ')' or '#')
                builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }
}
