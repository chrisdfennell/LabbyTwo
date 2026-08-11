using LabbyTwo.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// Conventions for the generated forms. Every provider, widget and tab kind describes
/// itself with <see cref="FieldSpec"/> and none of them ship UI, which is what makes the
/// app extensible — and also what lets one careless field ask somebody to find a database
/// id and type it in. These tests are the guard rail on that.
/// </summary>
public class FieldSpecConventionTests
{
    private static Registry Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddModules(
            typeof(Registry).Assembly,
            Path.Combine(Path.GetTempPath(), "labbytwo-no-plugins-" + Guid.NewGuid().ToString("n")),
            LoggerFactory.Create(_ => { }).CreateLogger("test"));
        services.AddSingleton<Registry>();
        return services.BuildServiceProvider().GetRequiredService<Registry>();
    }

    private static IEnumerable<(string Owner, FieldSpec Field)> EveryField(Registry registry) =>
        registry.Providers.SelectMany(p => p.Fields.Select(f => (p.DisplayName, f)))
            .Concat(registry.Widgets.SelectMany(w => w.Fields.Select(f => (w.DisplayName, f))))
            .Concat(registry.TabKinds.SelectMany(k => k.Fields.Select(f => (k.DisplayName, f))));

    [Fact]
    public void NothingAsksSomebodyToTypeAConnectionId()
    {
        // FieldKind.Connection renders a dropdown of what is already configured. A plain
        // text box called "connection" means somebody has to go and find an id, which is
        // how this app used to work and should not again.
        var offenders = EveryField(Build())
            .Where(entry => entry.Field.Kind is FieldKind.Text or FieldKind.Textarea)
            .Where(entry => entry.Field.Key.Contains("connection", StringComparison.OrdinalIgnoreCase))
            .Select(entry => $"{entry.Owner} · {entry.Field.Key}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void EveryFieldSaysWhatItIsFor()
    {
        // A label is the whole interface for a generated form; an empty one is a blank box.
        var nameless = EveryField(Build())
            .Where(entry => string.IsNullOrWhiteSpace(entry.Field.Label))
            .Select(entry => $"{entry.Owner} · {entry.Field.Key}")
            .ToList();

        Assert.Empty(nameless);
    }

    [Fact]
    public void ConnectionFieldsNarrowToOneProvider()
    {
        // Without a filter the dropdown offers every connection you own, including the
        // fifteen that could not possibly work — which is barely better than the id.
        var unfiltered = EveryField(Build())
            .Where(entry => entry.Field.Kind == FieldKind.Connection)
            .Where(entry => string.IsNullOrWhiteSpace(entry.Field.ProviderFilter))
            .Select(entry => $"{entry.Owner} · {entry.Field.Key}")
            .ToList();

        Assert.Empty(unfiltered);
    }

    [Fact]
    public void NothingRequiredIsHiddenBehindTheDisclosure()
    {
        // Advanced fields render inside a collapsed "More settings". A required one there
        // means a form that cannot be completed without discovering a disclosure, which is
        // worse than the long form it was meant to fix.
        var hidden = EveryField(Build())
            .Where(entry => entry.Field.Advanced && entry.Field.Required)
            .Select(entry => $"{entry.Owner} · {entry.Field.Key}")
            .ToList();

        Assert.Empty(hidden);
    }

    [Fact]
    public void SelectFieldsOfferSomethingToSelect()
    {
        var empty = EveryField(Build())
            .Where(entry => entry.Field.Kind == FieldKind.Select)
            .Where(entry => entry.Field.Options is null or { Count: 0 })
            .Select(entry => $"{entry.Owner} · {entry.Field.Key}")
            .ToList();

        Assert.Empty(empty);
    }
}
