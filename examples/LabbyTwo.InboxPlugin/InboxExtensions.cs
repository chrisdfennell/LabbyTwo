using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.InboxPlugin;

/// <summary>
/// Somewhere for things to report *to*.
///
/// Every integration in LabbyTwo works by asking: it polls a URL, reads a socket, pings an
/// address. That is the right shape for anything with a state you can go and look at, and
/// the wrong shape for anything that happens — a backup that finished, a build that failed,
/// a doorbell. Those have nobody to ask; the event exists for a second and then does not.
///
/// So this is the inverse of <c>WebhookProvider</c>: instead of LabbyTwo posting somewhere
/// when something changes, anything with a shell posts here.
///
/// <code>
/// curl -fsS -X POST "http://192.168.86.57:5150/ext/inbox?k=TOKEN&amp;source=backup" \
///      -d "Nightly backup finished — 412 GB, 41 minutes"
/// </code>
/// </summary>
public sealed class InboxProvider(Db db) : IConnectionProvider
{
    public const string ProviderType = "inbox";

    public string Type => ProviderType;
    public string DisplayName => "Inbox (incoming webhook)";
    public string Icon => "📬";
    public string Category => "Monitoring";

    public string Description =>
        "A URL your scripts, CI and appliances can POST to. Events are kept, shown on a timeline tab, "
        + "and can raise a real alert — including “nothing has reported since yesterday”, which is the "
        + "failure a poller can never see.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("token", "Token", FieldKind.Password, Required: true,
            Help: "Goes on the end of the URL as ?k=… — anything holding it can post events. "
                  + "Generate one with: openssl rand -hex 24"),

        new("expect_every_hours", "Expect something every (hours)", FieldKind.Number, Default: "0",
            Help: "For a job that reports on a schedule. Set it to a little more than the real interval — "
                  + "26 for something nightly — and the suggested alert fires when the job stops running, "
                  + "which is the fault nobody notices because nothing happens."),

        new("alert_on", "Raise an alert for", FieldKind.Select, Default: "levels",
            Options:
            [
                new SelectOption("levels", "Events marked up or down"),
                new SelectOption("all", "Every event"),
                new SelectOption("none", "Nothing — just record them"),
            ],
            Help: "An alert here goes out through your normal channels, quiet hours and all."),

        new("keep_days", "Keep events for (days)", FieldKind.Number, Default: "30",
            Help: "0 keeps them forever. The cleanup job runs every six hours.")
        {
            Advanced = true,
        },
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("events_24h", "Events today"),
        new("hours_since_last", "Since last event", " h", 1),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Nothing has reported", "hours_since_last", Comparison.Above, 26, ForMinutes: 0,
            Why: "A job that reports every night has not reported. Nothing failed loudly — it simply stopped, "
                 + "which is why a poller would never have caught it. Set the threshold above your real interval."),
    ];

    /// <summary>
    /// A receiver has no far end to fail, so this never returns Down — there is nothing it
    /// could mean. What it reports instead is how long the silence has been, which is the
    /// number worth alerting on.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        try
        {
            var summary = await new InboxStore(db).SummaryAsync(connection.Id, TimeSpan.FromHours(24), ct);

            var metrics = new Dictionary<string, double> { ["events_24h"] = summary.InWindow };

            // Left out entirely until something has arrived, rather than reported as zero.
            // Zero would read as "one just came in" and quietly hold a dead-man's-switch
            // rule open for ever.
            if (summary.Last is { } last)
                metrics["hours_since_last"] = (DateTimeOffset.Now - last).TotalHours;

            return ProbeResult.Up(TimeSpan.Zero,
                summary.Last is { } when
                    ? $"{summary.InWindow} in the last day, most recent {Ago.Since(when)}"
                    : "Nothing received yet.",
                metrics);
        }
        catch (Exception ex)
        {
            return ProbeResult.Down(TimeSpan.Zero, ex.GetBaseException().Message);
        }
    }
}

