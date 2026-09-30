using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// <c>{{chart: …}}</c> read from text: several lines split at commas outside quotes, the
/// window and the fixed span, the drawing options, and every mistake said in words. Also the
/// arithmetic the component leans on — thinning, the refresh cadence, the shared scale — so
/// none of it is only ever checked by looking at a chart.
/// </summary>
public sealed class ChartShortcodeTests
{
    private static ChartSpec Read(string text)
    {
        var code = Shortcodes.Parse(text);
        Assert.NotNull(code);
        var spec = ChartShortcode.Read(code!, out var problem);
        Assert.True(spec is not null, problem);
        return spec!;
    }

    private static string Problem(string text)
    {
        var spec = ChartShortcode.Read(Shortcodes.Parse(text)!, out var problem);
        Assert.Null(spec);
        Assert.NotNull(problem);
        return problem!;
    }

    [Fact]
    public void ItIsABlockKindFoundInText()
    {
        var found = Shortcodes.Find("Before\n\n{{chart: NAS / cpu_percent last=24h}}\n\nafter");
        var code = Assert.Single(found).Code;
        Assert.Equal("chart", code.Kind);
        Assert.True(code.IsBlock);
        Assert.Empty(Shortcodes.Unknown("{{chart: NAS / cpu_percent}}"));
    }

    [Fact]
    public void OneLineWithAWindow()
    {
        var spec = Read("{{chart: \"NAS\" / cpu_percent last=24h}}");
        Assert.Equal([new ChartLine("NAS", "cpu_percent")], spec.Lines);
        Assert.Equal(TimeSpan.FromHours(24), spec.Window);
        Assert.False(spec.IsFixed);
        Assert.Equal(ChartShortcode.DefaultHeight, spec.Height);
        Assert.Null(spec.Min);
        Assert.Null(spec.Unit);
    }

    [Fact]
    public void SeveralLinesAreSplitAtCommas()
    {
        var spec = Read("{{chart: \"NAS\" / cpu_percent, \"NAS\" / mem_percent last=7d}}");
        Assert.Equal([new ChartLine("NAS", "cpu_percent"), new ChartLine("NAS", "mem_percent")], spec.Lines);
        Assert.Equal(TimeSpan.FromDays(7), spec.Window);
    }

    [Fact]
    public void ACommaInsideQuotesIsPartOfTheName()
    {
        var spec = Read("{{chart: \"Home, office\" / temp_c, Home Assistant / Outdoor temperature}}");
        Assert.Equal("Home, office", spec.Lines[0].Connection);
        Assert.Equal("Home Assistant", spec.Lines[1].Connection);
        Assert.Equal("Outdoor temperature", spec.Lines[1].Metric);
        Assert.Equal(ChartShortcode.DefaultWindow, spec.Window);
    }

    [Fact]
    public void AQuotedNameMayHoldASlashAndEscapedQuotes()
    {
        var spec = Read("{{chart: \"Living room / TV\" / latency_ms, \"The \\\"big\\\" one\" / cpu}}");
        Assert.Equal("Living room / TV", spec.Lines[0].Connection);
        Assert.Equal("The \"big\" one", spec.Lines[1].Connection);
    }

    [Fact]
    public void TheSparklineShorthandWindowWorksToo()
    {
        var spec = Read("{{chart: NAS / cpu_percent 30d}}");
        Assert.Equal("cpu_percent", spec.Lines[0].Metric);
        Assert.Equal(TimeSpan.FromDays(30), spec.Window);
        Assert.Equal("", Read("{{chart: NAS}}").Lines[0].Metric);
    }

    [Fact]
    public void OptionsMaySitOnAnyLine()
    {
        var spec = Read("{{chart: NAS / cpu_percent height=300 min=0, Plex / cpu_percent max=100 title=\"CPU, both\" unit=\"%\"}}");
        Assert.Equal(300, spec.Height);
        Assert.Equal(0, spec.Min);
        Assert.Equal(100, spec.Max);
        Assert.Equal("CPU, both", spec.Title);
        Assert.Equal("%", spec.Unit);
        Assert.Equal(2, spec.Lines.Count);
    }

