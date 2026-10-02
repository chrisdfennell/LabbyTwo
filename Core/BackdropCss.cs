using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LabbyTwo.Core;

/// <summary>An uploaded background picture, as the server stored it: a content hash and one of three extensions.</summary>
/// <param name="Hash">Sixteen lowercase hex digits of the stored file's SHA-256 — its name, and its cache key.</param>
public sealed partial record BackdropImage(string Hash, string Extension, int Width, int Height, long Bytes)
{
    /// <summary>The URL prefix pictures are served from.</summary>
    public const string Route = "/backdrop";

    /// <summary>
    /// Where it is served. Server-made from the hash and the extension, never from a
    /// filename anybody sent; because the hash changes with the content, the address can be
    /// cached for a year and a new picture is a new address.
    /// </summary>
    public string Url => $"{Route}/{Hash}{Extension}";

    /// <summary>Both parts are checked again here, so a record built any other way still cannot put text in a url().</summary>
    public bool IsValid => HashShape().IsMatch(Hash) && BackdropImageFile.ContentTypes.ContainsKey(Extension);

    public string ContentType => BackdropImageFile.ContentTypes.GetValueOrDefault(Extension, "application/octet-stream");

    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex HashShape();
}

/// <summary>
/// Turns a <see cref="Backdrop"/> into the style block that draws it — the background
/// layer, and frosted glass on the cards.
///
/// The same rule as <see cref="ThemeCss"/>: the output is made from constants in this file,
/// numbers that have been clamped, colours rebuilt by <see cref="BackdropColour.Css"/>, and
/// a picture URL made from a hex hash. No setting is ever copied in as text. The properties
/// written are a fixed list — background-*, filter, backdrop-filter, isolation and a
/// handful of positioning ones — so a background cannot reach anything else on the page.
///
/// <para><b>Why a fixed pseudo-element.</b> The background is a ::before on each screen's
/// outermost element, fixed to the viewport and behind everything. Being out of the flow,
/// it cannot move anything: there is no layout shift when the picture arrives, and nothing
/// to lay out again as the page scrolls. Fixed, it is composited once and simply stays put,
/// so a blurred photo costs one blur, not one per frame — which matters on the wall, where
/// the page is redrawn for ever. The screen element is given <c>isolation: isolate</c> so
/// the layer's z-index of -1 puts it above the page colour but below the cards, rather than
/// under the body's own background where it would never be seen.</para>
///
/// <para><b>Glass, with its fallbacks.</b> Translucent cards are only ever written inside
/// <c>@supports (backdrop-filter)</c>, so a browser that cannot blur keeps solid cards
/// rather than getting see-through ones with no blur behind the text. Somebody who asked
/// their system to reduce transparency gets solid cards whatever the setting says. The phone
/// view is left solid unless asked for separately: blurring behind every card on a cheap
/// phone is the one thing here that can make scrolling stutter.</para>
/// </summary>
public static class BackdropCss
{
    /// <summary>
    /// The outermost element of each screen, as a direct child of body. MainLayout's shell is
    /// the dashboard (and the settings pages, which are part of it); the wall's own root is
    /// .wall even when it keeps the sidebar, whose .app-shell is then inside it and not a
    /// child of body; the phone view's is .phone-shell.
    /// </summary>
    public static readonly IReadOnlyList<(BackdropScreens Screen, string Root)> Roots =
    [
        (BackdropScreens.Dashboard, "body > .app-shell"),
        (BackdropScreens.Wall, "body > .wall"),
        (BackdropScreens.Phone, "body > .phone-shell"),
    ];

    /// <summary>
    /// What turns to glass on the dashboard and the wall: cards, the panels tables sit in,
    /// and the sidebar. A custom page's bare block draws no box, so it stays without one.
    /// Dialogs, menus and tooltips stay solid — they are read over whatever is beneath them,
    /// which is the busiest thing on the screen.
    /// </summary>
    public const string DeskCards = ":is(.widget:not(.is-bare), .panel, .app-nav)";

    /// <summary>
    /// The phone view's boxes. The red status and the warning banners keep their own tinted
    /// fills, which are how they say what they say.
    /// </summary>
    public const string PhoneCards = ":is(.phone-status:not(.is-red), .phone-banner:not(.is-blind):not(.is-maintenance):not(.is-quiet), .phone-row, .phone-rest)";

