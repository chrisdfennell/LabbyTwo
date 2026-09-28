using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Tests;

/// <summary>
/// Unusual rules meeting what already exists: a database full of threshold rules, and
/// exports written before the kind was a thing. Both must come through as exactly the
/// threshold rules they always were.
/// </summary>
public sealed class UnusualRuleStorageTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;

    public UnusualRuleStorageTests() => _services = TestHost.ReadyHost(_directory);

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private AlertRuleStore Rules => _services.GetRequiredService<AlertRuleStore>();

    private ConfigTransfer Transfer => new(
        _services.GetRequiredService<ConfigStore>(), Rules, _services.GetRequiredService<Registry>());

    [Fact]
    public async Task AnUnusualRuleSurvivesARoundTripThroughTheStore()
    {
        var rule = new AlertRule
        {
            Metric = "download_mbps",
            Kind = RuleKind.Unusual,
            UnusualBy = UnusualBy.Spread,
            Comparison = Comparison.Either,
            Threshold = 4,
            ClearThreshold = 2,
            ForMinutes = 30,
        };
        await Rules.SaveAsync(rule);

        Assert.Equal(rule, await Rules.GetAsync(rule.Id));
    }

    [Fact]
    public async Task ExistingRulesComeThroughTheMigrationAsThresholds()
    {
        // A database as the previous release left it: at migration 10, with neither new
        // column, holding a "below" rule.
        var db = _services.GetRequiredService<Db>();
        await using (var connection = await db.OpenAsync())
        {
            var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                ALTER TABLE alert_rules DROP COLUMN unusual_by;
                ALTER TABLE alert_rules DROP COLUMN kind;
                INSERT INTO alert_rules (id, name, connection_id, metric, comparison, threshold, clear_threshold,
                                         for_minutes, enabled, channel_id)
                VALUES ('old', 'Battery low', NULL, 'battery_percent', 'below', 20, 30, 5, 1, NULL);
                PRAGMA user_version = 10;
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        var reopened = new Db(Options.Create(new LabbyOptions { DatabasePath = Path.Combine(_directory, "test.db") }),
            _services.GetRequiredService<IHostEnvironment>());
        await reopened.EnsureSchemaAsync();

        var rule = Assert.Single(await new AlertRuleStore(reopened).AllAsync());
        Assert.Equal(RuleKind.Threshold, rule.Kind);
        Assert.Equal(Comparison.Below, rule.Comparison);
        Assert.Equal(20, rule.Threshold);
        Assert.Equal(30, rule.ClearThreshold);
        Assert.Equal(Verdict.Breaching, rule.Judge(15, null));
    }

    [Fact]
    public async Task AnExportFromBeforeUnusualRulesImportsAsThresholds()
    {
        var json = """
            {
              "version": 2,
              "includesSecrets": false,
              "connections": [], "tabs": [], "widgets": [],
              "rules": [
                { "id": "legacy", "name": "", "connectionId": null, "metric": "disk_percent",
                  "comparison": "Above", "threshold": 90, "clearThreshold": 85, "forMinutes": 10, "enabled": true }
              ]
            }
            """;

        var result = await Transfer.ImportAsync(json);

        Assert.Equal(1, result.Rules);
        var rule = (await Rules.GetAsync("legacy"))!;
        Assert.Equal(RuleKind.Threshold, rule.Kind);
        Assert.Equal(Comparison.Above, rule.Comparison);
        Assert.Equal(90, rule.Threshold);
    }

    [Fact]
    public async Task AnExportCarriesAnUnusualRuleAndLeavesAThresholdRuleAsItWas()
    {
        var unusual = new AlertRule
        {
            Metric = "download_mbps",
            Kind = RuleKind.Unusual,
            UnusualBy = UnusualBy.Percent,
            Comparison = Comparison.Below,
            Threshold = 50,
            ForMinutes = 30,
        };
        var threshold = new AlertRule { Metric = "disk_percent", Comparison = Comparison.Above, Threshold = 90 };
        await Rules.SaveAsync(unusual);
        await Rules.SaveAsync(threshold);

        var json = await Transfer.ExportAsync(includeSecrets: false);

        // A threshold rule's entry is exactly what an older release wrote, so an older
        // release reading this file sees nothing it does not understand for it.
        Assert.Equal(1, CountOf(json, "\"kind\""));
        Assert.Equal(1, CountOf(json, "\"unusualBy\""));

        await Rules.DeleteAsync(unusual.Id);
        await Rules.DeleteAsync(threshold.Id);
        await Transfer.ImportAsync(json);

        Assert.Equal(unusual, await Rules.GetAsync(unusual.Id));
        Assert.Equal(threshold, await Rules.GetAsync(threshold.Id));
    }

    private static int CountOf(string text, string fragment) =>
        (text.Length - text.Replace(fragment, "").Length) / fragment.Length;
}
