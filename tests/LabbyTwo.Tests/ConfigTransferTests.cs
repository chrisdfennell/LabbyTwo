using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// Importing over an install that already has the same rows — which is what restoring a
/// backup is. The import upserts by id, and for a long time "upsert" meant "replace with
/// whatever the file says", including for the things the file never says: the secrets a
/// default export strips, and the fields older exports did not carry at all.
/// </summary>
public sealed class ConfigTransferTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;

    public ConfigTransferTests() => _services = TestHost.ReadyHost(_directory);

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private ConfigStore Config => _services.GetRequiredService<ConfigStore>();
    private AlertRuleStore Rules => _services.GetRequiredService<AlertRuleStore>();

    private ConfigTransfer Transfer => new(Config, Rules, _services.GetRequiredService<Registry>());

    private static readonly DateTimeOffset Silence = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A gateway, a service behind it with a key and a silence, and a rule routed to a channel.</summary>
    private async Task<(Connection Gateway, Connection Sonarr, AlertRule Rule)> SeedAsync()
    {
        var gateway = new Connection { Provider = "http", Name = "Gateway", Settings = new SettingsBag { ["url"] = "http://gw" } };
        await Config.SaveConnectionAsync(gateway);

        var sonarr = new Connection
        {
            Provider = "sonarr",
            Name = "Sonarr",
            Settings = new SettingsBag { ["url"] = "http://nas:8989", ["api_key"] = "hunter2" },
            DependsOn = gateway.Id,
            SilencedUntil = Silence,
        };
        await Config.SaveConnectionAsync(sonarr);

        var rule = new AlertRule { Metric = "disk_pct", Threshold = 90, ConnectionId = sonarr.Id, ChannelId = gateway.Id };
        await Rules.SaveAsync(rule);

        return (gateway, sonarr, rule);
    }

    [Fact]
    public async Task ReimportingADefaultExportKeepsKeysDependenciesSilencesAndChannels()
    {
        var (gateway, sonarr, rule) = await SeedAsync();

        var json = await Transfer.ExportAsync(includeSecrets: false);
        Assert.DoesNotContain("hunter2", json);

        var result = await Transfer.ImportAsync(json);

        var restored = (await Config.ConnectionAsync(sonarr.Id))!;
        Assert.Equal("hunter2", restored.Settings.Get("api_key"));
        Assert.Equal(gateway.Id, restored.DependsOn);
        Assert.Equal(Silence, restored.SilencedUntil);
        Assert.Equal(gateway.Id, (await Rules.GetAsync(rule.Id))!.ChannelId);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("credentials"));
    }

    /// <summary>
    /// Onto an install that has never seen these rows, the new fields have to arrive from
    /// the file itself — that is the only place they can come from.
    /// </summary>
    [Fact]
    public async Task AnExportCarriesDependenciesAndChannelsToAFreshInstall()
    {
        var (gateway, sonarr, rule) = await SeedAsync();
        var json = await Transfer.ExportAsync(includeSecrets: true);

        var elsewhere = TestHost.TempDirectory();
        var other = TestHost.ReadyHost(elsewhere);
        try
        {
            var config = other.GetRequiredService<ConfigStore>();
            var rules = other.GetRequiredService<AlertRuleStore>();
            await new ConfigTransfer(config, rules, other.GetRequiredService<Registry>()).ImportAsync(json);

            var arrived = (await config.ConnectionAsync(sonarr.Id))!;
            Assert.Equal(gateway.Id, arrived.DependsOn);
            Assert.Equal("hunter2", arrived.Settings.Get("api_key"));

            // A silence is something happening on the install it was set on, not
            // configuration to hand to somebody else.
            Assert.Null(arrived.SilencedUntil);

            Assert.Equal(gateway.Id, (await rules.GetAsync(rule.Id))!.ChannelId);
        }
        finally
        {
            TestHost.Teardown(other, elsewhere);
        }
    }

    [Fact]
    public async Task WithoutSecretsAFreshInstallIsToldToEnterThem()
    {
        await SeedAsync();
        var json = await Transfer.ExportAsync(includeSecrets: false);

        var elsewhere = TestHost.TempDirectory();
        var other = TestHost.ReadyHost(elsewhere);
        try
        {
            var result = await new ConfigTransfer(other.GetRequiredService<ConfigStore>(),
                other.GetRequiredService<AlertRuleStore>(), other.GetRequiredService<Registry>()).ImportAsync(json);

            Assert.Contains(result.Warnings, w => w.Contains("Sonarr") && w.Contains("credentials"));
        }
        finally
        {
            TestHost.Teardown(other, elsewhere);
        }
    }

    /// <summary>
    /// A file written before dependencies and channels were exported says nothing about
    /// them, and "nothing" there means "not recorded", not "none". Every existing value
    /// survives — the ones the file could not carry and the key it chose not to.
    /// </summary>
    [Fact]
    public async Task AnOlderFileLeavesWhatItDoesNotMentionAlone()
    {
        var (gateway, sonarr, rule) = await SeedAsync();

        var json = $$"""
            {
              "version": 2,
              "includesSecrets": false,
              "connections": [
                { "id": "{{sonarr.Id}}", "provider": "sonarr", "name": "Sonarr (renamed)", "icon": "",
                  "enabled": true, "alerts": true, "sort": 0, "settings": { "url": "http://nas:8989" } }
              ],
              "tabs": [], "widgets": [],
              "rules": [
                { "id": "{{rule.Id}}", "name": "", "connectionId": "{{sonarr.Id}}", "metric": "disk_pct",
                  "comparison": "Above", "threshold": 95, "forMinutes": 0, "enabled": true }
              ]
            }
            """;

        await Transfer.ImportAsync(json);

        var restored = (await Config.ConnectionAsync(sonarr.Id))!;
        Assert.Equal("Sonarr (renamed)", restored.Name);
        Assert.Equal("hunter2", restored.Settings.Get("api_key"));
        Assert.Equal(gateway.Id, restored.DependsOn);
        Assert.Equal(Silence, restored.SilencedUntil);

        var restoredRule = (await Rules.GetAsync(rule.Id))!;
        Assert.Equal(95, restoredRule.Threshold);
        Assert.Equal(gateway.Id, restoredRule.ChannelId);
    }

    /// <summary>
    /// A current export does state these, including "none", so restoring one taken before a
    /// dependency was added puts things back as they were rather than keeping the newer link.
    /// </summary>
    [Fact]
    public async Task ACurrentFileIsBelievedWhenItSaysThereIsNoDependency()
    {
        var sonarr = new Connection { Provider = "sonarr", Name = "Sonarr", Settings = new SettingsBag { ["api_key"] = "k" } };
        await Config.SaveConnectionAsync(sonarr);
        var json = await Transfer.ExportAsync(includeSecrets: false);

        var gateway = new Connection { Provider = "http", Name = "Gateway" };
        await Config.SaveConnectionAsync(gateway);
        await Config.SaveConnectionAsync((await Config.ConnectionAsync(sonarr.Id))! with { DependsOn = gateway.Id });

        await Transfer.ImportAsync(json);

        var restored = (await Config.ConnectionAsync(sonarr.Id))!;
        Assert.Null(restored.DependsOn);
        Assert.Equal("k", restored.Settings.Get("api_key"));
    }

    /// <summary>
    /// A secret the file does carry wins: the point is only to not blank what it could not
    /// have known.
    /// </summary>
    [Fact]
    public async Task ASecretInTheFileStillReplacesTheOneOnTheInstall()
    {
        var (_, sonarr, _) = await SeedAsync();

        var json = $$"""
            {
              "version": 2,
              "includesSecrets": false,
              "connections": [
                { "id": "{{sonarr.Id}}", "provider": "sonarr", "name": "Sonarr", "icon": "",
                  "enabled": true, "alerts": true, "sort": 0, "settings": { "api_key": "rotated" } }
              ]
            }
            """;

        await Transfer.ImportAsync(json);

        Assert.Equal("rotated", (await Config.ConnectionAsync(sonarr.Id))!.Settings.Get("api_key"));
    }
}
