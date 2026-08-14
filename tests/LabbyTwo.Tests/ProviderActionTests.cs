using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// Conventions for the buttons providers offer. An action is the only thing in the app
/// that changes the world rather than reporting on it, so the guard rails here are about
/// what happens when one is wrong — a dangerous button with no confirmation, a button
/// offered on a connection that cannot run it, an alert for an outage you asked for.
/// </summary>
public class ProviderActionTests
{
    private static Registry Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddTestStorage(TestHost.TempDirectory());
        services.AddModules(
            typeof(Registry).Assembly,
            Path.Combine(Path.GetTempPath(), "labbytwo-no-plugins-" + Guid.NewGuid().ToString("n")),
            LoggerFactory.Create(_ => { }).CreateLogger("test"));
        services.AddSingleton<Registry>();
        return services.BuildServiceProvider().GetRequiredService<Registry>();
    }

    private static IEnumerable<(string Owner, ProviderAction Action)> EveryAction(Registry registry) =>
        registry.Providers.SelectMany(p => p.Actions.Select(a => (p.DisplayName, a)));

    [Fact]
    public void EveryActionHasSomethingToSayOnItsButton()
    {
        var nameless = EveryAction(Build())
            .Where(entry => string.IsNullOrWhiteSpace(entry.Action.Id)
                         || string.IsNullOrWhiteSpace(entry.Action.Label))
            .Select(entry => $"{entry.Owner} · {entry.Action.Id}")
            .ToList();

        Assert.Empty(nameless);
    }

    [Fact]
    public void ActionIdsAreUniqueWithinAProvider()
    {
        // The runner finds an action by id. Two with the same one means whichever is
        // listed first wins, silently, and the other button does the wrong thing.
        var duplicated = Build().Providers
            .SelectMany(provider => provider.Actions
                .GroupBy(action => action.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => $"{provider.DisplayName} · {group.Key}"))
            .ToList();

        Assert.Empty(duplicated);
    }

    [Fact]
    public void DangerousOverridesAProviderThatAsksToSkipTheConfirmation()
    {
        // The failure this prevents is a wall tablet somebody leans on and a NAS that
        // powers off, so Dangerous wins over Confirms rather than the two being independent.
        var action = new ProviderAction("shutdown", "Shut down") { Confirms = false, Dangerous = true };

        Assert.True(action.NeedsConfirming);
    }

    [Fact]
    public void AnOrdinaryActionConfirmsUnlessItSaysOtherwise()
    {
        // The default has to be the safe one: a provider author who thinks about none of
        // this should still get a confirmation.
        Assert.True(new ProviderAction("go", "Go").NeedsConfirming);
        Assert.False(new ProviderAction("go", "Go") { Confirms = false }.NeedsConfirming);
    }

    [Fact]
    public void AnythingThatTakesTheMachineAwaySilencesItForLongEnoughToComeBack()
    {
        // A disruptive action that silences for thirty seconds is worse than one that does
        // not silence at all: it looks handled and still pages you.
        var tooShort = EveryAction(Build())
            .Where(entry => entry.Action.Disrupts is { } window && window < TimeSpan.FromMinutes(2))
            .Select(entry => $"{entry.Owner} · {entry.Action.Id}")
            .ToList();

        Assert.Empty(tooShort);
    }

    [Fact]
    public void ADangerousActionExplainsWhatItCosts()
    {
        // "Are you sure?" is not information. The dialog is the last place anybody can be
        // told that the shares go away too.
        var mute = EveryAction(Build())
            .Where(entry => entry.Action.Dangerous && string.IsNullOrWhiteSpace(entry.Action.ConfirmMessage))
            .Select(entry => $"{entry.Owner} · {entry.Action.Id}")
            .ToList();

        Assert.Empty(mute);
    }
}

/// <summary>What the QNAP controls will and will not offer to do.</summary>
public class QnapActionTests
{
    private static QnapProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        return ActivatorUtilities.CreateInstance<QnapProvider>(services.BuildServiceProvider());
    }

    private static Connection WithMac(string? mac) =>
        new()
        {
            Provider = "qnap",
            Name = "NAS",
            Settings = mac is null ? new SettingsBag() : new SettingsBag { ["mac"] = mac },
        };

    [Fact]
    public void WakeOnLanIsHiddenUntilThereIsAMacToSendItTo()
    {
        var offered = Provider().ActionsFor(WithMac(null)).Select(action => action.Id).ToList();

        Assert.DoesNotContain("wake", offered);
        // The others do not need one, and hiding them too would be a NAS you cannot restart.
        Assert.Contains("restart", offered);
        Assert.Contains("shutdown", offered);
    }

    [Theory]
    [InlineData("00:11:22:33:44:55")]
    [InlineData("00-11-22-33-44-55")]
    [InlineData("001122334455")]
    [InlineData("0011.2233.4455")]
    [InlineData("  00:11:22:33:44:55  ")]
    public void AMacIsAcceptedHoweverItWasPasted(string mac)
    {
        // People copy this from the router, the label or QTS, and all three punctuate it
        // differently. Rejecting one of them reads as "Wake on LAN does not work".
        Assert.Contains("wake", Provider().ActionsFor(WithMac(mac)).Select(action => action.Id));
    }

    [Theory]
    [InlineData("not a mac")]
    [InlineData("00:11:22:33:44")]        // five octets
    [InlineData("00:11:22:33:44:55:66")]  // seven
    [InlineData("")]
    public void AMacThatCannotWorkHidesTheButtonRatherThanFailingLater(string mac)
    {
        Assert.DoesNotContain("wake", Provider().ActionsFor(WithMac(mac)).Select(action => action.Id));
    }

    [Theory]
    [InlineData("Good", false)]
    [InlineData("Normal", false)]
    [InlineData("--", false)]
    [InlineData("Warning", true)]
    [InlineData("Abnormal", true)]
    [InlineData("Error", true)]
    [InlineData("Something QTS has not invented yet", true)]
    public void AnUnfamiliarHealthWordCountsAsTrouble(string health, bool failing)
    {
        // Matched on the good words rather than the bad ones. A new failure word must not
        // read as healthy; a new healthy word costs one spurious warning, which is the
        // cheaper of the two mistakes.
        var disk = new QnapProvider.DiskInfo("1", "Model", health, 38, 0);

        Assert.Equal(failing, disk.IsFailing);
    }

    [Fact]
    public void ADriveWithNoHealthReadingIsNotCalledFailing()
    {
        Assert.False(new QnapProvider.DiskInfo("1", "Model", null, null, 0).IsFailing);
    }
}

