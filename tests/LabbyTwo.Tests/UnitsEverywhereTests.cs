using LabbyTwo.Components.Widgets;
using LabbyTwo.Core;
using LabbyTwo.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// The Units setting reaching every place a reading is written, not only the weather cards:
/// the one conversion helper everything goes through, the Markdown unit= and runbook
/// condition rules built on it, the alert lines and the weekly summary. What is drawn by the
/// components themselves is in UnitsRenderTests.
/// </summary>
public sealed class UnitsEverywhereTests : IDisposable
{
    private static readonly Units.Preferences Imperial = Units.Preferences.Of(Units.Imperial);
    private static readonly Units.Preferences Metric = Units.Preferences.Of(Units.Metric);

    private static readonly MetricSpec Temp = new("temp_c", "Temperature", "°C", 1);

    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;

    public UnitsEverywhereTests() => _services = TestHost.Build(_directory);

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private Registry Registry => _services.GetRequiredService<Registry>();

    // ---------- the helper ----------

    [Theory]
    [InlineData(17.6, "63.7°F")]
    [InlineData(0, "32.0°F")]
    [InlineData(-40, "-40.0°F")]
    [InlineData(-12.5, "9.5°F")]
    [InlineData(60, "140.0°F")]
    public void CelsiusIsShownInFahrenheitAtTheMetricsOwnDecimals(double celsius, string shown) =>
        Assert.Equal(shown, Units.Format(celsius, "°C", 1, Imperial));

    [Fact]
    public void CelsiusStaysCelsiusForSomebodyWhoChoseIt() =>
        Assert.Equal("17.6°C", Units.Format(17.6, "°C", 1, Metric));

    [Theory]
    [InlineData(10, "mph", "km/h", 16.09344)]
    [InlineData(16.09344, "km/h", "mph", 10)]
    [InlineData(29.92, "inHg", "hPa", 1013.2059)]
    [InlineData(1013.25, "hPa", "inHg", 29.9213)]
    [InlineData(1, "in", "mm", 25.4)]
    [InlineData(25.4, "mm", "in", 1)]
    [InlineData(10, "kn", "km/h", 18.52)]
    [InlineData(50, "°C", "°F", 122)]
    [InlineData(122, "°F", "°C", 50)]
    [InlineData(-40, "°F", "°C", -40)]
    [InlineData(0, "°C", "K", 273.15)]
    public void AnyTwoUnitsOfTheSameQuantityConvert(double value, string from, string to, double expected) =>
        Assert.Equal(expected, Units.Convert(value, from, to)!.Value, 2);

    [Theory]
    [InlineData("°C", "mph")]
    [InlineData("inHg", "mm")]
    [InlineData("%", "°F")]
    [InlineData(" ms", "m/s")]
    public void UnitsOfDifferentThingsDoNotConvert(string from, string to) =>
        Assert.Null(Units.Convert(1, from, to));

    [Theory]
    [InlineData(" µg/m³")]
    [InlineData("%")]
    [InlineData(" ms")]
    [InlineData(" GB")]
    [InlineData(" rpm")]
    [InlineData("")]
    // A metric whose unit is "K" is far likelier to be thousands of something than Kelvin.
    [InlineData("K")]
    public void UnitsWithNothingToChooseArePassedThroughUntouched(string unit)
    {
        Assert.Equal((42.5, unit), Units.Display(42.5, unit, Imperial));
        Assert.Equal((42.5, unit), Units.Display(42.5, unit, Metric));
        Assert.Equal("42.5" + unit, Units.Format(42.5, unit, 1, Imperial));
    }

    [Fact]
    public void MetricsStoredInMetricUnitsAreConvertedForAnImperialReader()
    {
        // Not every provider stores what the core does — a JSON field ending _f, a plugin
        // that reports km/h or hPa — and those follow the setting too.
        Assert.Equal("63.7°F", Units.Format(17.6, "°C", 1, Imperial));
        Assert.Equal("10.0 mph", Units.Format(16.09344, " km/h", 1, Imperial));
        Assert.Equal("29.92 inHg", Units.Format(1013.25, " hPa", 2, Imperial));
        Assert.Equal("1.00 in", Units.Format(25.4, " mm", 2, Imperial));
        Assert.Equal("17.6°C", Units.Format(63.68, "°F", 1, Metric));
    }

    [Fact]
    public void AChangeConvertsByTheSizeOfTheUnitNotItsZero()
    {
        Assert.Equal(9, Units.DisplayChange(5, "°C", Imperial).Value, 6);
        Assert.Equal("°F", Units.DisplayChange(5, "°C", Imperial).Unit);
        Assert.Equal(25.4, Units.DisplayChange(1, " in", Metric).Value, 6);
        Assert.Equal((5.0, "%"), Units.DisplayChange(5, "%", Imperial));
    }

