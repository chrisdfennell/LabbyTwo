using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LabbyTwo.Providers;

namespace LabbyTwo.Services;

/// <summary>
/// Fetches and caches site icons for bookmarks. Server-side rather than pointing an
/// &lt;img&gt; straight at the site, for three reasons: a LAN service is often reachable
/// from the LabbyTwo host but not from a phone on the guest network, half of them serve
/// no /favicon.ico and need the HTML read to find the real one, and a broken icon should
/// fail once here instead of on every page load in every browser.
/// </summary>
public sealed partial class FaviconService(IHttpClientFactory httpFactory, ILogger<FaviconService> log)
{
    /// <summary>An icon and its content type, or a negative entry when the site has none.</summary>
    public sealed record Icon(byte[] Bytes, string ContentType)
    {
        public bool Found => Bytes.Length > 0;
        public static readonly Icon None = new([], "");
    }

    private readonly ConcurrentDictionary<string, (Icon Icon, DateTimeOffset At)> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Long enough that a wall dashboard never refetches, short enough that replacing a
    // service's icon shows up the same day.
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromHours(12);

    // A site with no icon is the common case on a LAN, and retrying it every render would
    // be a slow request per card. Retry occasionally in case one gets added.
    private static readonly TimeSpan FailureTtl = TimeSpan.FromHours(1);

    private const int MaxBytes = 512 * 1024;

    public async Task<Icon> GetAsync(string url, CancellationToken ct = default)
    {
        if (!TryOrigin(url, out var origin))
            return Icon.None;

        if (_cache.TryGetValue(origin, out var cached) &&
            DateTimeOffset.UtcNow - cached.At < (cached.Icon.Found ? SuccessTtl : FailureTtl))
            return cached.Icon;

        var icon = await FetchAsync(origin, ct);
        _cache[origin] = (icon, DateTimeOffset.UtcNow);
        return icon;
    }

    /// <summary>
    /// Cache key and fetch target are the origin, not the full URL — every bookmark into
    /// the same Proxmox pulls one icon, not one per deep link.
    /// </summary>
    private static bool TryOrigin(string url, out string origin)
    {
        origin = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private async Task<Icon> FetchAsync(string origin, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));

        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);

            foreach (var candidate in await CandidatesAsync(http, origin, timeout.Token))
            {
                var icon = await TryDownloadAsync(http, candidate, timeout.Token);
                if (icon.Found)
                    return icon;
            }
        }
        catch (Exception ex)
        {
            // A missing icon is cosmetic. Log at debug so an unreachable LAN host does not
            // fill the log every twelve hours.
            log.LogDebug(ex, "No favicon for {Origin}", origin);
        }

        return Icon.None;
    }

    /// <summary>
    /// Icons declared in the page's own HTML first — they are the ones that actually
    /// exist and are usually a decent size — then the conventional paths.
    /// </summary>
    private async Task<List<string>> CandidatesAsync(HttpClient http, string origin, CancellationToken ct)
    {
        var candidates = new List<string>();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, origin);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode &&
                response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
            {
                // Only the head matters and some dashboards serve megabytes; read a slice.
                var buffer = new char[32 * 1024];
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
                var read = await reader.ReadBlockAsync(buffer, ct);
                var html = new string(buffer, 0, read);

                foreach (Match match in IconLink().Matches(html))
                {
                    var href = match.Groups["href"].Value;
                    if (href.Length > 0 && Uri.TryCreate(new Uri(origin), href, out var resolved))
                        candidates.Add(resolved.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not read {Origin} for icon links", origin);
        }

        candidates.Add($"{origin}/favicon.ico");
        candidates.Add($"{origin}/favicon.png");
        candidates.Add($"{origin}/apple-touch-icon.png");
        return candidates;
    }

    private async Task<Icon> TryDownloadAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return Icon.None;

            var type = response.Content.Headers.ContentType?.MediaType ?? "";

            // A single-page app that answers every path with index.html would otherwise
            // give every bookmark an "icon" that is a page of HTML.
            if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                type != "application/octet-stream")
                return Icon.None;

            if (response.Content.Headers.ContentLength > MaxBytes)
                return Icon.None;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0 || bytes.Length > MaxBytes)
                return Icon.None;

            return new Icon(bytes, type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? type : "image/x-icon");
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Icon candidate {Url} failed", url);
            return Icon.None;
        }
    }

    // <link rel="… icon …" href="…"> in either attribute order. A parser would be more
    // correct; this reads a <head> that a browser would also forgive.
    [GeneratedRegex(
        """<link\b(?=[^>]*\brel\s*=\s*["'][^"']*\bicon\b)[^>]*\bhref\s*=\s*["'](?<href>[^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IconLink();
}
