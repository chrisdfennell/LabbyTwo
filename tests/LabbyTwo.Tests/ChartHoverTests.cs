using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// What a chart hands to chart-hover.js.
///
/// The script is not tested here — it is fifty lines of pointer arithmetic and the honest
/// way to check it is to hover a real chart, which is what the verification run does. What
/// is worth pinning down is the payload, because it is the contract between the two: a chart
/// that describes itself slightly wrong produces a tooltip that is confidently incorrect,
/// which is worse than one that fails to appear.
/// </summary>
public class ChartHoverTests
{
    private static JsonElement Parse(string? json)
    {
        Assert.NotNull(json);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void AChartWithNothingToShowGetsNoAttributeAtAll()
    {
        // Null rather than an empty object, so the caller leaves the attribute off entirely
        // and the script never looks at it. A crosshair on a chart with one point would be a
        // promise of detail that is not there.
        Assert.Null(ChartHover.Attribute(Array.Empty<double>()));
        Assert.Null(ChartHover.Attribute(new[] { 1.0 }));
        Assert.Null(ChartHover.Attribute([new ChartHover.Series("a", "red", [1.0])]));
    }

    [Fact]
    public void TheUnitIsCarriedExactlyAsTheMetricDeclaredIt()
    {
        // Including the leading space. Losing it is what once turned every " mph" into
        // "0mph" on the page, and the tooltip would repeat the same mistake.
        var chart = Parse(ChartHover.Attribute([1.0, 2.0], " ms", 1));

        Assert.Equal(" ms", chart.GetProperty("u").GetString());
        Assert.Equal(1, chart.GetProperty("d").GetInt32());
    }

    [Fact]
    public void ValuesAreRoundedRatherThanCarriedAtFullPrecision()
    {
        // A raw double serialises as seventeen significant figures. On a day of readings that
        // is most of the attribute, carrying precision the tooltip immediately throws away.
        var chart = Parse(ChartHover.Attribute([1.0 / 3, 2.0 / 3], " ms", 1));

        foreach (var value in chart.GetProperty("s")[0].GetProperty("v").EnumerateArray())
            Assert.True(value.GetRawText().Length <= 6, $"{value.GetRawText()} is longer than the tooltip can show.");
    }

    [Fact]
    public void SeriesKeepTheirNameAndColourSoTheTooltipCanTellThemApart()
    {
        var chart = Parse(ChartHover.Attribute(
        [
            new ChartHover.Series("download", "var(--accent)", [1.0, 2.0]),
            new ChartHover.Series("upload", "var(--warn)", [3.0, 4.0]),
        ], " Mbps", 1));

        var series = chart.GetProperty("s");
        Assert.Equal(2, series.GetArrayLength());
        Assert.Equal("download", series[0].GetProperty("n").GetString());
        Assert.Equal("var(--warn)", series[1].GetProperty("c").GetString());
    }

    /// <summary>
    /// A series too short to draw is dropped rather than sent as an empty line, so a chart
    /// whose second connection has one reading does not offer a row that never has a value.
    /// </summary>
    [Fact]
    public void ASeriesWithOnePointIsLeftOutButTheOthersSurvive()
    {
        var chart = Parse(ChartHover.Attribute(
        [
            new ChartHover.Series("busy", "red", [1.0, 2.0, 3.0]),
            new ChartHover.Series("new", "blue", [9.0]),
        ]));

        Assert.Equal(1, chart.GetProperty("s").GetArrayLength());
        Assert.Equal("busy", chart.GetProperty("s")[0].GetProperty("n").GetString());
    }

    [Fact]
    public void TimesAreOnlyIncludedWhenThereIsATimeAxis()
    {
        var from = DateTimeOffset.FromUnixTimeSeconds(1_755_300_000);
        var to = DateTimeOffset.FromUnixTimeSeconds(1_755_386_400);

        var timed = Parse(ChartHover.Attribute([1.0, 2.0], from: from, to: to));
        Assert.Equal(1_755_300_000, timed.GetProperty("t0").GetInt64());
        Assert.Equal(1_755_386_400, timed.GetProperty("t1").GetInt64());

        // A sparkline with no times must not claim any — the script keys the whole timestamp
        // line off their presence.
        var untimed = Parse(ChartHover.Attribute([1.0, 2.0]));
        Assert.False(untimed.TryGetProperty("t0", out _));
    }

    /// <summary>
    /// A series name can be a connection's name, which somebody typed. It travels as JSON in
    /// an HTML attribute, so it has to survive serialisation intact — the script puts it on
    /// the page with textContent, never as markup.
    /// </summary>
    [Fact]
    public void ANameWithMarkupInItSurvivesAsText()
    {
        var chart = Parse(ChartHover.Attribute(
            [new ChartHover.Series("<img src=x onerror=alert(1)>", "red", [1.0, 2.0])]));

        Assert.Equal("<img src=x onerror=alert(1)>", chart.GetProperty("s")[0].GetProperty("n").GetString());
    }
}
