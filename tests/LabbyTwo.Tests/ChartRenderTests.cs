#pragma warning disable BL0006 // InteractiveRenderer reads the render tree back into HTML, which is the point of it.
using System.Net;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// <c>{{chart}}</c> drawn by the real component against a real database: lines appearing
/// after the page has drawn, several lines on one scale, readings converted into the
/// reader's units, a fixed span reading only that span, one read shared between two charts
/// of the same thing — and nothing written in the shortcode becoming markup.
/// </summary>
public sealed class ChartRenderTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly InteractiveRenderer _renderer;

    private sealed class Stub : IConnectionProvider
    {
        public string Type => "stub";
        public string DisplayName => "Stub";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];
        public IReadOnlyList<MetricSpec> Metrics =>
            [new("disk_percent", "Disk used", "%", 1), new("latency_ms", "Response time", " ms"), new("temp_c", "Temperature", "°C", 1)];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.FromMilliseconds(3), "OK", new Dictionary<string, double>()));
    }

    public ChartRenderTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);
        services.AddSingleton<IConnectionProvider>(new Stub());
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<CapacityForecasts>();
        services.AddSingleton<MetricBaselines>();
        services.AddSingleton<MetricAlertService>();
        services.AddSingleton<LatestReadings>();
        services.AddSingleton<Offload>();
        services.AddSingleton<DisplayUnits>();
        services.AddSingleton<SharedSeries>();
        services.AddSingleton<Markdown>();
        services.AddSingleton<ActionRunner>();
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        _renderer = new InteractiveRenderer(_services);
    }

    public async ValueTask DisposeAsync()
    {
        await _renderer.DisposeAsync();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private async Task<Connection> ConnectionAsync(string name)
    {
        var connection = new Connection { Provider = "stub", Name = name };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        return connection;
    }

    private async Task SampleAsync(string connectionId, string metric, DateTimeOffset at, double value)
    {
        await using var db = await Get<Db>().OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO samples (connection_id, metric, ts, value) VALUES ($c, $m, $t, $v)";
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$m", metric);
        cmd.Parameters.AddWithValue("$t", at.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$v", value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A day of half-hourly readings, 40 to 44 and round again, ending at <paramref name="last"/>.</summary>
    private async Task DayAsync(string connectionId, string metric, double last, double offset = 0)
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 46; i++)
            await SampleAsync(connectionId, metric, now.AddMinutes(-30 * (47 - i)), 40 + i % 5 + offset);
        await SampleAsync(connectionId, metric, now.AddSeconds(-5), last);
    }

    private static string Text(string html) => WebUtility.HtmlDecode(html);

    [Fact]
    public async Task AChartDrawsItsLineAfterThePage()
    {
        var nas = await ConnectionAsync("QNAP NAS");
        await DayAsync(nas.Id, "disk_percent", 42);

        await _renderer.RenderMarkdownAsync("Disk, lately:\n\n{{chart: QNAP NAS / Disk used last=24h height=240}}\n\nThe end.");
        var html = Text(await _renderer.WaitForAsync("<polyline"));

        Assert.Contains("<figure class=\"md-embed md-chart\" role=\"group\" aria-label=\"QNAP NAS · Disk used over the last 24 hours\">", html);
        Assert.Contains("<span class=\"md-chart-title\">QNAP NAS · Disk used</span>", html);
        Assert.Contains("now 42.0%", html);
        Assert.Contains("style=\"height:240px\"", html);
        Assert.Contains("data-chart=\"{", html);
        // On a line of its own the paragraph goes, as it does for a card.
        Assert.DoesNotContain("<p><figure", html);
        Assert.Contains("<p>The end.</p>", html);
        // The scale is written beside the plot: the lowest and highest of the day.
        Assert.Contains("<span>44.0%</span>", html);
        Assert.Contains("<span>40.0%</span>", html);
    }

    [Fact]
    public async Task SeveralLinesShareOneScaleAndAKey()
    {
        var nas = await ConnectionAsync("NAS");
        var plex = await ConnectionAsync("Plex");
        await DayAsync(nas.Id, "disk_percent", 42);
        await DayAsync(plex.Id, "disk_percent", 90, offset: 40);

        await _renderer.RenderMarkdownAsync("{{chart: NAS / disk_percent, Plex / disk_percent}}");
        var html = Text(await _renderer.WaitForAsync(h => h.Split("<polyline").Length == 3));

        Assert.Equal(3, html.Split("<polyline").Length);
        Assert.Contains("<span class=\"md-chart-title\">Disk used</span>", html);
        Assert.Contains("NAS <span class=\"tile-meta\">42.0%</span>", html);
        Assert.Contains("Plex <span class=\"tile-meta\">90.0%</span>", html);
        Assert.Contains("<span>90.0%</span>", html);
        Assert.Contains("<span>40.0%</span>", html);
    }

    [Fact]
    public async Task ReadingsAreInTheReadersUnitsUnlessUnitSaysOtherwise()
    {
        var nas = await ConnectionAsync("NAS");
        await DayAsync(nas.Id, "temp_c", 20);
        await Get<AppSettingsStore>().SaveAsync(Units.Preferences.SystemKey, Units.Imperial);
        await Get<DisplayUnits>().RefreshAsync();

        await _renderer.RenderMarkdownAsync("{{chart: NAS / temp_c}}\n\n{{chart: NAS / temp_c unit=°C}}\n\n{{chart: NAS / temp_c unit=\" degrees\"}}");
        var html = Text(await _renderer.WaitForAsync(h => h.Split("<polyline").Length == 4));

        Assert.Contains("now 68.0°F", html);
        Assert.Contains("now 20.0°C", html);
        // A word that is not a unit is a label; the number still follows the setting.
        Assert.Contains("now 68.0 degrees", html);
    }

    [Fact]
    public async Task AFixedSpanReadsOnlyThatSpan()
    {
        var nas = await ConnectionAsync("NAS");
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = today.AddDays(-12);
        var to = today.AddDays(-9);
        var start = WeeklySchedule.At(from, TimeOnly.MinValue, TimeZoneInfo.Local);
        var end = WeeklySchedule.At(to, TimeOnly.MinValue, TimeZoneInfo.Local);

        // Inside the span 10 to 30; either side of it, far higher, which must not be read.
        for (var at = start.AddHours(1); at < end; at = at.AddHours(1))
            await SampleAsync(nas.Id, "disk_percent", at, 10 + (at - start).TotalHours % 21);
        await SampleAsync(nas.Id, "disk_percent", start.AddHours(-2), 99);
        await SampleAsync(nas.Id, "disk_percent", end.AddHours(2), 99);
        await DayAsync(nas.Id, "disk_percent", 95, offset: 50);

        await _renderer.RenderMarkdownAsync($"{{{{chart: NAS / disk_percent from={from:yyyy-MM-dd} to={to:yyyy-MM-dd}}}}}");
        var html = Text(await _renderer.WaitForAsync("<polyline"));

        Assert.DoesNotContain("99.0%", html);
        Assert.DoesNotContain("95.0%", html);
        Assert.Contains("<span>30.0%</span>", html);
        Assert.Contains("<span>10.0%</span>", html);
        Assert.Contains(ChartShortcode.Describe(ChartShortcode.Read(Shortcodes.Parse(
            $"{{{{chart: NAS from={from:yyyy-MM-dd} to={to:yyyy-MM-dd}}}}}")!, out _)!), html);
    }

    [Fact]
    public async Task TwoChartsOfTheSameThingShareOneRead()
    {
        var nas = await ConnectionAsync("NAS");
        await DayAsync(nas.Id, "disk_percent", 42);

        await _renderer.RenderMarkdownAsync("{{chart: NAS / disk_percent last=7d}}\n\nAnd again:\n\n{{chart: NAS / disk_percent last=7d title=\"Again\"}}");
        await _renderer.WaitForAsync(h => h.Split("<polyline").Length == 3);

        Assert.Equal(1, Get<SharedSeries>().Queries);
    }

    [Theory]
    [InlineData("{{chart: Nowhere / disk_percent}}", "No connection called “Nowhere”")]
    [InlineData("{{chart: NAS / nothing_here}}", "has not reported a metric called “nothing_here”")]
    [InlineData("{{chart: NAS / disk_percent last=2y}}", "is not a window")]
    [InlineData("See {{chart: NAS / disk_percent}} here.", "goes on a line of its own")]
    public async Task AChartThatCannotBeDrawnSaysWhy(string markdown, string why)
    {
        await ConnectionAsync("NAS");
        await _renderer.RenderMarkdownAsync(markdown);
        Assert.Contains(why, Text(await _renderer.WaitForAsync("sc-problem")));
    }

    [Fact]
    public async Task NothingRecordedSaysSo()
    {
        await ConnectionAsync("NAS");
        await _renderer.RenderMarkdownAsync("{{chart: NAS / disk_percent last=7d}}");
        var html = Text(await _renderer.WaitForAsync("Nothing recorded"));
        Assert.Contains("Nothing recorded over the last 7 days.", html);
        Assert.DoesNotContain("<svg", html);
    }

    [Fact]
    public async Task ScriptInAnyArgumentStaysText()
    {
        var odd = await ConnectionAsync("<img src=x onerror=alert(1)>");
        await DayAsync(odd.Id, "disk_percent", 42);

        await _renderer.RenderMarkdownAsync(
            "{{chart: \"<img src=x onerror=alert(1)>\" / disk_percent title=\"<script>alert(2)</script>\" unit=\"<b>u</b>\"}}\n\n" +
            "{{chart: \"<svg onload=alert(3)>\" / disk_percent}}");
        var raw = await _renderer.WaitForAsync(h => h.Contains("<polyline", StringComparison.Ordinal) && h.Contains("alert(3)", StringComparison.Ordinal));

        Assert.DoesNotContain("<script", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<svg onload", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert(2)&lt;/script&gt;", raw);
        Assert.Contains("&lt;b&gt;u&lt;/b&gt;", raw);
    }
}
