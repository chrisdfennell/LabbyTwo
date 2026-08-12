using LabbyTwo.Core;
using LabbyTwo.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

public class MetricSpecTests
{
    [Theory]
    [InlineData("latency_ms", "Response time", " ms")]
    [InlineData("cpu_percent", "CPU", "%")]
    public void WellKnownNamesAreRecognised(string key, string label, string unit)
    {
        var spec = MetricSpec.Fallback(key);
        Assert.Equal(label, spec.Label);
        Assert.Equal(unit, spec.Unit);
    }

    [Theory]
    [InlineData("fan_rpm", "Fan", " rpm")]
    [InlineData("pool_used_gb", "Pool used", " GB")]
    [InlineData("inverter_watts", "Inverter", " W")]
    [InlineData("printer_bed_c", "Printer bed", "°C")]
    public void ATrailingUnitInTheNameIsHonoured(string key, string label, string unit)
    {
        var spec = MetricSpec.Fallback(key);
        Assert.Equal(label, spec.Label);
        Assert.Equal(unit, spec.Unit);
    }

    [Fact]
    public void AnUnknownNameIsHumanisedAndClaimsNoUnit()
    {
        var spec = MetricSpec.Fallback("widgets_produced");
        Assert.Equal("Widgets produced", spec.Label);
        Assert.Equal("", spec.Unit);
    }

    [Fact]
    public void FormattingUsesTheDeclaredUnitAndPrecision()
    {
        var spec = new MetricSpec("temp_c", "Temperature", "°C", 1);
        Assert.Equal("21.5°C", spec.Format(21.48));
        Assert.Equal("21°C", spec.Format(21.48, decimals: 0));
    }

    private static Registry BuildRegistry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddTestStorage(TestHost.TempDirectory());
        services.AddModules(typeof(Registry).Assembly,
            Path.Combine(Path.GetTempPath(), "labbytwo-none-" + Guid.NewGuid().ToString("n")),
            LoggerFactory.Create(_ => { }).CreateLogger("test"));
        services.AddSingleton<Registry>();
        return services.BuildServiceProvider().GetRequiredService<Registry>();
    }

    [Fact]
    public void AProviderDeclarationBeatsTheFallback()
    {
        var registry = BuildRegistry();
        var qnap = new Connection { Provider = "qnap", Name = "NAS" };

        // The fallback would call this "Disk" from the _percent suffix; QNAP says what it
        // actually measures.
        Assert.Equal("Fullest volume", registry.Metric(qnap, "disk_percent").Label);
    }

    [Fact]
    public void AnUndeclaredMetricStillResolves()
    {
        var registry = BuildRegistry();
        var qnap = new Connection { Provider = "qnap", Name = "NAS" };

        Assert.Equal("Something odd", registry.Metric(qnap, "something_odd").Label);
    }

    [Fact]
    public void TheJsonProviderReportsWhicheverMetricsTheUserConfigured()
    {
        var registry = BuildRegistry();
        var connection = new Connection
        {
            Provider = "json",
            Name = "Solar inverter",
            Settings = new SettingsBag
            {
                ["metrics"] = "output_watts = ac.power\n# a comment\nbattery_percent = battery.soc",
            },
        };

        var keys = registry.MetricsFor(connection).Select(m => m.Key).ToList();

        Assert.Contains("output_watts", keys);
        Assert.Contains("battery_percent", keys);
        Assert.Contains("latency_ms", keys);
        Assert.DoesNotContain("# a comment", keys);

        // And the naming convention still gives them units nobody had to declare.
        Assert.Equal(" W", registry.Metric(connection, "output_watts").Unit);
    }

    [Fact]
    public void MetricLookupOnANullConnectionDoesNotThrow()
    {
        var registry = BuildRegistry();
        Assert.Equal("Response time", registry.Metric(null, "latency_ms").Label);
        Assert.Empty(registry.MetricsFor(null));
    }

    [Fact]
    public void EverySuggestedRuleNamesAMetricItsProviderActuallyReports()
    {
        var registry = BuildRegistry();

        foreach (var provider in registry.Providers)
        {
            var declared = provider.Metrics.Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var suggestion in provider.SuggestedRules)
            {
                // A suggestion for a metric the provider never emits would be offered,
                // accepted, and then silently never fire.
                Assert.True(declared.Contains(suggestion.Metric),
                    $"{provider.Type} suggests a rule on \"{suggestion.Metric}\", which it does not declare.");
                Assert.NotEmpty(suggestion.Name);
            }
        }
    }

    [Fact]
    public void ASuggestedClearThresholdIsOnTheRecoverySideOfTheTrigger()
    {
        var registry = BuildRegistry();

        foreach (var provider in registry.Providers)
        {
            foreach (var rule in provider.SuggestedRules.Where(r => r.ClearThreshold is not null))
            {
                var clear = rule.ClearThreshold!.Value;
                // The wrong side would mean the alert can fire and never clear.
                if (rule.Comparison == Comparison.Above)
                    Assert.True(clear <= rule.Threshold, $"{provider.Type}/{rule.Name}: clear {clear} > threshold {rule.Threshold}");
                else
                    Assert.True(clear >= rule.Threshold, $"{provider.Type}/{rule.Name}: clear {clear} < threshold {rule.Threshold}");
            }
        }
    }

    [Fact]
    public void EveryProviderDeclarationHasAUniqueKey()
    {
        var registry = BuildRegistry();

        foreach (var provider in registry.Providers)
        {
            var keys = provider.Metrics.Select(m => m.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }
}