    /// <summary>
    /// The block for &lt;head&gt;, for every screen the background is on, or empty when there
    /// is nothing to draw — no background chosen, no screens ticked, or a picture chosen with
    /// no picture uploaded. Empty means no rule at all, so the default page is byte for byte
    /// what it was before backgrounds existed.
    /// </summary>
    /// <param name="screens">
    /// The screens it is on, after anything the caller knows that this does not — whether the
    /// "only with this theme" choice matches each screen's theme. Intersected with the
    /// setting's own screens here.
    /// </param>
    public static string Render(Backdrop backdrop, BackdropImage? image, BackdropScreens screens)
    {
        screens &= backdrop.Screens;
        if (!Draws(backdrop, image) || screens == BackdropScreens.None)
            return "";

        var roots = Roots.Where(r => screens.HasFlag(r.Screen)).Select(r => r.Root).ToList();
        var css = new StringBuilder();

        css.Append(string.Join(", ", roots)).Append(" { isolation: isolate; }\n");
        css.Append(string.Join(", ", roots.Select(r => r + "::before")))
           .Append(" { content: \"\"; position: fixed; inset: ").Append(Px(-Bleed(backdrop, image)))
           .Append("; z-index: -1; pointer-events: none; ")
           .Append(LayerDeclarations(backdrop, image)).Append(" }\n");

        if (!backdrop.Glass)
            return css.ToString();

        var cards = new List<string>();
        foreach (var (screen, root) in Roots)
        {
            if (!screens.HasFlag(screen))
                continue;
            if (screen == BackdropScreens.Phone && !backdrop.GlassOnPhone)
                continue;
            // :root in front so the rule outranks the "flat" card surface, whose light-theme
            // rule is the most specific card rule in app.css — without !important, which would
            // also beat the reduced-transparency fallback below.
            cards.Add($":root {root} {(screen == BackdropScreens.Phone ? PhoneCards : DeskCards)}");
        }

        if (cards.Count == 0)
            return css.ToString();

        var selector = string.Join(",\n", cards);
        css.Append("@supports ((-webkit-backdrop-filter: blur(1px)) or (backdrop-filter: blur(1px))) {\n")
           .Append(selector).Append(" { ").Append(GlassDeclarations(backdrop)).Append(" }\n");

        // A wall slide fades in, and a translucent ancestor stops the blur from reaching
        // through to the background until the fade ends. With glass on, it slides instead.
        if (screens.HasFlag(BackdropScreens.Wall))
            css.Append("body > .wall .wall-slide { animation-name: wall-in-glass; }\n");

        css.Append("}\n");

        css.Append("@media (prefers-reduced-transparency: reduce) {\n")
           .Append(selector).Append(" { ").Append(SolidDeclarations).Append(" }\n")
           .Append("}\n");

        return css.ToString();
    }

    /// <summary>Whether this backdrop draws anything, given the picture there is (or is not).</summary>
    public static bool Draws(Backdrop backdrop, BackdropImage? image) => backdrop.Kind switch
    {
        BackdropKind.Solid or BackdropKind.Gradient => true,
        BackdropKind.Image => image is { IsValid: true },
        _ => false,
    };

    /// <summary>What a glass card goes back to: the ordinary card, no blur.</summary>
    public const string SolidDeclarations = "background-color: var(--panel); -webkit-backdrop-filter: none; backdrop-filter: none;";

