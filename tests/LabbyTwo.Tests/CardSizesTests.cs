using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// The size controls in edit mode: which widths are offered, what they are called, and
/// whether the label on a card tells the truth about how much of the row it takes.
/// </summary>
public class CardSizesTests
{
    [Fact]
    public void ThePresetsAreTheWidthsThatTileTwelveColumns()
    {
        Assert.Equal([2, 3, 4, 6, 8, 9, 12], CardSizes.Presets.Select(p => p.Span));
        Assert.Equal(["⅙", "¼", "⅓", "½", "⅔", "¾", "Full"], CardSizes.Presets.Select(p => p.Symbol));
    }

    [Theory]
    [InlineData(6, "Half")]
    [InlineData(12, "Full width")]
    [InlineData(40, "Full width")]      // clamped, as the grid clamps it
    [InlineData(9, "Three quarters")]
    public void AStoredWidthFindsItsPreset(int width, string name) =>
        Assert.Equal(name, CardSizes.PresetFor(width)?.Name);

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(1)]
    public void AnOddWidthIsNoPreset(int width) => Assert.Null(CardSizes.PresetFor(width));

    [Theory]
    [InlineData(6, 12, "½")]
    [InlineData(4, 12, "⅓")]
    [InlineData(8, 12, "⅔")]
    [InlineData(9, 12, "¾")]
    [InlineData(6, 24, "¼")]
    [InlineData(3, 36, "1/12")]
    [InlineData(4, 36, "1/9")]
    [InlineData(5, 12, "5/12")]
    [InlineData(6, 48, "⅛")]
    [InlineData(12, 12, "1")]
    public void FractionsAreReducedAndUseAGlyphWhereThereIsOne(int n, int d, string expected) =>
        Assert.Equal(expected, CardSizes.Fraction(n, d));

    /// <summary>
    /// The heart of it: a "½" card is not half the row everywhere. These are the spans
    /// app.css gives each width in each layout, so a change to one without the other fails.
    /// </summary>
    [Theory]
    [InlineData(6, CardSizes.Layout.Standard, "½ of the row")]
    [InlineData(12, CardSizes.Layout.Standard, "Full row")]
    [InlineData(6, CardSizes.Layout.Wide24, "¼ of the row")]
    [InlineData(6, CardSizes.Layout.Wide36, "⅙ of the row")]
    [InlineData(6, CardSizes.Layout.Wide48, "⅛ of the row")]
    [InlineData(12, CardSizes.Layout.Wide24, "Full row")]      // full width stays a banner
    [InlineData(12, CardSizes.Layout.Wide48, "Full row")]
    [InlineData(3, CardSizes.Layout.Laptop, "⅓ of the row")]   // nothing under a third on a laptop
    [InlineData(2, CardSizes.Layout.Laptop, "⅓ of the row")]
    [InlineData(8, CardSizes.Layout.Laptop, "⅔ of the row")]
    [InlineData(4, CardSizes.Layout.Tablet, "½ of the row")]   // halves and whole rows only
    [InlineData(8, CardSizes.Layout.Tablet, "Full row")]
    [InlineData(2, CardSizes.Layout.Phone, "Full row")]
    public void TheShareIsWhatTheGridReallyDraws(int width, CardSizes.Layout layout, string expected) =>
        Assert.Equal(expected, CardSizes.Share(width, layout));

    [Fact]
    public void EveryLayoutHasAShare()
    {
        var shares = CardSizes.Shares(6);
        Assert.Equal(Enum.GetValues<CardSizes.Layout>(), shares.Select(s => s.Layout));
    }

    [Theory]
    [InlineData(0, "Auto")]
    [InlineData(-2, "Auto")]
    [InlineData(1, "1 row")]
    [InlineData(4, "4 rows")]
    [InlineData(99, "12 rows")]
    public void HeightsReadAsAutoOrRows(int rows, string expected) =>
        Assert.Equal(expected, CardSizes.Height(rows));

    /// <summary>
    /// The label is picked by CSS, one class per layout. A layout the stylesheet never
    /// switches on would be a label that never shows, so each must be named there.
    /// </summary>
    [Fact]
    public void TheStylesheetSwitchesEveryLayoutsLabel()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "wwwroot", "app.css"));
        foreach (var layout in Enum.GetValues<CardSizes.Layout>())
            Assert.Contains($".size-share > .at-{layout.ToString().ToLowerInvariant()} {{ display: inline; }}", css);
    }

    // ---- rendered ----------------------------------------------------------------------

    [Fact]
    public async Task TheCurrentWidthIsTheOnePressed()
    {
        var html = await RenderToolbarAsync(new Widget { Id = "w1", Type = "markdown", Width = 6 }, showHeight: false);

        Assert.Contains("aria-pressed=\"true\" aria-label=\"Half\"", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "aria-pressed=\"true\""));
        Assert.Equal(CardSizes.Presets.Count, System.Text.RegularExpressions.Regex.Matches(html, "aria-pressed=").Count);
        Assert.DoesNotContain("Height", html);
        Assert.Contains("aria-label=\"Remove Notes\"", html);
        Assert.Contains("href=\"api/share/widget?id=w1\"", html);
    }

    [Fact]
    public async Task ACustomPageBlockHasAHeightAndAnOddWidthStillReads()
    {
        var html = await RenderToolbarAsync(new Widget { Id = "b1", Type = "clock", Width = 5, Height = 0 }, showHeight: true);

        Assert.DoesNotContain("aria-pressed=\"true\"", html);
        Assert.Contains("now 5/12", html);
        Assert.Contains("Height", html);
        Assert.Contains(">Auto<", html);
        // Shorter from auto goes nowhere, so it says so.
        Assert.Matches("disabled[^>]*aria-label=\"Make Notes shorter\"|aria-label=\"Make Notes shorter\"[^>]*disabled", html);
    }

    [Fact]
    public async Task TheSizeLabelCarriesEveryLayoutsShare()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CardSizeLabel>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CardSizeLabel.Width)] = 6,
                [nameof(CardSizeLabel.Height)] = 3,
                [nameof(CardSizeLabel.ShowHeight)] = true,
            }));
            // Decoded, because the renderer writes ½ as &#xBD; and the tests read better as text.
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });

        Assert.Contains("<span class=\"at-standard\">½ of the row</span>", html);
        Assert.Contains("<span class=\"at-wide24\">¼ of the row</span>", html);
        Assert.Contains("<span class=\"at-phone\">Full row</span>", html);
        Assert.Contains("3 rows", html);
    }

    private static async Task<string> RenderToolbarAsync(Widget widget, bool showHeight)
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CardEditToolbar>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CardEditToolbar.Widget)] = widget,
                [nameof(CardEditToolbar.Name)] = "Notes",
                [nameof(CardEditToolbar.ShowHeight)] = showHeight,
            }));
            // Decoded, because the renderer writes ½ as &#xBD; and the tests read better as text.
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LabbyTwo.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
