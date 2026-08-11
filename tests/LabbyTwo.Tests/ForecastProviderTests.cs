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
          "precipitation_sum": [0.6, 1.5, 34.4],
          "precipitation_probability_max": [7, 26, 89],
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
        Assert.Equal(89, days[2].RainChance);
        Assert.Equal(82, days[2].Code);
    }

    [Fact]
    public void RainIsStoredInInchesLikeEveryOtherRainMetric()
    {
        // The API answers in millimetres; storing those under a metric declared in inches
        // would have shown 34mm of rain as 34 inches of it.
        Assert.Equal(34.4 / 25.4, Parse(Daily)[2].RainInches, 4);
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
        Assert.Equal(expected, new ForecastProvider.Day(default, 0, 0, 0, 0, code).Icon);

    [Fact]
    public void TodayIsNamedAndTheRestAreWeekdays()
    {
        var today = new DateOnly(2026, 8, 11); // A Tuesday.

        Assert.Equal("Today", new ForecastProvider.Day(today, 0, 0, 0, 0, 0).Label(today));
        Assert.Equal("Thu", new ForecastProvider.Day(today.AddDays(2), 0, 0, 0, 0, 0).Label(today));
    }
}
