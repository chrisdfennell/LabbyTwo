using System.Text.Json;
using LabbyTwo.Providers;

namespace LabbyTwo.Tests;

/// <summary>
/// Open-Meteo returns a forecast as one array per field rather than one object per day,
/// which is only safe to read by index while the arrays stay the same length and order.
/// These pin that reading — including the ragged case, where the alternative to a missing
/// number is losing the whole week.
/// </summary>
public class ForecastProviderTests
{
    // Trimmed from a real response for Denver.
    private const string Daily = """
        {
          "time": ["2026-08-11", "2026-08-12", "2026-08-13"],
          "temperature_2m_max": [35.6, 35.4, 31.3],
          "temperature_2m_min": [21.4, 18.9, 17.1],
          "apparent_temperature_max": [33.1, 33.0, 30.2],
          "apparent_temperature_min": [20.0, 17.5, 16.0],
          "precipitation_sum": [0.6, 1.5, 34.4],
          "snowfall_sum": [0, 0, 2.5],
          "precipitation_probability_max": [7, 26, 89],
          "wind_gusts_10m_max": [32.2, 40.0, 64.4],
          "wind_direction_10m_dominant": [215, 190, 270],
          "uv_index_max": [8.4, 9.1, 5.2],
          "weather_code": [53, 55, 82]
        }
        """;

    private static IReadOnlyList<ForecastProvider.Day> Parse(string json) =>
        ForecastProvider.ReadDays(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void EachDayIsReadFromTheSameIndexOfEveryArray()
    {
        var days = Parse(Daily);

        Assert.Equal(3, days.Count);
        Assert.Equal(new DateOnly(2026, 8, 13), days[2].Date);
        Assert.Equal(31.3, days[2].High, 3);
        Assert.Equal(17.1, days[2].Low, 3);
        Assert.Equal(30.2, days[2].FeelsHigh, 3);
        Assert.Equal(89, days[2].RainChance);
        Assert.Equal(270, days[2].WindDirection);
        Assert.Equal(5.2, days[2].UvIndex, 3);
        Assert.Equal(82, days[2].Code);
    }

    [Fact]
    public void EverythingIsStoredInTheUnitsTheRestOfTheAppUses()
    {
        var day = Parse(Daily)[2];

        // The API answers in millimetres, centimetres and km/h; storing those under metrics
        // declared in inches and mph would have shown 34mm of rain as 34 inches of it.
        Assert.Equal(34.4 / 25.4, day.RainInches, 4);
        Assert.Equal(2.5 / 2.54, day.SnowInches, 4);
        Assert.Equal(64.4 / 1.60934, day.GustMph, 3);
    }

    [Fact]
    public void AShortArrayCostsOneNumberRatherThanTheWholeForecast()
    {
        var days = Parse("""
            {
              "time": ["2026-08-11", "2026-08-12"],
              "temperature_2m_max": [35.6, 35.4],
              "temperature_2m_min": [21.4]
            }
            """);

        Assert.Equal(2, days.Count);
        Assert.Equal(35.4, days[1].High, 3);
        Assert.Equal(0, days[1].Low);
    }

    [Fact]
    public void NoDatesMeansNoDaysRatherThanAnException() =>
        Assert.Empty(Parse("""{ "temperature_2m_max": [35.6] }"""));

    [Theory]
    [InlineData(0, "☀️")]
    [InlineData(3, "☁️")]
    [InlineData(65, "🌧️")]
    [InlineData(73, "🌨️")]
    [InlineData(99, "⛈️")]
    [InlineData(4242, "☁️")]
    public void EveryWeatherCodeGetsAnIcon(int code, string expected) =>
        Assert.Equal(expected, WeatherCode.Icon(code));

    [Fact]
    public void TodayIsNamedAndTheRestAreWeekdays()
    {
        var today = new DateOnly(2026, 8, 11); // A Tuesday.

        Assert.Equal("Today", new ForecastProvider.Day(today, 0).Label(today));
        Assert.Equal("Thu", new ForecastProvider.Day(today.AddDays(2), 0).Label(today));
    }

    private const string Hourly = """
        {
          "time": ["2026-08-11T13:00", "2026-08-11T14:00", "2026-08-11T15:00", "2026-08-11T16:00"],
          "temperature_2m": [30.1, 32.4, 33.9, 33.2],
          "precipitation_probability": [3, 8, 22, 41],
          "precipitation": [0, 0, 0.2, 1.5],
          "weather_code": [1, 2, 51, 61]
        }
        """;

    private static IReadOnlyList<ForecastProvider.Hour> Hours(DateTime? now) =>
        ForecastProvider.ReadHours(JsonDocument.Parse(Hourly).RootElement, now);

    [Fact]
    public void TheHourlyStripStartsAtTheHourYouAreIn()
    {
        // Ten past three: three o'clock is the hour you are in, not one you have missed.
        var hours = Hours(new DateTime(2026, 8, 11, 15, 10, 0));

        Assert.Equal(2, hours.Count);
        Assert.Equal(15, hours[0].At.Hour);
        Assert.Equal(33.9, hours[0].TempC, 3);
        Assert.Equal(22, hours[0].RainChance);
        Assert.Equal(1.5 / 25.4, hours[1].RainInches, 4);
    }

    [Fact]
    public void WithNoClockGivenEveryHourIsKept() =>
        Assert.Equal(4, Hours(null).Count);

    [Theory]
    [InlineData(0, "12am")]
    [InlineData(9, "9am")]
    [InlineData(12, "12pm")]
    [InlineData(15, "3pm")]
    public void HoursReadTheWayPeopleSayThem(int hour, string expected) =>
        Assert.Equal(expected, new ForecastProvider.Hour(
            new DateTime(2026, 8, 11, hour, 0, 0), 0, 0, 0, 0).Label);
}