/// <summary>Pi-hole's controls, which exist to prove the surface is not QNAP-shaped.</summary>
public class PiholeActionTests
{
    private static PiholeProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        return ActivatorUtilities.CreateInstance<PiholeProvider>(services.BuildServiceProvider());
    }

    [Fact]
    public void PausingBlockingNeedsTheTokenTheSummaryDoesNot()
    {
        var withoutToken = new Connection { Provider = "pihole", Settings = new SettingsBag { ["url"] = "http://pi.hole" } };
        var withToken = new Connection { Provider = "pihole", Settings = new SettingsBag { ["url"] = "http://pi.hole", ["token"] = "abc" } };

        Assert.Empty(Provider().ActionsFor(withoutToken));
        Assert.Equal(2, Provider().ActionsFor(withToken).Count);
    }
}

/// <summary>
/// The runner, which is where the promises around an action are actually kept. Uses a
/// stand-in provider rather than a real one: what is being tested is what happens around
/// the call, not the call.
/// </summary>
public sealed class ActionRunnerTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly ConfigStore _config;

    public ActionRunnerTests()
    {
        _services = TestHost.Build(_directory);
        _services.GetRequiredService<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        _config = _services.GetRequiredService<ConfigStore>();
    }

    /// <summary>A provider with one of each kind of action and nothing to talk to.</summary>
    private sealed class Stub(bool succeeds) : IConnectionProvider
    {
        public string Type => "stub";
        public string DisplayName => "Stub";
        public string Icon => "🧪";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];
        public int Ran { get; private set; }

        public IReadOnlyList<ProviderAction> Actions =>
        [
            new("reboot", "Reboot") { Dangerous = true, ConfirmMessage = "Goes away.", Disrupts = TimeSpan.FromMinutes(15) },
            new("poke", "Poke") { Confirms = false },
        ];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.Zero));

        public Task<ActionResult> RunActionAsync(Connection connection, ProviderAction action, SettingsBag input, CancellationToken ct)
        {
            Ran++;
            return Task.FromResult(succeeds ? ActionResult.Done("Done.") : ActionResult.Failed("Nope."));
        }
    }

    private async Task<(ActionRunner Runner, Stub Provider, Connection Connection)> SetupAsync(bool succeeds)
    {
        var provider = new Stub(succeeds);
        var registry = new Registry([provider], [], []);
        var health = ActivatorUtilities.CreateInstance<HealthMonitor>(_services, registry);
        var runner = ActivatorUtilities.CreateInstance<ActionRunner>(_services, registry, health);

        var connection = new Connection { Provider = "stub", Name = "The NAS" };
        await _config.SaveConnectionAsync(connection);

        return (runner, provider, connection);
    }

    [Fact]
    public async Task AnActionTheProviderDoesNotHaveIsRefusedRatherThanRun()
    {
        var (runner, provider, connection) = await SetupAsync(succeeds: true);

        var result = await runner.RunAsync(connection, "format-everything");

        Assert.False(result.Ok);
        Assert.Equal(0, provider.Ran);
        Assert.Contains("The NAS", result.Message);
    }

    [Fact]
    public async Task RebootingSilencesTheConnectionSoItDoesNotPageYouAboutItself()
    {
        var (runner, _, connection) = await SetupAsync(succeeds: true);

        var result = await runner.RunAsync(connection, "reboot");
        var stored = await _config.ConnectionAsync(connection.Id);

        Assert.True(result.Ok);
        Assert.True(stored!.IsSilenced(DateTimeOffset.Now));
    }

    [Fact]
    public async Task ARebootThatFailedLeavesTheConnectionAudibleAgain()
    {
        // The silence is taken before the request, because a monitor sweep can land in
        // between. If the request then fails, the machine never went anywhere — and a
        // machine that is genuinely down has to still be able to say so.
        var (runner, _, connection) = await SetupAsync(succeeds: false);

        var result = await runner.RunAsync(connection, "reboot");
        var stored = await _config.ConnectionAsync(connection.Id);

        Assert.False(result.Ok);
        Assert.False(stored!.IsSilenced(DateTimeOffset.Now));
    }

    [Fact]
    public async Task AnActionThatInterruptsNothingSilencesNothing()
    {
        var (runner, _, connection) = await SetupAsync(succeeds: true);

        await runner.RunAsync(connection, "poke");
        var stored = await _config.ConnectionAsync(connection.Id);

        Assert.False(stored!.IsSilenced(DateTimeOffset.Now));
    }

    public void Dispose()
    {
        _services.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A test that cannot tidy up after itself is not a failing test.
        }
    }
}
