#pragma warning disable BL0006 // The interactive test renderer is a Renderer, which is the whole point of it.
using System.Net;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Tests;

/// <summary>
/// The power figures against a real database: sources found from what connections have
/// recorded, the same energy before and after the history is rolled up into hours, the
/// user's choices honoured, and <c>{{power}}</c> drawing them.
/// </summary>
public sealed class PowerRenderTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private InteractiveRenderer? _renderer;

    /// <summary>A plug that reports watts and its own meter, like a Shelly.</summary>
    private sealed class Plug : IConnectionProvider
    {
        public string Type => "plug";
        public string DisplayName => "Plug";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];
        public IReadOnlyList<MetricSpec> Metrics =>
            [new("watts", "Power", " W", 1), new("energy_kwh", "Energy used", " kWh", 2), new("latency_ms", "Response time", " ms")];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.FromMilliseconds(3), "OK"));
    }

    public PowerRenderTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        // A day of raw readings, so a two-day history has a day of summaries behind it.
        services.AddTestStorage(_directory, options => options.RetentionDays = 1);
        services.AddSingleton<IConnectionProvider, Plug>();
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<LatestReadings>();
        services.AddSingleton<CapacityForecasts>();
        services.AddSingleton<Offload>();
        services.AddSingleton<DisplayUnits>();
        services.AddSingleton<SharedSeries>();
        services.AddSingleton<Markdown>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<PowerCosts>();
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_renderer is not null)
            await _renderer.DisposeAsync();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private InteractiveRenderer Renderer => _renderer ??= new InteractiveRenderer(_services);

    private async Task<Connection> PlugAsync(string name)
    {
        var connection = new Connection { Provider = "plug", Name = name };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        return connection;
    }

    private async Task InsertAsync(string connectionId, string metric, IEnumerable<(long Ts, double Value)> rows)
    {
        await using var connection = await Get<Db>().OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO samples (connection_id, metric, ts, value) VALUES ($c, $m, $t, $v)";
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$m", metric);
        var ts = cmd.Parameters.Add("$t", SqliteType.Integer);
        var value = cmd.Parameters.Add("$v", SqliteType.Real);
        foreach (var row in rows)
        {
            ts.Value = row.Ts;
            value.Value = row.Value;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    /// <summary>A reading every minute from <paramref name="from"/> up to <paramref name="to"/>.</summary>
    private static IEnumerable<(long, double)> Minutely(DateTimeOffset from, DateTimeOffset to, Func<long, double> value)
    {
        for (var t = from.ToUnixTimeSeconds(); t <= to.ToUnixTimeSeconds(); t += 60)
            yield return (t, value(t));
    }

    [Fact]
    public async Task The_energy_is_the_same_after_the_history_is_rolled_up_into_hours()
    {
        var plug = await PlugAsync("NAS plug");
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        await InsertAsync(plug.Id, "watts", Minutely(now.AddDays(-2), now, _ => 100));

        var before = await Get<PowerCosts>().GetAsync(now, TimeZoneInfo.Utc, CancellationToken.None);
        var rolled = await Get<HistoryStore>().RollupAsync(now, null, CancellationToken.None);
        var after = await Get<PowerCosts>().GetAsync(now, TimeZoneInfo.Utc, CancellationToken.None);

        Assert.True(rolled.HoursWritten > 20, "some of the history should have been rolled up");
        var report = Assert.Single(before.Reports);
        Assert.False(report.FromCounter);
        Assert.Equal(4.8, before.LastWeek.Kwh, 2);
        Assert.Equal(before.LastWeek.Kwh, after.LastWeek.Kwh, 2);
        Assert.Equal(0.72, after.LastWeek.Cost, 2);
    }

    [Fact]
    public async Task A_plugs_own_meter_is_preferred_and_a_reset_is_not_negative_energy()
    {
        var plug = await PlugAsync("Desk plug");
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var start = now.AddHours(-10);
        // Watts say 100 W; the meter says 2 kWh in ten hours (200 W), reset halfway.
        await InsertAsync(plug.Id, "watts", Minutely(start, now, _ => 100));
        var reset = start.AddHours(5).ToUnixTimeSeconds();
        await InsertAsync(plug.Id, "energy_kwh", Minutely(start, now, t =>
            t < reset ? 50 + (t - start.ToUnixTimeSeconds()) / 3600.0 * 0.2 : (t - reset) / 3600.0 * 0.2));

        var snapshot = await Get<PowerCosts>().GetAsync(now, TimeZoneInfo.Utc, CancellationToken.None);

        var report = Assert.Single(snapshot.Reports);
        Assert.True(report.FromCounter);
        Assert.Equal(2.0, report.LastWeek.Kwh, 1);
    }

    [Fact]
    public async Task A_source_left_out_is_not_counted_and_a_named_one_goes_by_its_name()
    {
        var nas = await PlugAsync("NAS plug");
        var ups = await PlugAsync("UPS");
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        await InsertAsync(nas.Id, "watts", Minutely(now.AddHours(-2), now, _ => 100));
        await InsertAsync(ups.Id, "watts", Minutely(now.AddHours(-2), now, _ => 300));

        await Get<PowerCosts>().SaveAsync(PowerTariff.Default, new PowerSetup(
        [
            new(PowerSourceSettings.KeyFor(nas.Id, "watts"), "Rack", true, [new("NAS"), new("Plex")], false),
            new(PowerSourceSettings.KeyFor(ups.Id, "watts"), "", false, [], false),
        ]));
        var snapshot = await Get<PowerCosts>().GetAsync(now, TimeZoneInfo.Utc, CancellationToken.None);

        var report = Assert.Single(snapshot.Reports);
        Assert.Equal("Rack", report.Name);
        Assert.Equal(2, snapshot.Sources.Count);
        Assert.Equal(0.2, snapshot.Today.Kwh, 2);
        Assert.Equal(0.1, snapshot.Service("plex")!.MonthKwh, 2);
    }

    [Fact]
    public async Task The_shortcode_shows_the_total_a_source_and_a_service()
    {
        var plug = await PlugAsync("NAS plug");
        var now = DateTimeOffset.UtcNow;
        await InsertAsync(plug.Id, "watts", Minutely(now.AddHours(-2), now, _ => 100));
        await Get<PowerCosts>().SaveAsync(PowerTariff.Default, new PowerSetup(
            [new(PowerSourceSettings.KeyFor(plug.Id, "watts"), "", true, [new("NAS"), new("Plex")], false)]));

        await Renderer.RenderMarkdownAsync("All: {{power: show=kwh period=30d}}.");
        Assert.Contains("0.20 kWh", WebUtility.HtmlDecode(await Renderer.WaitForAsync("kWh")));

        await Renderer.RenderMarkdownAsync("Plug: {{power: \"NAS plug\" period=30d}}.");
        Assert.Contains("$0.03", WebUtility.HtmlDecode(await Renderer.WaitForAsync("$0.03")));

        await Renderer.RenderMarkdownAsync("Plex: {{power: Plex show=kwh}}.");
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync(h => h.Contains("kWh", StringComparison.Ordinal) || h.Contains("sc-problem", StringComparison.Ordinal)));
        Assert.Contains("share of NAS plug", html);
    }

    [Fact]
    public async Task The_shortcode_says_what_is_wrong_rather_than_guessing()
    {
        var plug = await PlugAsync("NAS plug");
        var now = DateTimeOffset.UtcNow;
        await InsertAsync(plug.Id, "watts", Minutely(now.AddHours(-1), now, _ => 100));

        await Renderer.RenderMarkdownAsync("{{power: Toaster}} and more.");
        Assert.Contains("No power source or service called", WebUtility.HtmlDecode(await Renderer.WaitForAsync("sc-problem")));

        await Renderer.RenderMarkdownAsync("{{power: period=fortnight}} and more.");
        Assert.Contains("is not a period", WebUtility.HtmlDecode(await Renderer.WaitForAsync("sc-problem")));
    }

    [Fact]
    public async Task With_nothing_reporting_power_the_shortcode_says_so()
    {
        await PlugAsync("Router");

        await Renderer.RenderMarkdownAsync("It cost {{power}} this month.");
        Assert.Contains("Nothing reports power yet", WebUtility.HtmlDecode(await Renderer.WaitForAsync("sc-problem")));
    }
}
