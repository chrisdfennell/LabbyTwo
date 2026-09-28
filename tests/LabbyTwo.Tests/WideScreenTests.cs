using LabbyTwo.Core;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// What a dashboard does on a screen wider than it was laid out for. The CSS does the
/// work; what can be pinned here is that the choice is stored, that it reaches the html
/// element as the attribute the stylesheet is written against, and that only dashboard
/// kinds are marked to lift the page's width cap.
/// </summary>
public sealed class WideScreenTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private WebApplicationFactory<Program>? _factory;

    public void Dispose()
    {
        _factory?.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // SQLite's pool can hold the file a moment longer; a temp folder left behind is harmless.
        }
    }

    [Fact]
    public void MoreColumnsIsTheDefaultAndStampsNoAttribute()
    {
        var look = Appearance.From(new SettingsBag());

        Assert.Equal("columns", look.WideScreens);
        Assert.Null(look.WideAttribute);
    }

    [Theory]
    [InlineData("columns", null)]
    [InlineData("stretch", "stretch")]
    [InlineData("centred", "centred")]
    // Anything else was not written by the settings page; it gets the default rather than
    // an attribute the stylesheet has no rules for.
    [InlineData("\" onload=\"x", null)]
    [InlineData("", null)]
    public void EachModeBecomesTheAttributeTheStylesheetIsWrittenAgainst(string stored, string? attribute)
    {
        var look = Appearance.From(new SettingsBag { [Appearance.WideScreensKey] = stored });
        Assert.Equal(attribute, look.WideAttribute);
    }

    [Fact]
    public void EveryModeOnOfferIsOneTheAttributeKnows()
    {
        // The page renders from this list; a mode added to it without a matching attribute
        // would be offered and do nothing.
        foreach (var (value, _, _) in Appearance.WideScreenModes)
        {
            var look = Appearance.From(new SettingsBag { [Appearance.WideScreensKey] = value });
            Assert.Equal(value == "columns" ? null : value, look.WideAttribute);
        }
    }

    [Fact]
    public async Task TheChoiceSurvivesARoundTripThroughTheStore()
    {
        using var services = TestHost.Build(_directory);
        var settings = services.GetRequiredService<AppSettingsStore>();

        await settings.SaveAsync(Appearance.WideScreensKey, "stretch");

        var look = Appearance.From(await settings.AllAsync());
        Assert.Equal("stretch", look.WideScreens);
        Assert.Equal("stretch", look.WideAttribute);
    }

    [Theory]
    [InlineData(TabKinds.Grid, true)]
    [InlineData("media", true)]
    [InlineData("weather-station", true)]
    [InlineData(TabKinds.Status, true)]
    [InlineData(TabKinds.Embed, true)]
    [InlineData(TabKinds.Custom, true)]
    [InlineData("containers", true)]
    [InlineData(TabKinds.Notes, false)]
    [InlineData("git", false)]
    public void OnlyDashboardKindsFillAWideScreen(string kind, bool fills)
    {
        using var services = TestHost.Build(_directory);
        var registry = services.GetRequiredService<Registry>();

        Assert.Equal(fills, registry.TabKind(kind)!.FillsWideScreens);
    }

    /// <summary>
    /// The whole page, through the real app: the attribute on html, and the marker the
    /// stylesheet's :has() looks for around a dashboard but not around a page of notes.
    /// </summary>
    [Fact]
    public async Task TheRenderedPageCarriesTheModeAndMarksOnlyDashboards()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("Labby:DatabasePath", Path.Combine(_directory, "labbytwo.db"));
            host.UseSetting("Labby:PluginPath", Path.Combine(_directory, "plugins"));
        });
        var client = _factory.CreateClient();

        var config = _factory.Services.GetRequiredService<ConfigStore>();
        await config.SaveTabAsync(new Tab { Id = "board", Slug = "board", Name = "Board", Kind = TabKinds.Grid });
        await config.SaveTabAsync(new Tab { Id = "notes", Slug = "notes", Name = "Notes", Kind = TabKinds.Notes });

        var board = await client.GetStringAsync("/t/board");
        Assert.Contains("class=\"tab-page fills-width\"", board);
        Assert.DoesNotContain("data-wide=", board);

        var notes = await client.GetStringAsync("/t/notes");
        Assert.Contains("class=\"tab-page\"", notes);
        Assert.DoesNotContain("fills-width", notes);

        await _factory.Services.GetRequiredService<AppSettingsStore>().SaveAsync(Appearance.WideScreensKey, "centred");
        Assert.Contains("data-wide=\"centred\"", await client.GetStringAsync("/t/board"));
    }
}