    [Fact]
    public void AnEmptyUnitIsKeptApartFromNone()
    {
        Assert.Equal("", Read("{{chart: NAS / temp_c unit=\"\"}}").Unit);
        Assert.Null(Read("{{chart: NAS / temp_c}}").Unit);
        Assert.Equal("°C", Read("{{chart: NAS / temp_c suffix=°C}}").Unit);
    }

    [Fact]
    public void AFixedSpanIsDatesInTheLabsZone()
    {
        var spec = Read("{{chart: NAS / latency_ms from=2026-09-01 to=2026-10-01}}");
        Assert.True(spec.IsFixed);
        Assert.Equal(new DateOnly(2026, 9, 1), spec.From);
        Assert.Equal(new DateOnly(2026, 10, 1), spec.To);
        Assert.Equal("1–30 Sep 2026", ChartShortcode.Describe(spec));

        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/Plus2", TimeSpan.FromHours(2), "+2", "+2");
        var (from, to) = spec.Span(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), zone);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(2)), from);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(2)), to);
        Assert.False(spec.IsLive(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), zone));
        Assert.True(spec.IsLive(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero), zone));
    }

    [Fact]
    public void AMonthAcrossTheAutumnClockChangeIsAnHourLonger()
    {
        // UTC+1 in summer, UTC+0 from the last Sunday of October.
        var london = TimeZoneInfo.CreateCustomTimeZone("Test/London", TimeSpan.Zero, "London", "GMT", "BST",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 5, DayOfWeek.Sunday)),
        ]);
        var spec = Read("{{chart: NAS from=2026-10-01 to=2026-11-01}}");
        var (from, to) = spec.Span(DateTimeOffset.UtcNow, london);
        Assert.Equal(TimeSpan.FromDays(31) + TimeSpan.FromHours(1), to - from);
    }

    [Fact]
    public void FromWithoutToRunsUntilNow()
    {
        var spec = Read("{{chart: NAS from=2026-09-01}}");
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(now, spec.Span(now, TimeZoneInfo.Utc).To);
        Assert.True(spec.IsLive(now, TimeZoneInfo.Utc));
        Assert.Equal("since 1 Sep 2026", ChartShortcode.Describe(spec));
    }

    [Theory]
    [InlineData("{{chart: NAS / cpu_percent last=45y}}", "is not a window")]
    [InlineData("{{chart: NAS / cpu_percent last=2y}}", "is not a window")]
    [InlineData("{{chart: NAS / cpu_percent last=30min}}", "between 1h and 365d")]
    [InlineData("{{chart: NAS / cpu_percent last=400d}}", "between 1h and 365d")]
    [InlineData("{{chart: NAS / cpu_percent height=20}}", "from 80 to 600")]
    [InlineData("{{chart: NAS / cpu_percent height=tall}}", "from 80 to 600")]
    [InlineData("{{chart: NAS / cpu_percent min=low}}", "min=“low” is not a number")]
    [InlineData("{{chart: NAS / cpu_percent min=10 max=5}}", "has to be above min=")]
    [InlineData("{{chart: NAS / cpu_percent from=01/09/2026}}", "is not a date")]
    [InlineData("{{chart: NAS / cpu_percent to=2026-10-01}}", "needs a from=")]
    [InlineData("{{chart: NAS / cpu_percent from=2026-10-01 to=2026-09-01}}", "has to come after from=")]
    [InlineData("{{chart: NAS / cpu_percent from=2024-01-01 to=2026-01-01}}", "at most 365 days")]
    [InlineData("{{chart: last=24h}}", "Say what to draw")]
    [InlineData("{{chart: NAS / disks / one}}", "more than a connection and a metric")]
    [InlineData("{{chart: a/x, b/x, c/x, d/x, e/x, f/x, g/x, h/x, i/x}}", "at most 8 lines")]
    public void MistakesAreSaidInWords(string text, string expected) =>
        Assert.Contains(expected, Problem(text));

    [Fact]
    public void HeightMayBeWrittenInPixels() =>
        Assert.Equal(240, Read("{{chart: NAS height=240px}}").Height);

    [Fact]
    public void WriteReadsBackExactly()
    {
        var lines = new[] { new ChartLine("Home, office", "latency_ms"), new ChartLine("Living room / TV", "cpu_percent"), new ChartLine("NAS", "") };
        var written = ChartShortcode.Write(lines,
            [new("from", "2026-09-01"), new("to", "2026-10-01"), new("title", "Response \"time\" {{x}}"), new("height", "")]);

        var found = Assert.Single(Shortcodes.Find(written));
        Assert.Equal(written, found.Code.Source);
        var spec = ChartShortcode.Read(found.Code, out var problem);
        Assert.Null(problem);
        Assert.Equal(lines, spec!.Lines);
        Assert.Equal("Response \"time\" {{x}}", spec.Title);
        Assert.Equal(new DateOnly(2026, 9, 1), spec.From);
    }

    [Fact]
    public void BracesInAQuotedTitleDoNotEndTheShortcode()
    {
        var found = Assert.Single(Shortcodes.Find("{{chart: NAS title=\"a }} b\"}} tail"));
        Assert.Equal("a }} b", ChartShortcode.Read(found.Code, out _)!.Title);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(24, 432)]
    [InlineData(24 * 7, 3024)]
    [InlineData(24 * 30, 3600)]
    [InlineData(24 * 365, 3600)]
    public void ALongerChartIsReadLessOften(int hours, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), ChartShortcode.RefreshEvery(TimeSpan.FromHours(hours)));

    [Fact]
    public void ThinningKeepsTheEndsAndAveragesTheMiddle()
    {
        var values = Enumerable.Range(0, 20_160).Select(i => (double)i).ToList();
        var thinned = ChartShortcode.Thin(values);

        Assert.Equal(ChartShortcode.MaxPoints, thinned.Count);
        Assert.Equal(0, thinned[0]);
        Assert.Equal(20_159, thinned[^1]);
        Assert.True(thinned.Zip(thinned.Skip(1)).All(pair => pair.Second > pair.First));
        var few = values.Take(10).ToList();
        Assert.Same(few, ChartShortcode.Thin(few));
    }

    [Fact]
    public void LinesShareOneScaleFromAllOfThem()
    {
        var plot = ChartPlot.Build([[10, 20], [0, 40]]);
        Assert.Equal(0, plot.Low);
        Assert.Equal(40, plot.High);
        Assert.Equal("0,29 100,20", plot.Lines[0].Points);
        Assert.Equal("0,38 100,2", plot.Lines[1].Points);
    }

    [Fact]
    public void AFixedScaleClampsValuesToItsEdges()
    {
        var plot = ChartPlot.Build([[-5, 50, 150]], min: 0, max: 100);
        Assert.Equal(0, plot.Low);
        Assert.Equal(100, plot.High);
        Assert.Equal("0,38 50,20 100,2", plot.Lines[0].Points);
    }

    [Fact]
    public void AFlatLineSitsInTheMiddle()
    {
        var plot = ChartPlot.Build([[7, 7, 7]]);
        Assert.Equal("0,20 50,20 100,20", plot.Lines[0].Points);
        // A floor with nothing above it still has a top, a unit up, so the line lies on the floor.
        var floored = ChartPlot.Build([[5, 5]], min: 5);
        Assert.Equal(6, floored.High);
        Assert.Equal("0,38 100,38", floored.Lines[0].Points);
    }
}
