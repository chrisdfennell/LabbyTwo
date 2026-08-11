using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LabbyTwo.Providers;

namespace LabbyTwo.GoogleCalendarPlugin;

/// <summary>One event, in the terms this plugin renders and edits.</summary>
/// <param name="Id">Google's event id. Empty for an event that has not been saved yet.</param>
public sealed record CalEvent(
    string Id,
    string Summary,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool AllDay,
    string Location = "",
    string Description = "")
{
    public bool IsNow(DateTimeOffset now) => Start <= now && End > now;

    /// <summary>
    /// Whether this event touches a given day. Spans matter: a three-day trip has to
    /// appear on all three days of a month grid, not only the day it started.
    /// </summary>
    public bool OnDay(DateOnly day)
    {
        var from = DateOnly.FromDateTime(Start.LocalDateTime);
        // An all-day event's end is exclusive in Google's model — a one-day event ends the
        // next morning — so step back a day before comparing, or every one of them would
        // bleed into tomorrow.
        var to = DateOnly.FromDateTime((AllDay ? End.AddDays(-1) : End).LocalDateTime);
        if (to < from)
            to = from;
        return day >= from && day <= to;
    }

    public string TimeLabel => AllDay ? "all day" : $"{Start:HH:mm}–{End:HH:mm}";
}

/// <summary>
/// The OAuth half. Google will not redirect to a LAN address — a redirect URI has to be
/// https or loopback — so the flow this supports is: send the user to Google, let the
/// browser dead-end on 127.0.0.1, and have them paste the <c>code</c> back. If LabbyTwo is
/// reachable over https, point <c>redirect_uri</c> at
/// <see cref="GoogleCalendarEndpoints"/> instead and the round trip completes itself.
/// </summary>
public static class GoogleOAuth
{
    /// <summary>Read and write events on the user's calendars. Nothing else is asked for.</summary>
    public const string Scope = "https://www.googleapis.com/auth/calendar.events";

    public sealed record Tokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

    public static string AuthorizationUrl(string clientId, string redirectUri, string state) =>
        "https://accounts.google.com/o/oauth2/v2/auth" +
        $"?client_id={Uri.EscapeDataString(clientId)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        "&response_type=code" +
        $"&scope={Uri.EscapeDataString(Scope)}" +
        // offline + consent is what makes Google hand over a refresh token. Without both,
        // a second authorisation returns an access token only and the connection silently
        // stops working an hour later.
        "&access_type=offline&prompt=consent&include_granted_scopes=true" +
        $"&state={Uri.EscapeDataString(state)}";

    public static Task<Tokens> ExchangeAsync(
        IHttpClientFactory httpFactory, string clientId, string clientSecret,
        string code, string redirectUri, CancellationToken ct) =>
        PostAsync(httpFactory, new Dictionary<string, string>
        {
            ["code"] = code.Trim(),
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        }, ct);

    public static Task<Tokens> RefreshAsync(
        IHttpClientFactory httpFactory, string clientId, string clientSecret,
        string refreshToken, CancellationToken ct) =>
        PostAsync(httpFactory, new Dictionary<string, string>
        {
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "refresh_token",
        }, ct);

    private static async Task<Tokens> PostAsync(
        IHttpClientFactory httpFactory, Dictionary<string, string> form, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        using var response = await http.PostAsync(
            "https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(body);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Explain(document.RootElement));

        var root = document.RootElement;
        var access = root.TryGetProperty("access_token", out var token) ? token.GetString() ?? "" : "";
        // Absent on a refresh: Google issues the refresh token once, at first consent.
        var refresh = root.TryGetProperty("refresh_token", out var given) ? given.GetString() ?? "" : "";
        var seconds = root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600;

        if (access.Length == 0)
            throw new InvalidOperationException("Google returned no access token.");

        // A minute of slack, so a token never expires between the check and the call.
        return new Tokens(access, refresh, DateTimeOffset.UtcNow.AddSeconds(seconds - 60));
    }

    /// <summary>
    /// Google's OAuth errors are terse codes with a longer description beside them. Say
    /// both, and translate the two that everybody hits into what to actually do.
    /// </summary>
    private static string Explain(JsonElement root)
    {
        var code = root.TryGetProperty("error", out var error) ? error.GetString() ?? "" : "";
        var detail = root.TryGetProperty("error_description", out var described)
            ? described.GetString() ?? "" : "";

        return code switch
        {
            "invalid_grant" =>
                "Google rejected the code. They expire in a couple of minutes and work once — " +
                "start the Connect link again and paste the new one straight away. " +
                "(A refresh token gets this too once it has been revoked in your Google account.)",
            "redirect_uri_mismatch" =>
                "The redirect URI does not match one registered on the OAuth client. It has to be " +
                "character-for-character identical to a URI listed under \"Authorised redirect URIs\".",
            _ => $"Google said {code}{(detail.Length > 0 ? $": {detail}" : "")}".Trim(),
        };
    }
}

/// <summary>
/// Calendar API v3, only the five calls this plugin needs. Everything takes an access
/// token rather than fetching one, so token handling stays in one place — the provider,
/// which is the singleton that can cache it.
/// </summary>
public sealed class GoogleCalendarApi(IHttpClientFactory httpFactory)
{
    private const string Base = "https://www.googleapis.com/calendar/v3";

