using System.Globalization;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Turns "Denver" into a pair of coordinates, using Open-Meteo's geocoding API — free, no
/// key, and the same people already answering for the forecast.
///
/// This exists because typing latitude and longitude by hand is a bad ask: nobody knows
/// theirs, the sign convention catches everyone at least once, and a mistyped digit does
/// not fail — it silently shows you the weather ninety miles away.
/// </summary>
public sealed class Geocoder(IHttpClientFactory httpFactory)
{
    /// <param name="Detail">Region and country, because half the world has a Springfield.</param>
    public sealed record Place(string Name, string Detail, double Latitude, double Longitude, string Timezone)
    {
        public string Label => Detail.Length > 0 ? $"{Name}, {Detail}" : Name;
    }

    public async Task<IReadOnlyList<Place>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (query.Trim() is not { Length: > 1 } needle)
            return [];

        var url = "https://geocoding-api.open-meteo.com/v1/search" +
                  $"?name={Uri.EscapeDataString(needle)}&count=8&language=en&format=json";

        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return Read(document.RootElement);
    }

    /// <summary>
    /// Separate from the request so it can be tested without one. A result missing its
    /// coordinates is dropped rather than defaulted — the Atlantic off Africa is where
    /// every zeroed pair of coordinates lands, and it is a long way from anywhere.
    /// </summary>
    public static IReadOnlyList<Place> Read(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];

        var places = new List<Place>();
        foreach (var result in results.EnumerateArray())
        {
            if (Number(result, "latitude") is not { } latitude || Number(result, "longitude") is not { } longitude)
                continue;

            var detail = string.Join(", ", new[] { Text(result, "admin1"), Text(result, "country") }
                .Where(part => part.Length > 0));

            places.Add(new Place(Text(result, "name"), detail, latitude, longitude, Text(result, "timezone")));
        }

        return places;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    /// <summary>Invariant, for the same reason <see cref="HomeLocation.Format"/> is.</summary>
    public static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
