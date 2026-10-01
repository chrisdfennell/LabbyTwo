using System.Diagnostics;
using System.Xml.Linq;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>Plex Media Server — reachability plus what is playing right now.</summary>
public sealed class PlexProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "plex";
    public string DisplayName => "Plex Media Server";
    public string Icon => "🎬";
    public string Category => "Media";
    public string Description => "Now-playing sessions and server version. The token is the X-Plex-Token from any Plex web URL.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.50:32400", Required: true),
        new("token", "X-Plex-Token", FieldKind.Password, Required: true,
            Help: "In the Plex web app, open any item's XML from the ⋯ menu — the token is in that URL."),
    ];

    public sealed record Session(string Title, string Subtitle, string User, string Player, double PercentDone)
    {
        /// <summary>
        /// Whether the server is converting the video or audio as it plays — Plex says so in
        /// the session's TranscodeSession, which a direct play does not have at all.
        /// </summary>
        public bool Transcoding { get; init; }

        /// <summary>The series, for an episode; null for a film or a track.</summary>
        public string? Series { get; init; }

        /// <summary>
        /// As <c>{{who: watching}}</c> and Tautulli say it: the series first and the episode
        /// second, where this record has always put the episode first for the Plex card.
        /// </summary>
        public NowPlayingStream ToStream() =>
            new(User, Series ?? Title, Series is null ? Subtitle : Title, Player, PercentDone, Transcoding);
    }

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("stream_count", "Active streams"),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("More streams than expected", "stream_count", Comparison.Above, 5, ClearThreshold: 3, ForMinutes: 15,
            Why: "Worth a look either way: the server is working hard, or somebody is watching " +
                 "who you did not know had an account."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var root = await GetAsync(connection, "/", ct);
            var version = root.Attribute("version")?.Value;
            var sessions = await SessionsAsync(connection, ct);
            stopwatch.Stop();

            var metrics = new Dictionary<string, double>
            {
                ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
                ["stream_count"] = sessions.Count,
            };
            var message = sessions.Count switch
            {
                0 => version is null ? "Connected" : $"v{version} — nothing playing",
                1 => "1 stream",
                _ => $"{sessions.Count} streams",
            };
            // The sessions were read to be counted; keeping who and what is free, and is what
            // {{who: watching}} draws, so a note never has to ask Plex itself.
            var details = new Dictionary<string, string>
            {
                [NowPlaying.DetailKey] = NowPlaying.Encode(sessions.Select(s => s.ToStream())),
            };
            return ProbeResult.Up(stopwatch.Elapsed, message, metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    public async Task<IReadOnlyList<Session>> SessionsAsync(Connection connection, CancellationToken ct)
    {
        var root = await GetAsync(connection, "/status/sessions", ct);
        return ReadSessions(root);
    }

    /// <summary>The sessions in a <c>/status/sessions</c> answer. Separate so a test can hand it Plex's XML.</summary>
    public static IReadOnlyList<Session> ReadSessions(XElement root)
    {
        var sessions = new List<Session>();
        foreach (var video in root.Elements().Where(e => e.Name.LocalName is "Video" or "Track"))
        {
            var duration = double.TryParse(video.Attribute("duration")?.Value, out var d) ? d : 0;
            var offset = double.TryParse(video.Attribute("viewOffset")?.Value, out var o) ? o : 0;

            // A show reports the episode as title and the series as grandparentTitle; a
            // movie has neither, so fall back to the year for the second line.
            var series = video.Attribute("grandparentTitle")?.Value;
            sessions.Add(new Session(
                video.Attribute("title")?.Value ?? "Unknown",
                series ?? video.Attribute("year")?.Value ?? "",
                video.Element("User")?.Attribute("title")?.Value ?? "",
                video.Element("Player")?.Attribute("title")?.Value ?? "",
                duration > 0 ? offset / duration * 100 : 0)
            {
                Series = series,
                Transcoding = video.Element("TranscodeSession") is { } transcode
                              && (string.Equals(transcode.Attribute("videoDecision")?.Value, "transcode", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(transcode.Attribute("audioDecision")?.Value, "transcode", StringComparison.OrdinalIgnoreCase)),
            });
        }
        return sessions;
    }

    private async Task<XElement> GetAsync(Connection connection, string path, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            throw new InvalidOperationException("No base URL configured.");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}{path}");
        request.Headers.TryAddWithoutValidation("X-Plex-Token", connection.Settings.Get("token"));
        request.Headers.TryAddWithoutValidation("Accept", "application/xml");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Plex rejected the token.");
        response.EnsureSuccessStatusCode();
        return XDocument.Parse(await response.Content.ReadAsStringAsync(ct)).Root
            ?? throw new InvalidOperationException("Plex returned an empty response.");
    }
}