    [Theory]
    [InlineData("°F", "°F")]
    [InlineData("F", "°F")]
    [InlineData(" celsius ", "°C")]
    [InlineData("KPH", "km/h")]
    [InlineData("knots", "kn")]
    [InlineData("mb", "mbar")]
    [InlineData("inches", "in")]
    public void WhatPeopleTypeIsReadAsTheUnitTheyMeant(string written, string unit) =>
        Assert.Equal(unit, Units.Parse(written));

    [Theory]
    [InlineData("ms")]
    [InlineData(" degrees")]
    [InlineData("%")]
    [InlineData("")]
    public void AnythingElseIsNotAUnit(string written) => Assert.Null(Units.Parse(written));

    // ---------- a unit written by the person ----------

    [Fact]
    public void AUnitOfTheSameKindConvertsToItWhateverTheSettingSays()
    {
        Assert.Equal((17.6, "°C"), Units.Display(17.6, "°C", Imperial, "°C"));
        Assert.Equal("63.7°F", LiveText.Metric(17.6, 1, "°C", Metric, "°F"));
        Assert.Equal("16.1 km/h", LiveText.Metric(10, 1, " mph", Imperial, "km/h"));
    }

    [Fact]
    public void AnythingElseIsALabelOnANumberThatStillFollowsTheSetting()
    {
        Assert.Equal("63.7 degrees", LiveText.Metric(17.6, 1, "°C", Imperial, " degrees"));
        Assert.Equal("17.6 degrees", LiveText.Metric(17.6, 1, "°C", Metric, " degrees"));
        // A unit of a different kind is a label too: somebody who writes "mph" after a
        // temperature gets their word, not an error and not a conversion.
        Assert.Equal("63.7mph", LiveText.Metric(17.6, 1, "°C", Imperial, "mph"));
        // On a metric with no unit, "°F" is the label it always was — it was never Celsius.
        Assert.Equal("72.0°F", LiveText.Metric(72, 1, "", Metric, "°F"));
        // An empty unit= still hides the unit.
        Assert.Equal("63.7", LiveText.Metric(17.6, 1, "°C", Imperial, ""));
    }

    [Fact]
    public void WithNothingWrittenTheMetricTileAndTheSentenceAgree()
    {
        Assert.Equal("63.7°F", LiveText.Metric(17.6, 1, "°C", Imperial));
        var (value, unit) = Units.Display(17.6, "°C", Imperial, null);
        Assert.Equal("63.7°F", LiveText.Number(value, 1) + unit);
    }

    // ---------- runbook conditions ----------

    private static readonly Connection Nas = new() { Id = "nas", Provider = "http", Name = "NAS" };

    private RunbookFacts Facts(double celsius)
    {
        var registry = Registry;
        return new RunbookFacts(registry, _ => true, _ => null, _ => new Dictionary<string, double> { ["temp_c"] = celsius });
    }

    private static RunbookCondition Condition(string text) => Runbook.ParseCondition(text, out _)!;

    [Theory]
    [InlineData(55, true)]
    [InlineData(50, false)]
    [InlineData(45, false)]
    public void FahrenheitAndCelsiusThresholdsAreTheSameCondition(double celsius, bool hot)
    {
        var facts = Facts(celsius);

        Assert.Equal(hot, facts.Evaluate(Condition("metric: NAS / temp_c > 122°F"), [Nas]).Holds);
        Assert.Equal(hot, facts.Evaluate(Condition("metric: NAS / temp_c > 50°C"), [Nas]).Holds);
        Assert.Equal(hot, facts.Evaluate(Condition("metric: NAS / temp_c > 50 C"), [Nas]).Holds);
    }

    [Fact]
    public void ABareNumberIsStillInTheStoredUnit()
    {
        // 55 °C is 131 °F: a bare 122 is Celsius, as it always was, so it does not hold.
        var facts = Facts(55);

        Assert.True(facts.Evaluate(Condition("metric: NAS / temp_c > 50"), [Nas]).Holds);
        Assert.False(facts.Evaluate(Condition("metric: NAS / temp_c > 122"), [Nas]).Holds);
        Assert.True(facts.Evaluate(Condition("metric: NAS / temp_c == 131°F"), [Nas]).Holds);
        Assert.True(facts.Evaluate(Condition("metric: NAS / temp_c > 323.15 K"), [Nas]).Holds);
    }

