using System.Net;
using System.Security.Cryptography;
using System.Text;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LabbyTwo.StatusPagePlugin;

/// <summary>
/// The page itself: one self-contained HTML document, written by hand rather than rendered
/// by a component.
///
/// That is a deliberate choice and not a shortcut. A status page is read at exactly the
/// moment the rest of the house is misbehaving, on a phone, by somebody who does not have
/// an account — so it must not need a Blazor circuit, a WebSocket, a stylesheet fetch or a
/// second request of any kind. Everything is inline; the page renders from one GET and
/// nothing else. A component could not make that promise.
///
/// It also says less than the dashboard does, on purpose. No probe messages, no addresses,
/// no provider names — a failure message is written for the person who can fix it and
/// routinely contains an internal URL or a port. What a stranger gets is a name, a colour
/// and a percentage.
/// </summary>
public sealed class StatusPageEndpoints(
    ConfigStore config,
    HealthMonitor health,
    HistoryStore history,
    Registry registry) : IEndpointExtension
{
    public const string RouteKey = "status";

    public string Key => RouteKey;

    /// <summary>The reason this override exists. The token in the link is the authorisation.</summary>
    public bool RequiresAuthorization => false;

    /// <summary>
    /// One route only. "" and "/" are the same pattern once the group prefix is applied,
    /// and registering both is an ambiguous match rather than a kindness to whoever left
    /// the slash off.
    /// </summary>
    public void Map(IEndpointRouteBuilder routes) => routes.MapGet("", RenderAsync);

    private async Task<IResult> RenderAsync(HttpContext context, CancellationToken ct)
    {
        var pages = (await config.ConnectionsAsync(ct))
            .Where(c => c.Provider == StatusPageProvider.ProviderType && c.Enabled)
            .ToList();

        var presented = context.Request.Query["k"].ToString();
        var page = pages.FirstOrDefault(p => Matches(p.Settings.Get("token"), presented));

        // 404, not 401. A wrong token should be indistinguishable from nothing being here,
        // because anything else confirms to a scanner that there is a door to keep knocking
        // on — and unlike the Prometheus endpoint, there is no scrape config to debug.
        if (page is null)
            return Results.NotFound();

        var settings = page.Settings;
        var days = Math.Clamp(settings.GetInt("uptime_days", 7), 1, 90);
        var refresh = Math.Clamp(settings.GetInt("refresh_seconds", 60), 0, 3600);

        var published = Published(settings.Get("publish"));
        var connections = (await config.ConnectionsAsync(ct))
            .Where(c => c.Enabled)
            .Where(c => registry.Provider(c.Provider)?.IsMonitored != false)
            .Where(c => published is null || published.Contains(c.Name))
            .OrderBy(c => c.Sort)
            .ToList();

        var now = DateTimeOffset.Now;
        var rows = new List<Row>(connections.Count);

        foreach (var connection in connections)
        {
            var state = health.State(connection.Id);
            var uptime = await history.UptimeAsync(connection.Id, TimeSpan.FromDays(days), ct);
            rows.Add(new Row(connection.Name, connection.Icon, state?.IsUp, state?.ChangedAt, uptime));
        }

        var html = Page(settings, rows, days, refresh, now);

        // Never cached. A status page served from a phone's cache is a status page that is
        // confidently wrong about the thing you opened it to check.
        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private sealed record Row(string Name, string Icon, bool? IsUp, DateTimeOffset? Since, HistoryStore.Uptime Uptime);

    /// <summary>
    /// The publish list, or null for "everything monitored". Matched on the name as typed,
    /// ignoring case and surrounding space, because the list is retyped by hand and a
    /// trailing space should not quietly unpublish a service.
    /// </summary>
    private static HashSet<string>? Published(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return raw
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Page(SettingsBag settings, IReadOnlyList<Row> rows, int days, int refresh, DateTimeOffset now)
    {
        var title = settings.Get("title", "Service status");
        var footer = settings.Get("footer");

        var down = rows.Count(r => r.IsUp == false);
        var (bannerClass, bannerText) = down switch
        {
            0 when rows.Count == 0 => ("checking", "Nothing is being published yet."),
            0 => ("up", "All systems normal"),
            1 => ("down", "One service is down"),
            _ => ("down", $"{down} services are down"),
        };

        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.Append("<meta name=\"robots\" content=\"noindex,nofollow\">");

        if (refresh > 0)
            html.Append($"<meta http-equiv=\"refresh\" content=\"{refresh}\">");

        html.Append("<title>").Append(H(title)).Append("</title>");
        html.Append("<style>").Append(Css).Append("</style>");
        html.Append("</head><body><main>");

        html.Append("<h1>").Append(H(title)).Append("</h1>");
        html.Append($"<p class=\"banner {bannerClass}\">").Append(H(bannerText)).Append("</p>");

        html.Append("<ul class=\"services\">");
        foreach (var row in rows)
        {
            var (dotClass, label) = row.IsUp switch
            {
                true => ("up", "Operational"),
                false => ("down", "Down"),
                null => ("checking", "Checking"),
            };

            html.Append("<li>");
            html.Append("<span class=\"name\">");
            if (row.Icon is { Length: > 0 })
                html.Append("<span class=\"icon\">").Append(H(row.Icon)).Append("</span>");
            html.Append(H(row.Name)).Append("</span>");

            html.Append("<span class=\"meta\">");

            // Only shown once there is enough history to mean something. A percentage from
            // four samples is a number pretending to be a measurement.
            if (row.Uptime.Samples > 0)
                html.Append($"<span class=\"uptime\">{row.Uptime.Percent:0.##}% / {days}d</span>");

            if (row.Since is { } since && row.IsUp is not null)
                html.Append("<span class=\"since\">")
                    .Append(H($"{(row.IsUp == true ? "up" : "down")} for {Held(now - since)}"))
                    .Append("</span>");

            html.Append($"<span class=\"dot {dotClass}\"></span>");
            html.Append("<span class=\"state\">").Append(H(label)).Append("</span>");
            html.Append("</span></li>");
        }
        html.Append("</ul>");

        if (footer is { Length: > 0 })
            html.Append("<p class=\"footer\">").Append(H(footer)).Append("</p>");

        html.Append("<p class=\"stamp\">Checked ").Append(H(now.ToString("HH:mm"))).Append("</p>");
        html.Append("</main></body></html>");

        return html.ToString();
    }

    /// <summary>
    /// How long it has been in this state, phrased as a duration rather than as a moment.
    /// <see cref="Ago"/> is the house helper for this and is the wrong shape here: it
    /// answers "when did that happen" — "5h ago" — and a status row is asking "how long has
    /// this been true". Bolting the two together gives "down 0m ago" for something that
    /// just fell over, which is both ugly and slightly untrue.
    /// </summary>
    private static string Held(TimeSpan held)
    {
        if (held < TimeSpan.Zero)
            held = TimeSpan.Zero;

        if (held.TotalMinutes < 1)
            return "less than a minute";

        if (held.TotalHours < 1)
            return $"{held.Minutes}m";

        if (held.TotalDays < 1)
            return held.Minutes == 0 ? $"{held.Hours}h" : $"{held.Hours}h {held.Minutes}m";

        var days = (int)held.TotalDays;
        return held.Hours == 0 ? $"{days}d" : $"{days}d {held.Hours}h";
    }

    /// <summary>
    /// Inline and small. It has to work with no network beyond the one request, and it has
    /// to be legible on a phone in the dark, which is most of what the media query is for.
    /// </summary>
    private const string Css = """
        :root { color-scheme: light dark; --bg:#f6f7f9; --card:#fff; --line:#e4e6eb; --text:#1a1d21; --muted:#6b7280; --up:#35d07f; --down:#ff5c6c; --wait:#9aa4b2; }
        @media (prefers-color-scheme: dark) { :root { --bg:#14171c; --card:#1c2027; --line:#2b313a; --text:#e8eaed; --muted:#9aa4b2; } }
        * { box-sizing: border-box; }
        body { margin:0; padding:2rem 1rem; background:var(--bg); color:var(--text);
               font:16px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif; }
        main { max-width:44rem; margin:0 auto; }
        h1 { font-size:1.5rem; margin:0 0 1rem; }
        .banner { margin:0 0 1.5rem; padding:.85rem 1rem; border-radius:.6rem; font-weight:600;
                  background:var(--card); border:1px solid var(--line); border-left:4px solid var(--wait); }
        .banner.up { border-left-color:var(--up); }
        .banner.down { border-left-color:var(--down); }
        .services { list-style:none; margin:0; padding:0; background:var(--card);
                    border:1px solid var(--line); border-radius:.6rem; overflow:hidden; }
        .services li { display:flex; align-items:center; justify-content:space-between; gap:1rem;
                       padding:.85rem 1rem; border-top:1px solid var(--line); }
        .services li:first-child { border-top:0; }
        .name { display:flex; align-items:center; gap:.5rem; font-weight:500; min-width:0; }
        .name .icon { flex:none; }
        .meta { display:flex; align-items:center; gap:.75rem; color:var(--muted); font-size:.875rem; flex:none; }
        .uptime, .since { font-variant-numeric:tabular-nums; }
        .dot { width:.6rem; height:.6rem; border-radius:50%; background:var(--wait); flex:none; }
        .dot.up { background:var(--up); }
        .dot.down { background:var(--down); }
        .state { min-width:5.5rem; text-align:right; }
        .footer { margin:1.5rem 0 0; color:var(--muted); }
        .stamp { margin:.5rem 0 0; color:var(--muted); font-size:.8125rem; }
        @media (max-width:30rem) {
          .services li { flex-direction:column; align-items:flex-start; gap:.4rem; }
          .state { text-align:left; min-width:0; }
        }
        """;

    /// <summary>
    /// Everything user-typed goes through here on its way into the document. Connection
    /// names, the title and the footer are all free text typed by somebody who may well be
    /// the only person with a login — but "the only author is trusted" is how every
    /// hand-built HTML string eventually grows an injection.
    /// </summary>
    private static string H(string value) => WebUtility.HtmlEncode(value);

    private static bool Matches(string expected, string presented) =>
        expected.Length > 0
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(presented));
}
