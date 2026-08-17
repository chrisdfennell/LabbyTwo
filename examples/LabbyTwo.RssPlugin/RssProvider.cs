using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using LabbyTwo.Core;
using LabbyTwo.Providers;

namespace LabbyTwo.RssPlugin;

/// <summary>
/// A feed. News, a subreddit, releases from a repository, the outage page of the ISP —
/// the one thing a start page has always been expected to do that LabbyTwo could not.
///
/// It is a provider rather than only a widget because a feed has numbers worth keeping: how
/// many items it carries, and how long it has been since the newest one. That second one is
/// the interesting half — a release feed that has gone quiet for ninety days is news, and
/// so is a status page that suddenly has three items in an hour.
/// </summary>
public sealed class RssProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public const string ProviderType = "rss";

    public string Type => ProviderType;
    public string DisplayName => "RSS / Atom feed";
    public string Icon => "📰";
    public string Category => "General";

    public string Description =>
        "Any RSS or Atom feed — headlines as a card, and how long it has been since the last item as a metric.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Feed URL", FieldKind.Url, "https://example.com/feed.xml", Required: true),

        new("limit", "Keep this many items", FieldKind.Number, Default: "20",
            Help: "How many are held for the card to show. The card has its own setting for how many it draws.")
        {
            Advanced = true,
        },

        new("timeout", "Timeout (seconds)", FieldKind.Number, Default: "15") { Advanced = true },
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("items", "Items in feed"),
        new("hours_since_latest", "Since newest item", " h", 1),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Feed has gone quiet", "hours_since_latest", Comparison.Above, 24 * 14, ForMinutes: 0,
            Why: "Nothing new in a fortnight. Worth having on a release feed or a project you depend on; "
                 + "pointless on a news site, so this is offered rather than created."),
    ];

    /// <summary>
    /// Fifteen minutes. A feed is republished on somebody else's schedule and asking every
    /// thirty seconds would be 2,880 requests a day for an answer that changes a handful of
    /// times — which is how you get politely blocked.
    /// </summary>
    public TimeSpan MinimumInterval => TimeSpan.FromMinutes(15);

    /// <summary>
    /// The last set of items per connection, for the card to draw. Metrics can carry
    /// numbers and nothing else, and a headline is not a number — this is the pattern the
    /// docs point at for exactly that, and the reason a provider being a singleton matters.
    /// </summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<FeedItem>> _items = new();

    public IReadOnlyList<FeedItem> ItemsFor(string connectionId) =>
        _items.GetValueOrDefault(connectionId, []);

    public sealed record FeedItem(string Title, string Link, DateTimeOffset? Published);

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var url = connection.Settings.Get("url");
        if (url.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No feed URL configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Some feeds serve HTML to anything that does not ask for a feed, and a few
            // block the default client string outright.
            request.Headers.TryAddWithoutValidation("Accept", "application/rss+xml, application/atom+xml, application/xml, text/xml;q=0.9, */*;q=0.8");
            request.Headers.TryAddWithoutValidation("User-Agent", "LabbyTwo");

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return ProbeResult.Down(stopwatch.Elapsed, $"HTTP {(int)response.StatusCode} from the feed.");

            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();

            var items = Parse(body, Math.Clamp(connection.Settings.GetInt("limit", 20), 1, 200));
            _items[connection.Id] = items;

            var metrics = new Dictionary<string, double>
            {
                ["items"] = items.Count,
                ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
            };

            var newest = items.Select(i => i.Published).OfType<DateTimeOffset>().DefaultIfEmpty().Max();
            if (newest != default)
                metrics["hours_since_latest"] = Math.Max(0, (DateTimeOffset.Now - newest).TotalHours);

            return ProbeResult.Up(stopwatch.Elapsed,
                items.Count == 0 ? "Read, but it has no items." : $"{items.Count} items, newest “{items[0].Title}”",
                metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// RSS 2.0 and Atom in one pass. They disagree about almost every name — item versus
    /// entry, link as text versus link as an href attribute, pubDate in RFC 822 versus
    /// updated in ISO 8601 — but they agree on the shape, so one reader that checks both
    /// spellings is smaller and less breakable than two.
    /// </summary>
    internal static IReadOnlyList<FeedItem> Parse(string xml, int limit)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidOperationException($"That did not parse as a feed: {ex.Message}");
        }

        var items = new List<FeedItem>();

        // Matched on local name so a feed's namespace — and there are several — never
        // decides whether its items are found.
        foreach (var node in document.Descendants()
                     .Where(e => e.Name.LocalName is "item" or "entry"))
        {
            var title = Child(node, "title")?.Value.Trim() ?? "";
            var link = Link(node);
            var published = Published(node);

            if (title.Length == 0 && link.Length == 0)
                continue;

            items.Add(new FeedItem(
                title.Length > 0 ? Collapse(title) : link,
                link,
                published));

            if (items.Count >= limit * 4)
                break; // sorted below, so read a few more than needed and then stop
        }

        return
        [
            .. items
                // Undated items keep the document's own order by sorting last rather than
                // being thrown to the top by a default DateTimeOffset of year 1.
                .OrderByDescending(i => i.Published ?? DateTimeOffset.MinValue)
                .Take(limit)
        ];
    }

    private static XElement? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    /// <summary>RSS puts the URL in the element's text; Atom puts it in an href attribute.</summary>
    private static string Link(XElement node)
    {
        foreach (var link in node.Elements().Where(e => e.Name.LocalName == "link"))
        {
            // Atom feeds carry several links; the alternate one is the article, and the
            // others are the feed itself, comments, or an enclosure.
            var relation = link.Attribute("rel")?.Value;
            if (relation is not null && relation != "alternate")
                continue;

            if (link.Attribute("href")?.Value is { Length: > 0 } href)
                return href.Trim();

            if (link.Value is { Length: > 0 } text)
                return text.Trim();
        }

        return Child(node, "guid")?.Value.Trim() is { Length: > 0 } guid
               && guid.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? guid
            : "";
    }

    private static DateTimeOffset? Published(XElement node)
    {
        foreach (var name in (string[])["pubDate", "published", "updated", "date"])
        {
            if (Child(node, name)?.Value.Trim() is not { Length: > 0 } raw)
                continue;

            // RFC 822 with a named zone, ISO 8601, and the several near-misses in between.
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed;

            if (DateTimeOffset.TryParseExact(raw, ["ddd, dd MMM yyyy HH:mm:ss zzz", "ddd, dd MMM yyyy HH:mm:ss 'GMT'"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exact))
                return exact;
        }

        return null;
    }

    /// <summary>Feed titles arrive with newlines and runs of spaces in them; a card row has one line.</summary>
    private static string Collapse(string value) =>
        string.Join(' ', value.Split((char[])['\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
}
