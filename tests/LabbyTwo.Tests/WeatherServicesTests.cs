using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Providers;

namespace LabbyTwo.Tests;

/// <summary>
/// The three weather services that answer in somebody else's shape: NWS warnings, air
/// quality, and turning a place name into coordinates.
/// </summary>
public class WeatherServicesTests
{
    // Trimmed from a real api.weather.gov response.
    private const string Warnings = """
        {
          "features": [
            {
              "properties": {
                "id": "urn:oid:2.49.0.1.840.0.minor",
                "event": "Frost Advisory",
                "headline": "Frost Advisory issued October 3",
                "severity": "Minor",
                "urgency": "Expected",
                "areaDesc": "Douglas County",
                "instruction": "Cover sensitive plants.",
                "onset": "2026-10-03T22:00:00-06:00",
                "ends": "2026-10-04T09:00:00-06:00"
              }
            },
            {
              "properties": {
                "id": "urn:oid:2.49.0.1.840.0.severe",
                "event": "Tornado Warning",
                "headline": "Tornado Warning issued October 3 at 8:15PM MDT",
                "severity": "Severe",
                "urgency": "Immediate",
                "areaDesc": "Arapahoe County",
                "instruction": "TAKE COVER NOW.",
                "onset": "2026-10-03T20:15:00-06:00",
                "ends": "2026-10-03T21:00:00-06:00"
              }
            }
          ]
        }
        """;

    private static IReadOnlyList<WeatherAlertsProvider.Warning> Read(int minimum) =>
        WeatherAlertsProvider.Read(JsonDocument.Parse(Warnings).RootElement, minimum);

    [Fact]
    public void TheWorstWarningComesFirst()
    {
        // A page listing a frost advisory above a tornado warning is worse than no page.
        var warnings = Read(0);

        Assert.Equal("Tornado Warning", warnings[0].Event);
        Assert.Equal("🌪️", warnings[0].Icon);
        Assert.Equal("is-severe", warnings[0].Css);
    }

    [Fact]
    public void AnythingBelowTheChosenSeverityIsDropped()
    {
        var warnings = Read(3);

        Assert.Single(warnings);
        Assert.Equal("Tornado Warning", warnings[0].Event);
    }

    [Fact]
    public void AWarningWithNoIdIsIgnored()
    {
        // The id is what stops a warning being announced on every five-minute run, so one
        // without it would be announced forever.
        var warnings = WeatherAlertsProvider.Read(JsonDocument.Parse("""
            { "features": [ { "properties": { "event": "Flood Warning", "severity": "Severe" } } ] }
            """).RootElement, 0);

        Assert.Empty(warnings);
    }

    [Fact]
    public void NoFeaturesMeansNothingInForce() =>
        Assert.Empty(WeatherAlertsProvider.Read(JsonDocument.Parse("{}").RootElement, 0));

    [Fact]
    public void SevereWeatherIgnoresQuietHours()
    {
        // 23:00 to 07:00, nothing gets through — except the thing this exists for.
        var policy = new AlertPolicy(new TimeOnly(23, 0), new TimeOnly(7, 0), AlertPolicy.Nothing);

        // Quiet hours are read in local time, so the moment has to be built as local — an
        // offset of zero would be 8pm somewhere and this test would pass by accident.
        var at = new DateTimeOffset(new DateTime(2026, 10, 3, 2, 0, 0, DateTimeKind.Local));

        Assert.False(policy.Allows(new Alert(AlertLevel.Down, "Disk full", ""), at));
        Assert.True(policy.Allows(new Alert(AlertLevel.Down, "Tornado Warning", "") { Urgent = true }, at));
    }

    [Fact]
    public void AirQualityKeepsWhicheverIndexWasAskedFor()
    {
        const string json = """
            {
              "current": {
                "us_aqi": 168,
                "european_aqi": 74,
                "pm2_5": 88.2,
                "pm10": 102.0,
                "ozone": 61,
                "uv_index": 4.2
              }
            }
            """;

        var american = AirQualityProvider.Read(JsonDocument.Parse(json).RootElement, european: false);
        var european = AirQualityProvider.Read(JsonDocument.Parse(json).RootElement, european: true);

        Assert.Equal(168, american["aqi"]);
        Assert.Equal(74, european["aqi"]);
        Assert.Equal(88.2, american["pm2_5"], 3);
    }

    [Theory]
    [InlineData(20, "Good")]
    [InlineData(75, "Moderate")]
    [InlineData(160, "Unhealthy")]
    [InlineData(420, "Hazardous")]
    public void TheIndexIsTranslatedIntoWords(double aqi, string expected) =>
        Assert.Equal(expected, AirQualityProvider.Band(aqi).Label);

    [Fact]
    public void GeocodingReadsThePlacesAndSkipsTheUnplaceable()
    {
        var places = Geocoder.Read(JsonDocument.Parse("""
            {
              "results": [
                { "name": "Denver", "latitude": 39.73915, "longitude": -104.9847,
                  "admin1": "Colorado", "country": "United States", "timezone": "America/Denver" },
                { "name": "Nowhere", "admin1": "Somewhere" }
              ]
            }
            """).RootElement);

        Assert.Single(places);
        Assert.Equal("Denver, Colorado, United States", places[0].Label);
        Assert.Equal(39.73915, places[0].Latitude, 5);
    }

    [Fact]
    public void NoResultsIsAnEmptyListRatherThanAFailure() =>
        Assert.Empty(Geocoder.Read(JsonDocument.Parse("""{ "generationtime_ms": 0.2 }""").RootElement));

    [Fact]
    public void AConnectionsOwnCoordinatesBeatTheHouses()
    {
        var home = new HomeLocation(39.7392, -104.9903, "Denver");

        Assert.Equal((51.5, -0.12), home.Resolve("51.5", "-0.12"));
        Assert.Equal((39.7392, -104.9903), home.Resolve("", ""));
        Assert.Null(HomeLocation.None.Resolve("", ""));
    }

    [Fact]
    public void HalfALocationIsNoLocation()
    {
        // A latitude with no longitude is not a point, and defaulting the other half to
        // zero would put the dashboard in the Atlantic without saying so.
        Assert.Null(HomeLocation.None.Resolve("39.7392", ""));
        Assert.Equal((39.7392, -0.12), new HomeLocation(1, -0.12, "").Resolve("39.7392", ""));
    }

    [Fact]
    public void CoordinatesSurviveAMachineThatWritesCommasForDecimalPoints()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            Assert.Equal("39.7392", HomeLocation.Format(39.7392));
            Assert.Equal(39.7392, HomeLocation.Number("39.7392"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
