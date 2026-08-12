using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Providers;

/// <summary>
/// Active watches and warnings from the US National Weather Service. Free, no key, and the
/// authoritative source rather than somebody's repackaging of it.
///
/// This is the one weather thing a dashboard genuinely owes you: a forecast is something
/// you go and look at, and a tornado warning is something that has to come and find you.
/// That is why <see cref="WeatherAlertJob"/> pushes these through the alert channels
/// instead of waiting for somebody to open a browser tab.
/// </summary>
public sealed class WeatherAlertsProvider(IHttpClientFactory httpFactory, AppSettingsStore appSettings) : IConnectionProvider
{
    public string Type => "nws";
    public string DisplayName => "Weather warnings (US)";
    public string Icon => "⚠️";
    public string Category => "Sensors";
    public string Description =>
        "Tornado, flood, winter-storm and heat warnings from the National Weather Service, pushed to your alert channels. United States only.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("latitude", "Latitude", FieldKind.Text,
            Help: "Leave both blank to use the location set in Settings."),

        new("longitude", "Longitude", FieldKind.Text),

        // Everything, by default. The NWS classifies inconsistently — a Denver air quality
        // alert comes through with severity "Unknown" — so a tidier-looking default would
        // quietly hide real warnings, which is the one thing this must never do.
        new("min_severity", "Tell me about", FieldKind.Select, Default: "all", Options:
        [
            new SelectOption("all", "Everything in force (recommended)"),
            new SelectOption("Moderate", "Moderate and above — skips advisories"),
            new SelectOption("Severe", "Severe and above — tornado, flash flood, hurricane"),
            new SelectOption("Extreme", "Extreme only — the rare ones"),
        ]),

        new("contact", "Contact for the NWS", FieldKind.Text,
            Help: "The NWS asks API users for an email or a URL so they can get in touch about traffic. " +
                  "Optional, and sent only to them.") { Advanced = true },
    ];

    /// <param name="Id">The NWS identifier, which is what stops one warning being announced twice.</param>
    /// <param name="Severity">Extreme, Severe, Moderate, Minor or Unknown, in their words.</param>
    public sealed record Warning(
        string Id,
        string Event,
        string Headline,
        string Severity,
        string Urgency,
        string Area,
        string Instruction,
        DateTimeOffset? Onset,
        DateTimeOffset? Ends)
    {
        public int Rank => Rankings.TryGetValue(Severity, out var rank) ? rank : 0;

        /// <summary>Red for the ones you act on now, amber for the rest.</summary>
        public string Css => Rank >= 3 ? "is-severe" : Rank == 2 ? "is-moderate" : "is-minor";

        public string Icon => Event switch
        {
            var e when e.Contains("Tornado", StringComparison.OrdinalIgnoreCase) => "🌪️",
            var e when e.Contains("Flood", StringComparison.OrdinalIgnoreCase) => "🌊",
            var e when e.Contains("Fire", StringComparison.OrdinalIgnoreCase) => "🔥",
            var e when e.Contains("Snow", StringComparison.OrdinalIgnoreCase)
                       || e.Contains("Winter", StringComparison.OrdinalIgnoreCase)
                       || e.Contains("Ice", StringComparison.OrdinalIgnoreCase) => "❄️",
            var e when e.Contains("Heat", StringComparison.OrdinalIgnoreCase) => "🥵",
            var e when e.Contains("Wind", StringComparison.OrdinalIgnoreCase) => "💨",
            var e when e.Contains("Thunder", StringComparison.OrdinalIgnoreCase) => "⛈️",
            _ => "⚠️",
        };

        /// <summary>"until 9:15 PM" — the part of a warning people actually read.</summary>
        public string Window => Ends is { } ends
            ? $"until {ends.ToLocalTime():ddd HH:mm}"
            : Onset is { } onset ? $"from {onset.ToLocalTime():ddd HH:mm}" : "";
    }

    public static readonly IReadOnlyDictionary<string, int> Rankings =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Extreme"] = 4,
            ["Severe"] = 3,
            ["Moderate"] = 2,
            ["Minor"] = 1,
            ["Unknown"] = 0,
        };

    private readonly ConcurrentDictionary<string, IReadOnlyList<Warning>> _active = new();

    /// <summary>What is in force, from the last fetch.</summary>
    public IReadOnlyList<Warning> Active(Connection connection) =>
        _active.TryGetValue(connection.Id, out var warnings) ? warnings : [];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("alerts_active", "Active warnings"),
        new("alerts_severe", "Severe or worse"),

        // 0 none, 1 minor, 2 moderate, 3 severe, 4 extreme — so a rule can watch how bad
        // it is rather than only how many there are.
        new("highest_severity", "Highest severity"),

        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Severe weather warning", "alerts_severe", Comparison.Above, 0,
            Why: "A second line of defence: the warning itself is already pushed to your channels, " +
                 "and this makes it visible on the alerts page too."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var warnings = await FetchAsync(connection, ct);
            stopwatch.Stop();

            var severe = warnings.Count(w => w.Rank >= 3);
            var metrics = new Dictionary<string, double>
            {
                ["alerts_active"] = warnings.Count,
                ["alerts_severe"] = severe,
                ["highest_severity"] = warnings.Count == 0 ? 0 : warnings.Max(w => w.Rank),
                ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
            };

            // "Up" here means the NWS answered, not that the weather is fine — a station
            // that went red every time it rained would be unusable.
            var message = warnings.Count == 0
                ? "Nothing in force"
                : $"{warnings[0].Event}" + (warnings.Count > 1 ? $" and {warnings.Count - 1} more" : "");

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, "api.weather.gov"));
        }
    }

    /// <summary>
    /// Fetches and caches. Public because <see cref="WeatherAlertJob"/> runs on its own
    /// interval — a warning should not have to wait for the next dashboard sweep.
    /// </summary>
    public async Task<IReadOnlyList<Warning>> FetchAsync(Connection connection, CancellationToken ct)
    {
        var home = HomeLocation.From(await appSettings.AllAsync(ct));
        if (home.Resolve(connection.Settings.Get("latitude"), connection.Settings.Get("longitude"))
            is not var (latitude, longitude))
        {
            throw new InvalidOperationException(
                "No coordinates: set a location in Settings, or give this connection its own.");
        }

        var url = "https://api.weather.gov/alerts/active" +
                  $"?point={HomeLocation.Format(latitude)},{HomeLocation.Format(longitude)}";

        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // The NWS rejects requests with no User-Agent and asks for a way to contact whoever
        // is making them. Saying what this is, honestly, is the price of the free API.
        var contact = connection.Settings.Get("contact").Trim();
        request.Headers.UserAgent.ParseAdd(
            contact is { Length: > 0 } ? $"LabbyTwo ({contact})" : "LabbyTwo (self-hosted dashboard)");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/geo+json"));

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        // Anything unrecognised — including "all" — means no floor at all.
        var minimum = Rankings.GetValueOrDefault(connection.Settings.Get("min_severity"), 0);
        var warnings = Read(document.RootElement, minimum);

        _active[connection.Id] = warnings;
        return warnings;
    }

    /// <summary>
    /// Reads the GeoJSON feed. Sorted worst-first, because a page showing a frost advisory
    /// above a tornado warning is worse than no page.
    /// </summary>
    public static IReadOnlyList<Warning> Read(JsonElement root, int minimumRank)
    {
        if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            return [];

        var warnings = new List<Warning>();
        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("properties", out var p))
                continue;

            var warning = new Warning(
                Text(p, "id"),
                Text(p, "event"),
                Text(p, "headline"),
                Text(p, "severity"),
                Text(p, "urgency"),
                Text(p, "areaDesc"),
                Text(p, "instruction"),
                Time(p, "onset") ?? Time(p, "effective"),
                Time(p, "ends") ?? Time(p, "expires"));

            if (warning.Id.Length == 0 || warning.Rank < minimumRank)
                continue;

            warnings.Add(warning);
        }

        return [.. warnings.OrderByDescending(w => w.Rank).ThenBy(w => w.Ends ?? DateTimeOffset.MaxValue)];
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
