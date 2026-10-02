using System.Diagnostics;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Tunarr — live TV channels built from a Plex or Jellyfin library, streamed by running
/// ffmpeg for every channel somebody is watching. That last part is why it is here at all:
/// each watched channel is a transcode, and on a box where Plex and Tdarr share the same
/// Intel GPU, Tunarr is the third mouth at the table.
///
/// Its API is young and has moved between releases, so this asks for as little as it can
/// and treats everything past "is it there" as optional. <c>/api/version</c> is the probe —
/// it has answered since the first release and needs no key. Channels, the sessions being
/// streamed and which hardware acceleration ffmpeg is set to use are each tried in turn; a
/// build that lacks one simply reports one number fewer rather than going down.
/// </summary>
public sealed class TunarrProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "tunarr";
    public string DisplayName => "Tunarr";
    public string Icon => "📡";
    public string Category => "Media";
    public string Description => "Channels, how many are being streamed right now, and whether ffmpeg is set to use the GPU.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://tunarr:8000", Required: true,
            Help: "The web interface's address — port 8000 unless you changed it."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("channel_count", "Channels"),
        new("active_sessions", "Channels streaming"),
        new("viewers", "Viewers"),
        new("latency_ms", "Response time", " ms"),
    ];

    /// <summary>The probe detail holding ffmpeg's hardware acceleration mode, e.g. "qsv" or "none".</summary>
    public const string HardwareDetail = "hw_accel";

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No base URL configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);

            using var version = await ReadAsync(http, $"{baseUrl}/api/version", ct);
            var tunarrVersion = version.RootElement.ValueKind == JsonValueKind.Object
                                && version.RootElement.TryGetProperty("tunarr", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

            var metrics = new Dictionary<string, double>();
            var details = new Dictionary<string, string>();

            if (await TryReadAsync(http, $"{baseUrl}/api/channels", ct) is { } channels)
            {
                using (channels)
                {
                    if (channels.RootElement.ValueKind == JsonValueKind.Array)
                        metrics["channel_count"] = channels.RootElement.GetArrayLength();
                }
            }

            if (await TryReadAsync(http, $"{baseUrl}/api/sessions", ct) is { } sessions)
            {
                using (sessions)
                {
                    var read = ReadSessions(sessions.RootElement);
                    metrics["active_sessions"] = read.Active;
                    metrics["viewers"] = read.Viewers;
                }
            }

            var mode = await HardwareAccelerationAsync(http, connection, baseUrl, ct);
            if (mode is not null)
                details[HardwareDetail] = mode;

            stopwatch.Stop();
            metrics["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds;

            var streaming = metrics.GetValueOrDefault("active_sessions");
            var message = metrics.ContainsKey("active_sessions")
                ? streaming switch
                {
                    0 => "Nothing streaming",
                    1 => "1 channel streaming",
                    _ => $"{streaming:0} channels streaming",
                }
                : tunarrVersion is { Length: > 0 } ? $"v{tunarrVersion}" : "Connected";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    /// <summary>
    /// The acceleration setting is somebody's one-time choice, not a reading, so it is asked
    /// for once every <see cref="SettingsAge"/> per connection rather than every sweep — two
    /// fewer requests each time on the builds that need both tried.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Mode, DateTimeOffset At)> _modes = new();

    private static readonly TimeSpan SettingsAge = TimeSpan.FromMinutes(15);

    private async Task<string?> HardwareAccelerationAsync(HttpClient http, Connection connection, string baseUrl, CancellationToken ct)
    {
        var key = $"{connection.Id}|{baseUrl}";
        if (_modes.TryGetValue(key, out var known) && DateTimeOffset.UtcNow - known.At < SettingsAge)
            return known.Mode;

        // Older builds keep one ffmpeg setting; newer ones a list of transcode configs.
        string? mode = null;
        if (await TryReadAsync(http, $"{baseUrl}/api/ffmpeg-settings", ct) is { } ffmpeg)
        {
            using (ffmpeg)
                mode = ReadHardwareAcceleration(ffmpeg.RootElement);
        }
        if (mode is null && await TryReadAsync(http, $"{baseUrl}/api/transcode_configs", ct) is { } configs)
        {
            using (configs)
                mode = ReadHardwareAcceleration(configs.RootElement);
        }

        _modes[key] = (mode, DateTimeOffset.UtcNow);
        return mode;
    }

    /// <summary>Channels being streamed, and how many people are watching them.</summary>
    public sealed record Sessions(int Active, int Viewers);

    /// <summary>
    /// The answer to <c>/api/sessions</c>, in any of the shapes it has had: an object keyed
    /// by channel id whose values are a session or a list of them, or a bare list. A session
    /// counts when somebody is connected to it (<c>numConnections</c>, or the size of its
    /// <c>connections</c>), or — when it says neither — when its <c>state</c> is not one of
    /// the stopped ones. Tunarr keeps a channel's session warm for a little while after the
    /// last viewer leaves, which is exactly what the connection count sees through.
    /// </summary>
    public static Sessions ReadSessions(JsonElement root)
    {
        int active = 0, viewers = 0;

        void One(JsonElement session)
        {
            if (session.ValueKind != JsonValueKind.Object)
                return;

            int? connected = null;
            if (session.TryGetProperty("numConnections", out var n) && n.ValueKind == JsonValueKind.Number)
                connected = n.GetInt32();
            else if (session.TryGetProperty("connections", out var c))
                connected = c.ValueKind switch
                {
                    JsonValueKind.Array => c.GetArrayLength(),
                    JsonValueKind.Object => c.EnumerateObject().Count(),
                    _ => null,
                };

            if (connected is { } count)
            {
                if (count > 0)
                {
                    active++;
                    viewers += count;
                }
                return;
            }

            var state = session.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()?.ToLowerInvariant() ?? ""
                : "";
            if (state is not ("stopped" or "error" or "errored" or "idle" or "cleanup" or "ended"))
            {
                active++;
                viewers++;
            }
        }

        void Many(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray())
                    One(item);
            else
                One(value);
        }

        if (root.ValueKind == JsonValueKind.Array)
            Many(root);
        else if (root.ValueKind == JsonValueKind.Object)
            foreach (var channel in root.EnumerateObject())
                Many(channel.Value);

        return new Sessions(active, viewers);
    }

    /// <summary>
    /// The <c>hardwareAccelerationMode</c> ffmpeg is set to — "qsv", "vaapi", "cuda",
    /// "videotoolbox" or "none" — from the old single settings object or the newer list of
    /// transcode configs, where several distinct modes are joined with commas. Null when
    /// the answer does not say.
    /// </summary>
    public static string? ReadHardwareAcceleration(JsonElement root)
    {
        static string? Mode(JsonElement element) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("hardwareAccelerationMode", out var mode)
            && mode.ValueKind == JsonValueKind.String
            && mode.GetString()?.Trim().ToLowerInvariant() is { Length: > 0 } text
                ? text
                : null;

        if (root.ValueKind == JsonValueKind.Array)
        {
            var modes = root.EnumerateArray().Select(Mode).OfType<string>().Distinct().ToList();
            return modes.Count == 0 ? null : string.Join(",", modes);
        }
        return Mode(root);
    }

    private static async Task<JsonDocument> ReadAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                "That answered, but not with Tunarr's API. Check the URL points at Tunarr's web interface.");
        }
    }

    private static async Task<JsonDocument?> TryReadAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            return await ReadAsync(http, url, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // An endpoint this build does not have costs that one number, not the probe.
            return null;
        }
    }
}