    [Fact]
    public void AUnitOfAnotherKindIsStillAMistake()
    {
        var wrong = Facts(55).Evaluate(Condition("metric: NAS / temp_c > 50 mph"), [Nas]);

        Assert.False(wrong.Holds);
        Assert.Contains("in °C", wrong.Problem);
    }

    // ---------- alerts ----------

    [Fact]
    public void AnAlertLineSaysTheReadingAndTheLimitInTheReadersUnits()
    {
        var qnap = new Connection { Id = "qnap", Provider = "http", Name = "QNAP" };
        var hot = new AlertRule { Metric = "temp_c", Threshold = 55, ConnectionId = qnap.Id };
        var named = new AlertRule { Name = "Hot NAS", Metric = "temp_c", Threshold = 50 };
        var now = DateTimeOffset.Now;
        MetricAlertService.Breach[] firing =
        [
            new(hot.Id, qnap.Id, now.AddMinutes(-10), true, 60),
            new(named.Id, qnap.Id, now.AddMinutes(-5), true, 60),
        ];

        var imperial = MarkdownLists.Alerts(firing, [hot, named], [qnap], Registry, null, Imperial);
        Assert.Equal("QNAP · Temperature above 131°F", imperial[0].Name);
        Assert.Equal("140.0°F", imperial[0].Value);
        Assert.Equal("above 131.0°F", imperial[0].Limit);
        Assert.Equal("Hot NAS", imperial[1].Name);
        Assert.Equal("above 122.0°F", imperial[1].Limit);

        var metric = MarkdownLists.Alerts(firing, [hot, named], [qnap], Registry, null, Metric);
        Assert.Equal("QNAP · Temperature above 55°C", metric[0].Name);
        Assert.Equal("60.0°C", metric[0].Value);
        Assert.Equal("above 55.0°C", metric[0].Limit);
    }

    [Fact]
    public void TheRuleEditorsInputAndTheAlertTextAgree()
    {
        // Somebody shown °F types 131 into the rule editor; what the alert list then says
        // is the 131 they typed, not the 55 it was stored as.
        var stored = Units.Store(131, "°C", Imperial);
        Assert.Equal(55, stored, 6);
        var rule = new AlertRule { Metric = "temp_c", Threshold = stored };
        Assert.Equal("NAS · Temperature above 131°F", rule.Describe(Temp, "NAS", Imperial));
    }

    // ---------- the weather cards ----------

    [Fact]
    public void AWeatherCardFollowsTheSettingUnlessItWasGivenAPresetOfItsOwn()
    {
        Assert.Equal(WeatherUnits.FromSettings, WeatherUnits.Field.Default);
        Assert.Equal(Metric, WeatherUnits.Resolve(new SettingsBag(), Metric));
        Assert.Equal(Metric, WeatherUnits.Resolve(new SettingsBag { ["units"] = WeatherUnits.FromSettings }, Metric));
        // A card saved before this existed, with the old default, keeps reading as it did.
        Assert.Equal(Imperial, WeatherUnits.Resolve(new SettingsBag { ["units"] = "imperial" }, Metric));

        Assert.Equal("63.7°F", WeatherUnits.Temperature(17.6, Imperial));
        Assert.Equal("9.0°F", WeatherUnits.TemperatureChange(5, Imperial));
        Assert.Equal("1013 hPa", WeatherUnits.Pressure(29.92, Metric));
        Assert.Equal("29.92 inHg", WeatherUnits.Pressure(29.92, Imperial));
        Assert.Equal("3.0 mm", WeatherUnits.Rain(0.12, Metric));
    }

    // ---------- the weekly summary ----------

    [Fact]
    public void TheWeeklySummarySaysReadingsInTheReadersUnits()
    {
        var from = new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);
        var forecast = new CapacityForecast(ForecastState.Filling, 2, 10, 0.1, ForecastConfidence.High, 0.9, TimeSpan.FromDays(14), null);
        WeeklySummaryData Week(Units.Preferences units) => new()
        {
            From = from,
            To = from.AddDays(7),
            Zone = TimeZoneInfo.Utc,
            Capacity = [new CapacityLine("Garden", "Rain barrel", 2, 0.5, " in", 2, forecast)],
            Units = units,
        };

        Assert.Contains("Garden · Rain barrel — 2.00 in, +0.50 in a week", WeeklySummary.Build(Week(Imperial)).Text);
        Assert.Contains("Garden · Rain barrel — 50.80 mm, +12.70 mm a week", WeeklySummary.Build(Week(Metric)).Text);
    }
}