    /// <summary>
    /// The layer's own declarations — shared with the Appearance page's preview, which draws
    /// them inline on a box, so the preview is the background rather than a picture of it.
    /// </summary>
    public static string LayerDeclarations(Backdrop backdrop, BackdropImage? image)
    {
        var dim = backdrop.Dim > 0 ? DimLayer(backdrop) : null;
        var css = new StringBuilder();

        switch (backdrop.Kind)
        {
            case BackdropKind.Solid:
                css.Append("background-color: ").Append(backdrop.Solid.Css).Append(';');
                if (dim is not null)
                    css.Append(" background-image: ").Append(dim).Append(';');
                break;

            case BackdropKind.Gradient:
                css.Append("background-color: var(--ink); background-image: ");
                if (dim is not null)
                    css.Append(dim).Append(", ");
                css.Append(Gradient(backdrop.Angle, backdrop.Stops)).Append(';');
                break;

            case BackdropKind.Image when image is { IsValid: true }:
                var (size, repeat) = backdrop.Fit switch
                {
                    BackdropFit.Contain => ("contain", "no-repeat"),
                    BackdropFit.Tile => ("auto", "repeat"),
                    _ => ("cover", "no-repeat"),
                };
                var position = Position(backdrop.Position);

                // The URL is the server's own — /backdrop/, sixteen hex digits, one of three
                // extensions — checked by IsValid above, so there is nothing in it to quote.
                css.Append("background-color: var(--ink); background-image: ");
                css.Append(dim is not null ? dim + ", " : "").Append("url(\"").Append(image.Url).Append("\");");
                css.Append(" background-size: ").Append(dim is not null ? "100% 100%, " : "").Append(size).Append(';');
                css.Append(" background-repeat: ").Append(dim is not null ? "no-repeat, " : "").Append(repeat).Append(';');
                css.Append(" background-position: ").Append(dim is not null ? "center, " : "").Append(position).Append(';');
                if (backdrop.Blur > 0)
                    css.Append(" filter: blur(").Append(Px(backdrop.Blur)).Append(");");
                break;

            default:
                return "";
        }

        return css.ToString();
    }

    /// <summary>
    /// The preview box's layer: the same declarations, reaching past the box's edges by the
    /// same bleed as the real layer so a blurred picture has no pale frame there either.
    /// Empty when nothing would be drawn.
    /// </summary>
    public static string PreviewLayerStyle(Backdrop backdrop, BackdropImage? image) =>
        Draws(backdrop, image) ? $"inset: {Px(-Bleed(backdrop, image))}; {LayerDeclarations(backdrop, image)}" : "";

    /// <summary>A glass card's fill and blur. Also drawn inline on the preview's sample card.</summary>
    public static string GlassDeclarations(Backdrop backdrop)
    {
        var opacity = Math.Clamp(backdrop.GlassOpacity, Backdrop.MinGlassOpacity, Backdrop.MaxGlassOpacity);
        var blur = Math.Clamp(backdrop.GlassBlur, 0, Backdrop.MaxGlassBlur);
        var saturate = Math.Clamp(backdrop.GlassSaturation, Backdrop.MinSaturation, Backdrop.MaxSaturation);
        var filter = $"blur({Px(blur)}) saturate({Number(saturate)}%)";
        return $"background-color: color-mix(in srgb, var(--panel) {Number(opacity)}%, transparent); "
             + $"-webkit-backdrop-filter: {filter}; backdrop-filter: {filter};";
    }

    /// <summary>A gradient of two or three colours at an angle. Also used for the preset swatches.</summary>
    public static string Gradient(int angle, IReadOnlyList<BackdropColour> stops)
    {
        var valid = stops.Count is >= Backdrop.MinStops and <= Backdrop.MaxStops ? stops : Backdrop.Default.Stops;
        return $"linear-gradient({Number(Math.Clamp(angle, 0, 359))}deg, {string.Join(", ", valid.Select(s => s.Css))})";
    }

    /// <summary>
    /// The dim, as a flat layer over the background. A gradient of one colour twice, because
    /// a background-image layer is the only kind that stacks above another one.
    /// </summary>
    private static string DimLayer(Backdrop backdrop)
    {
        var token = backdrop.DimWith == BackdropDimWith.Overlay ? "overlay" : "ink";
        var amount = Number(Math.Clamp(backdrop.Dim, 0, Backdrop.MaxDim));
        var colour = $"color-mix(in srgb, var(--{token}) {amount}%, transparent)";
        return $"linear-gradient({colour}, {colour})";
    }

    private static string Position(BackdropPosition position) => position switch
    {
        BackdropPosition.Top => "center top",
        BackdropPosition.Bottom => "center bottom",
        BackdropPosition.Left => "left center",
        BackdropPosition.Right => "right center",
        _ => "center",
    };

    /// <summary>
    /// How far the layer reaches past the screen's edges. A blur spreads transparent pixels
    /// in from outside the element, which would show as a pale frame; drawing the picture
    /// twice the blur past every edge keeps the edges as solid as the middle.
    /// </summary>
    private static int Bleed(Backdrop backdrop, BackdropImage? image) =>
        backdrop.Kind == BackdropKind.Image && image is not null ? Math.Clamp(backdrop.Blur, 0, Backdrop.MaxBlur) * 2 : 0;

    private static string Px(int value) => value == 0 ? "0" : Number(value) + "px";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