    public async Task<IReadOnlyList<CalEvent>> ListAsync(
        string accessToken, string calendarId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // singleEvents expands recurrence into occurrences, which is the only sane thing
        // for a display: without it a weekly event arrives once, as a rule to interpret.
        var url = $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events" +
                  $"?timeMin={Uri.EscapeDataString(from.ToString("o", CultureInfo.InvariantCulture))}" +
                  $"&timeMax={Uri.EscapeDataString(to.ToString("o", CultureInfo.InvariantCulture))}" +
                  "&singleEvents=true&orderBy=startTime&maxResults=2500";

        using var document = await SendAsync(accessToken, HttpMethod.Get, url, null, ct);

        var events = new List<CalEvent>();
        if (document is not null
            && document.RootElement.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                // Declined and deleted occurrences come back in the same list.
                if (item.TryGetProperty("status", out var status) && status.GetString() == "cancelled")
                    continue;
                if (Read(item) is { } parsed)
                    events.Add(parsed);
            }
        }

        return [.. events.OrderBy(e => e.Start)];
    }

    public async Task<CalEvent?> InsertAsync(
        string accessToken, string calendarId, CalEvent draft, CancellationToken ct)
    {
        using var document = await SendAsync(accessToken, HttpMethod.Post,
            $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events", Body(draft), ct);
        return document is null ? null : Read(document.RootElement);
    }

    public async Task<CalEvent?> UpdateAsync(
        string accessToken, string calendarId, CalEvent changed, CancellationToken ct)
    {
        using var document = await SendAsync(accessToken, HttpMethod.Patch,
            $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(changed.Id)}",
            Body(changed), ct);
        return document is null ? null : Read(document.RootElement);
    }

    public async Task DeleteAsync(string accessToken, string calendarId, string eventId, CancellationToken ct)
    {
        using var _ = await SendAsync(accessToken, HttpMethod.Delete,
            $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}",
            null, ct);
    }

    /// <summary>The calendar's own name, for the page heading.</summary>
    public async Task<string> NameAsync(string accessToken, string calendarId, CancellationToken ct)
    {
        using var document = await SendAsync(accessToken, HttpMethod.Get,
            $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}", null, ct);
        return document is not null && document.RootElement.TryGetProperty("summary", out var summary)
            ? summary.GetString() ?? calendarId
            : calendarId;
    }

    private static string Body(CalEvent value)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("summary", value.Summary);
            if (value.Location.Length > 0)
                json.WriteString("location", value.Location);
            if (value.Description.Length > 0)
                json.WriteString("description", value.Description);

            WriteWhen(json, "start", value.Start, value.AllDay);
            // Google wants an exclusive end date for an all-day event: a single day on the
            // 4th is start 2026-08-04, end 2026-08-05. The UI works in inclusive days, so
            // the conversion lives here rather than in everything that builds an event.
            WriteWhen(json, "end", value.AllDay ? value.End.AddDays(1) : value.End, value.AllDay);

            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteWhen(Utf8JsonWriter json, string name, DateTimeOffset when, bool allDay)
    {
        json.WriteStartObject(name);
        if (allDay)
        {
            json.WriteString("date", when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        else
        {
            json.WriteString("dateTime", when.ToString("o", CultureInfo.InvariantCulture));
            json.WriteString("timeZone", TimeZoneInfo.Local.Id);
        }
        json.WriteEndObject();
    }

    private static CalEvent? Read(JsonElement item)
    {
        var (start, startAllDay) = When(item, "start");
        var (end, _) = When(item, "end");
        if (start is not { } from)
            return null;

        var to = end ?? (startAllDay ? from.AddDays(1) : from.AddHours(1));

        return new CalEvent(
            item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            item.TryGetProperty("summary", out var summary) ? summary.GetString() ?? "(no title)" : "(no title)",
            from,
            to,
            startAllDay,
            item.TryGetProperty("location", out var location) ? location.GetString() ?? "" : "",
            item.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "");
    }

    /// <summary>An event carries either a dateTime or, for an all-day one, a bare date.</summary>
    private static (DateTimeOffset? When, bool AllDay) When(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var when) || when.ValueKind != JsonValueKind.Object)
            return (null, false);

        if (when.TryGetProperty("dateTime", out var moment)
            && DateTimeOffset.TryParse(moment.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var parsed))
            return (parsed.ToLocalTime(), false);

        if (when.TryGetProperty("date", out var day)
            && DateOnly.TryParse(day.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return (new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(date.ToDateTime(TimeOnly.MinValue))), true);

        return (null, false);
    }

    /// <summary>
    /// One request, with Google's error body turned into a sentence. Returns null for the
    /// 204 a delete answers with, which is the only call here that sends no JSON back.
    /// </summary>
    private async Task<JsonDocument?> SendAsync(
        string accessToken, HttpMethod method, string url, string? body, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Explain(response.StatusCode, text));

        return text.Trim().Length == 0 ? null : JsonDocument.Parse(text);
    }

    private static string Explain(System.Net.HttpStatusCode status, string body)
    {
        var message = "";
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var detail)
                    ? detail.GetString() ?? ""
                    : error.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // Not JSON — an HTML error page from a proxy, most likely. Fall through.
        }

        return (int)status switch
        {
            401 => "Google rejected the token. Reconnect the calendar — access may have been revoked.",
            403 when message.Contains("insufficient", StringComparison.OrdinalIgnoreCase) =>
                "That account can read this calendar but not change it. Ask the owner for " +
                "\"Make changes to events\", or authorise as an account that has it.",
            403 => $"Google refused: {message}",
            404 => "No calendar with that id. Check the calendar id in the connection — " +
                   "Google Calendar → Settings → the calendar → Integrate calendar → Calendar ID.",
            _ => message.Length > 0 ? $"Google said: {message}" : $"Google answered HTTP {(int)status}.",
        };
    }
}