/// <summary>
/// The route things post to. Anonymous, because the whole point is that a cron job on
/// another machine can reach it, and a cron job cannot log in — so the token in the URL is
/// the authorisation, exactly as it is for a share link.
/// </summary>
public sealed class InboxEndpoints(
    ConfigStore config,
    AlertService alerts,
    Db db,
    ILogger<InboxEndpoints> log) : IEndpointExtension
{
    public const string RouteKey = "inbox";

    public string Key => RouteKey;

    public bool RequiresAuthorization => false;

    public void Map(IEndpointRouteBuilder routes)
    {
        // POST is the right verb and what everything sensible will use. GET is here because
        // half the things that want to report have no way to make a POST — a router's
        // "call this URL" box, a camera, a NAS notification rule — and refusing them on
        // principle would mean they simply go on reporting nowhere.
        routes.MapPost("", ReceiveAsync).DisableAntiforgery();
        routes.MapGet("", ReceiveAsync);
    }

    private async Task<IResult> ReceiveAsync(HttpContext context, CancellationToken ct)
    {
        var presented = context.Request.Query["k"].ToString();
        var inbox = (await config.ConnectionsAsync(ct))
            .Where(c => c.Provider == InboxProvider.ProviderType && c.Enabled)
            .FirstOrDefault(c => Matches(c.Settings.Get("token"), presented));

        if (inbox is null)
            return Results.Text("Bad or missing token.", "text/plain", statusCode: 401);

        var (title, body, level, source) = await ReadAsync(context, ct);

        if (title.Length == 0 && body.Length == 0)
            return Results.Text(
                "Nothing to record. Send a body, or ?title=… — an empty event is a row that tells you nothing.",
                "text/plain", statusCode: 400);

        // A title on its own reads better in a list than a body on its own, so a message
        // that only has one becomes the title.
        if (title.Length == 0)
        {
            title = body.Length <= 120 ? body : body[..117] + "…";
            body = title == body ? "" : body;
        }

        await new InboxStore(db).AddAsync(inbox.Id, source, level, title, body, ct);

        var shouldAlert = inbox.Settings.Get("alert_on", "levels") switch
        {
            "all" => true,
            "none" => false,
            _ => level is "down" or "up",
        };

        if (shouldAlert)
        {
            var alert = new Alert(
                level switch { "down" => AlertLevel.Down, "up" => AlertLevel.Up, _ => AlertLevel.Info },
                source.Length > 0 ? $"{source}: {title}" : title,
                body);

            // Broadcast, not thrown on failure. The event is already recorded, and something
            // that posted a webhook should not be told its report failed because a Discord
            // channel was misconfigured.
            try
            {
                await alerts.BroadcastAsync(alert, ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Recorded an inbox event but could not send the alert for it");
            }
        }

        // Plain text and a short body: the reader is a shell script with `curl -f`, and the
        // status code is the whole of what it needs.
        return Results.Text("Recorded.\n", "text/plain");
    }

    /// <summary>
    /// Whatever the caller found easiest to send. JSON if it is JSON, form fields if it is a
    /// form, otherwise the raw body as the message — with the query string overriding any of
    /// it, because that is the only channel some appliances have.
    /// </summary>
    private static async Task<(string Title, string Body, string Level, string Source)> ReadAsync(
        HttpContext context, CancellationToken ct)
    {
        var query = context.Request.Query;
        string title = query["title"].ToString();
        string body = query["body"].ToString();
        string level = query["level"].ToString();
        string source = query["source"].ToString();

        if (context.Request.Method != HttpMethods.Get)
        {
            var contentType = context.Request.ContentType ?? "";

            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                var raw = await reader.ReadToEndAsync(ct);
                try
                {
                    using var document = JsonDocument.Parse(raw);
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        var root = document.RootElement;
                        title = Pick(title, Text(root, "title"), Text(root, "subject"));
                        body = Pick(body, Text(root, "body"), Text(root, "message"), Text(root, "text"));
                        level = Pick(level, Text(root, "level"), Text(root, "status"));
                        source = Pick(source, Text(root, "source"), Text(root, "service"));
                    }
                }
                catch (JsonException)
                {
                    // Announced as JSON and is not. Keeping it as the message body beats
                    // rejecting it: the useful half of a malformed report is still the text.
                    body = Pick(body, raw);
                }
            }
            else if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(ct);
                title = Pick(title, form["title"].ToString());
                body = Pick(body, form["body"].ToString(), form["message"].ToString());
                level = Pick(level, form["level"].ToString());
                source = Pick(source, form["source"].ToString());
            }
            else
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                body = Pick(body, await reader.ReadToEndAsync(ct));
            }
        }

        return (Trim(title, 200), Trim(body, 4000), Normalise(level), Trim(source, 60));
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>First non-empty wins, so the query string beats the body it was sent with.</summary>
    private static string Pick(params string[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim() ?? "";

    /// <summary>
    /// The words other systems actually use, mapped onto the three levels an
    /// <see cref="Alert"/> has. Anything unrecognised is information rather than a failure —
    /// guessing "down" from a word nobody defined is how a dashboard cries wolf.
    /// </summary>
    private static string Normalise(string level) => level.Trim().ToLowerInvariant() switch
    {
        "down" or "fail" or "failed" or "failure" or "error" or "critical" or "bad" => "down",
        "up" or "ok" or "success" or "succeeded" or "resolved" or "recovered" or "good" => "up",
        _ => "info",
    };

    private static string Trim(string value, int limit)
    {
        value = value.Trim();
        return value.Length <= limit ? value : value[..limit];
    }

    private static bool Matches(string expected, string presented) =>
        expected.Length > 0
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(presented));
}

/// <summary>A page of what has come in, newest first.</summary>
public sealed class InboxTabKind : ITabKind
{
    public const string KindKey = "inbox";

    public string Kind => KindKey;
    public string DisplayName => "Inbox";
    public string Icon => "📬";
    public string Description => "A timeline of events posted to LabbyTwo by your scripts and appliances.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("connection", "Inbox", FieldKind.Connection,
            Help: "Which receiver to show. Leave it unset to show everything that has come in.")
        {
            ProviderFilter = InboxProvider.ProviderType,
        },

        new("limit", "How many to show", FieldKind.Number, Default: "50"),
    ];

    public Type Component => typeof(InboxTab);
}

/// <summary>
/// Retention. Nothing else would ever delete these — a receiver accumulates for ever by
/// design, and a table nobody prunes is a database that grows until somebody notices.
/// </summary>
public sealed class InboxPurgeJob(ConfigStore config, Db db, ILogger<InboxPurgeJob> log) : IBackgroundJob
{
    public string Name => "inbox-cleanup";

    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task RunAsync(CancellationToken ct)
    {
        var connections = (await config.ConnectionsAsync(ct))
            .Where(c => c.Provider == InboxProvider.ProviderType)
            .ToList();

        var store = new InboxStore(db);

        var removed = await store.PurgeAsync(
            connections.ToDictionary(c => c.Id, c => c.Settings.GetInt("keep_days", 30)), ct);

        // Deleting a receiver leaves its events behind, and they are unreachable — no tab
        // can select them and no provider reports on them. They are only taking up room.
        removed += await store.PurgeOrphansAsync([.. connections.Select(c => c.Id)], ct);

        if (removed > 0)
            log.LogInformation("Inbox cleanup removed {Count} event(s)", removed);
    }
}
