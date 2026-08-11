using System.Collections.Concurrent;
using System.Diagnostics;
using LabbyTwo.Core;

namespace LabbyTwo.GoogleCalendarPlugin;

/// <summary>
/// A Google calendar you can write to. The ICS plugin next door reads a published feed,
/// which is simpler and enough for a bin collection schedule — but a feed is a file Google
/// publishes, so nothing typed into a dashboard can ever travel back up it. This one talks
/// to the Calendar API, which means events added here appear on every phone in the family,
/// and edits made on a phone appear here in seconds rather than whenever Google's feed
/// cache catches up.
///
/// The cost is OAuth. See <see cref="GoogleOAuth"/> for why connecting takes a paste.
/// </summary>
public sealed class GoogleCalendarProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "google-calendar";
    public string DisplayName => "Google Calendar";
    public string Icon => "📆";
    public string Category => "Home";
    public string Description =>
        "A Google calendar, read and write — month, week and list views, and events you can add from here.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("calendar_id", "Calendar ID", FieldKind.Text, "family0123456789%40group.calendar.google.com",
            Default: "primary", Required: true,
            Help: "Google Calendar → Settings → the calendar → Integrate calendar → Calendar ID. " +
                  "\"primary\" means the main calendar of whichever account you authorise."),

        new("client_id", "OAuth client ID", FieldKind.Text, "…apps.googleusercontent.com", Required: true,
            Help: "From a Google Cloud project: APIs & Services → Credentials → Create credentials → " +
                  "OAuth client ID → Web application. Enable the Google Calendar API in that project too."),

        new("client_secret", "OAuth client secret", FieldKind.Password, Required: true),

        new("redirect_uri", "Redirect URI", FieldKind.Text,
            Default: "http://127.0.0.1:5150/oauth2callback",
            Help: "Must match one of the client's \"Authorised redirect URIs\" exactly. Google refuses a " +
                  "LAN address, so the default deliberately points nowhere: you copy the code out of the " +
                  "browser's address bar. If LabbyTwo has an https address, use https://your-host/ext/" +
                  "google-calendar/callback instead and the connect step completes by itself."),

        new("refresh_token", "Refresh token", FieldKind.Password,
            Help: "Filled in for you when you connect on the calendar page. Clearing it disconnects."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("events_today", "Events today"),
        new("events_ahead", "Events coming up"),
        new("hours_to_next", "Until the next one", " h", 1),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Calendar unreachable", "latency_ms", Comparison.Above, 10000, ForMinutes: 15,
            Why: "Google being slow matters little; this is really here to notice a revoked token, " +
                 "which shows up as the connection going down."),
    ];

    // ---- tokens ------------------------------------------------------------------------

    // An access token lasts an hour. Cached per connection so a page that draws three
    // months does not exchange the refresh token three times.
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> _tokens = new();

    private readonly ConcurrentDictionary<string, (IReadOnlyList<CalEvent> Events, DateTimeOffset At)> _events = new();

    private readonly GoogleCalendarApi _api = new(httpFactory);

    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    public static bool IsConnected(Connection connection) =>
        connection.Settings.Get("refresh_token").Length > 0;

    /// <summary>
    /// Where to send someone to grant access. <paramref name="state"/> comes back on the
    /// redirect, so the callback knows which connection it is completing.
    /// </summary>
    public static string AuthorizationUrl(Connection connection) =>
        GoogleOAuth.AuthorizationUrl(
            connection.Settings.Get("client_id"),
            RedirectUri(connection),
            connection.Id);

    public static string RedirectUri(Connection connection) =>
        connection.Settings.Get("redirect_uri", "http://127.0.0.1:5150/oauth2callback");

    /// <summary>
    /// Turns the one-time code into a refresh token. The caller saves it onto the
    /// connection — a provider cannot inject <c>ConfigStore</c> without a dependency cycle,
    /// since the store asks the registry about providers.
    /// </summary>
    public async Task<string> ExchangeAsync(Connection connection, string code, CancellationToken ct)
    {
        var tokens = await GoogleOAuth.ExchangeAsync(
            httpFactory,
            connection.Settings.Get("client_id"),
            connection.Settings.Get("client_secret"),
            code,
            RedirectUri(connection),
            ct);

        if (tokens.RefreshToken.Length == 0)
            throw new InvalidOperationException(
                "Google returned an access token but no refresh token, which happens when this app was " +
                "already authorised. Remove LabbyTwo at myaccount.google.com/permissions and connect again.");

        _tokens[connection.Id] = (tokens.AccessToken, tokens.ExpiresAt);
        return tokens.RefreshToken;
    }

    private async Task<string> AccessTokenAsync(Connection connection, CancellationToken ct)
    {
        if (_tokens.TryGetValue(connection.Id, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Token;

        var refresh = connection.Settings.Get("refresh_token");
        if (refresh.Length == 0)
            throw new InvalidOperationException(
                "Not connected to Google yet — open this calendar's page and use Connect.");

        var tokens = await GoogleOAuth.RefreshAsync(
            httpFactory,
            connection.Settings.Get("client_id"),
            connection.Settings.Get("client_secret"),
            refresh,
            ct);

        _tokens[connection.Id] = (tokens.AccessToken, tokens.ExpiresAt);
        return tokens.AccessToken;
    }

    /// <summary>Forgets the cached token and events, so the next read starts clean.</summary>
    public void Forget(Connection connection)
    {
        _tokens.TryRemove(connection.Id, out _);
        foreach (var key in _events.Keys.Where(k => k.StartsWith(connection.Id, StringComparison.Ordinal)))
            _events.TryRemove(key, out _);
    }

    // ---- reading and writing -------------------------------------------------------------

    /// <summary>
    /// Events overlapping a window. Cached for a minute per window, because moving between
    /// months and back is the normal way to use a calendar and should not re-fetch.
    /// </summary>
    public async Task<IReadOnlyList<CalEvent>> EventsAsync(
        Connection connection, DateTimeOffset from, DateTimeOffset to, bool fresh, CancellationToken ct)
    {
        var key = $"{connection.Id}|{from:yyyyMMdd}|{to:yyyyMMdd}";

        if (!fresh && _events.TryGetValue(key, out var cached) && DateTimeOffset.Now - cached.At < CacheFor)
            return cached.Events;

        var events = await _api.ListAsync(
            await AccessTokenAsync(connection, ct), CalendarId(connection), from, to, ct);

        _events[key] = (events, DateTimeOffset.Now);
        return events;
    }

    public async Task AddAsync(Connection connection, CalEvent draft, CancellationToken ct)
    {
        await _api.InsertAsync(await AccessTokenAsync(connection, ct), CalendarId(connection), draft, ct);
        Invalidate(connection);
    }

    public async Task UpdateAsync(Connection connection, CalEvent changed, CancellationToken ct)
    {
        await _api.UpdateAsync(await AccessTokenAsync(connection, ct), CalendarId(connection), changed, ct);
        Invalidate(connection);
    }

    public async Task DeleteAsync(Connection connection, string eventId, CancellationToken ct)
    {
        await _api.DeleteAsync(await AccessTokenAsync(connection, ct), CalendarId(connection), eventId, ct);
        Invalidate(connection);
    }

    public async Task<string> CalendarNameAsync(Connection connection, CancellationToken ct)
    {
        try
        {
            return await _api.NameAsync(await AccessTokenAsync(connection, ct), CalendarId(connection), ct);
        }
        catch (Exception)
        {
            // A name is decoration. Never let it be the reason a page fails.
            return CalendarId(connection);
        }
    }

    private static string CalendarId(Connection connection) => connection.Settings.Get("calendar_id", "primary");

    /// <summary>Drops the cache after a write, so the grid redraws with what was just saved.</summary>
    private void Invalidate(Connection connection)
    {
        foreach (var key in _events.Keys.Where(k => k.StartsWith(connection.Id, StringComparison.Ordinal)))
            _events.TryRemove(key, out _);
    }

    // ---- probing ---------------------------------------------------------------------------

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        if (connection.Settings.Get("client_id").Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No OAuth client ID — add one from your Google Cloud project.");

        if (!IsConnected(connection))
            return ProbeResult.Down(TimeSpan.Zero,
                "Not connected to Google yet. Open this calendar's page and use Connect.");

        try
        {
            var now = DateTimeOffset.Now;
            var events = await EventsAsync(connection, now.Date, now.Date.AddDays(14), fresh: false, ct);
            stopwatch.Stop();

            var today = DateOnly.FromDateTime(now.LocalDateTime);
            var todayCount = events.Count(e => e.OnDay(today));
            var next = events.FirstOrDefault(e => e.Start > now);

            var metrics = new Dictionary<string, double>
            {
                ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
                ["events_today"] = todayCount,
                ["events_ahead"] = events.Count(e => e.Start > now),
            };
            if (next is not null)
                metrics["hours_to_next"] = (next.Start - now).TotalHours;

            var message = todayCount switch
            {
                0 when next is null => "Nothing scheduled",
                0 => $"Nothing today — next: {next.Summary}",
                1 => "1 event today",
                _ => $"{todayCount} events today",
            };

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ex.GetBaseException().Message);
        }
    }
}
