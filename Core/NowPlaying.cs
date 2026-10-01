using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabbyTwo.Core;

/// <summary>One stream playing right now on a media server, as <c>{{who: watching}}</c> shows it.</summary>
/// <param name="User">Who is watching — the account name the server shows, nothing more.</param>
/// <param name="Title">The film, or the series for an episode.</param>
/// <param name="Subtitle">The episode's title for a series, the year for a film; empty when neither is known.</param>
/// <param name="Device">The player's name: "Living room TV", "Chrome".</param>
/// <param name="Percent">How far through, 0–100.</param>
/// <param name="Transcoding">True when the server is converting it as it plays, false for direct play or stream, null when it did not say.</param>
public sealed record NowPlayingStream(
    string User,
    string Title,
    string Subtitle,
    string Device,
    double Percent,
    bool? Transcoding);

/// <summary>
/// What a media server is playing, carried from its probe to a page in the probe's details.
///
/// The Plex and Tautulli probes already ask for the sessions every sweep, to count them;
/// keeping the list they read, rather than only its length, is what lets a note say who is
/// watching what without asking the server again — once per sweep, however many pages are
/// open. Details are strings, so the list travels as a small JSON array under one key, and
/// is read back by <see cref="Decode"/>, which never throws: a detail written by an older or
/// stranger provider is simply nothing playing.
/// </summary>
public static class NowPlaying
{
    /// <summary>The probe detail holding the list.</summary>
    public const string DetailKey = "now_playing";

    /// <summary>
    /// The most streams kept. A household's server has a handful; this is only a ceiling on
    /// what a misbehaving one could make every sweep store and every page draw.
    /// </summary>
    public const int MaxStreams = 25;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The list as the detail's value.</summary>
    public static string Encode(IEnumerable<NowPlayingStream> streams) =>
        JsonSerializer.Serialize(streams.Take(MaxStreams).ToList(), Json);

    /// <summary>The list read back from <paramref name="details"/>; empty when there is none or it cannot be read.</summary>
    public static IReadOnlyList<NowPlayingStream> From(IReadOnlyDictionary<string, string>? details) =>
        details is not null && details.TryGetValue(DetailKey, out var value) ? Decode(value) : [];

    /// <summary>The list read back from the detail's value; empty when it cannot be read.</summary>
    public static IReadOnlyList<NowPlayingStream> Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        try
        {
            var streams = JsonSerializer.Deserialize<List<NowPlayingStream?>>(value, Json) ?? [];
            return
            [
                .. streams.OfType<NowPlayingStream>().Take(MaxStreams).Select(s => s with
                {
                    User = s.User ?? "",
                    Title = s.Title ?? "",
                    Subtitle = s.Subtitle ?? "",
                    Device = s.Device ?? "",
                    Percent = double.IsFinite(s.Percent) ? Math.Clamp(s.Percent, 0, 100) : 0,
                }),
            ];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
