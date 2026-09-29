using LabbyTwo.Components.Family;
using LabbyTwo.Core;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LabbyTwo.Services;

/// <summary>
/// The family status page's routes: the page itself and the report form, at
/// <c>/family/{token}</c> for the share link and at <c>/family</c> for the home network when
/// the owner has allowed that.
///
/// Plain endpoints rendering one static component rather than a routable Blazor page. That
/// is what keeps an anonymous visitor entirely outside the app: the fallback login policy
/// never has to be opened for a page, no circuit is started, and every request passes the
/// link check and the rate limit here before anything is read. Each URL answers exactly the
/// same 404 whether the feature is off, the link is wrong, or the request is not from the
/// home network, so a stranger learns nothing by trying.
/// </summary>
public static class FamilyEndpoints
{
    public const string Prefix = "/family";

    public static void MapFamilyStatus(this WebApplication app)
    {
        var group = app.MapGroup(Prefix).AllowAnonymous();

        group.MapGet("/{token}", (HttpContext context, string token, FamilyStatus family, FamilyThrottle throttle,
                AppSettingsStore settings, CancellationToken ct, int sent = 0) =>
            StatusAsync(context, token, family, throttle, settings, sent == 1, ct));
        group.MapGet("/{token}/report", (HttpContext context, string token, FamilyStatus family, FamilyThrottle throttle,
                AppSettingsStore settings, IAntiforgery antiforgery, CancellationToken ct, string? item = null) =>
            FormAsync(context, token, family, throttle, settings, antiforgery, item, ct));
        group.MapPost("/{token}/report", (HttpContext context, string token, FamilyStatus family, FamilyThrottle throttle,
                AppSettingsStore settings, IAntiforgery antiforgery, CancellationToken ct) =>
            SubmitAsync(context, token, family, throttle, settings, antiforgery, ct));

        // The home-network address. A literal segment outranks {token}, so /family/report is
        // always this form and never a token called "report" — which a random one never is.
        group.MapGet("", (HttpContext context, FamilyStatus family, FamilyThrottle throttle,
                AppSettingsStore settings, CancellationToken ct, int sent = 0) =>
            StatusAsync(context, null, family, throttle, settings, sent == 1, ct));
        group.MapGet("/report", (HttpContext context, FamilyStatus family, FamilyThrottle throttle,
                AppSettingsStore settings, IAntiforgery antiforgery, CancellationToken ct, string? item = null) =>
            FormAsync(context, null, family, throttle, settings, antiforgery, item, ct));
        group.MapPost("/report", (HttpContext context, FamilyStatus family, FamilyThrottle throttle,
                AppSettingsStore settings, IAntiforgery antiforgery, CancellationToken ct) =>
            SubmitAsync(context, null, family, throttle, settings, antiforgery, ct));
    }

    /// <summary>
    /// Whether this request may see the page, and the path it lives at if so. A token must
    /// match; no token means the home-network address, which must be switched on and must
    /// come from the home network.
    /// </summary>
    public static string? Allowed(FamilyStatusSettings settings, string? token, HttpContext context)
    {
        if (!settings.Enabled)
            return null;
        if (token is not null)
            return settings.Matches(token) ? $"{Prefix}/{settings.Token}" : null;
        return settings.LanAccess && FamilyStatus.IsLocal(context) ? Prefix : null;
    }

    private static async Task<IResult> StatusAsync(HttpContext context, string? token, FamilyStatus family,
        FamilyThrottle throttle, AppSettingsStore settings, bool sent, CancellationToken ct)
    {
        Harden(context);
        if (throttle.Page(context.Connection.RemoteIpAddress) is { } wait)
            return Busy(context, wait, null);

        var config = await family.SettingsAsync(ct);
        if (Allowed(config, token, context) is not { } path)
            return NotFound();

        var view = await family.ViewAsync(config, ct);
        return Page(new()
        {
            [nameof(FamilyPage.Screen)] = FamilyPage.Screens.Status,
            [nameof(FamilyPage.View)] = view,
            [nameof(FamilyPage.Look)] = await LookAsync(settings, ct),
            [nameof(FamilyPage.Base)] = path,
            [nameof(FamilyPage.Sent)] = sent,
        });
    }

    private static async Task<IResult> FormAsync(HttpContext context, string? token, FamilyStatus family,
        FamilyThrottle throttle, AppSettingsStore settings, IAntiforgery antiforgery, string? item, CancellationToken ct)
    {
        Harden(context);
        if (throttle.Page(context.Connection.RemoteIpAddress) is { } wait)
            return Busy(context, wait, null);

        var config = await family.SettingsAsync(ct);
        if (Allowed(config, token, context) is not { } path)
            return NotFound();

        return await FormPageAsync(context, family, settings, antiforgery, config, path, item, null, null, null, 200, ct);
    }

    private static async Task<IResult> SubmitAsync(HttpContext context, string? token, FamilyStatus family,
        FamilyThrottle throttle, AppSettingsStore settings, IAntiforgery antiforgery, CancellationToken ct)
    {
        Harden(context);
        if (throttle.Page(context.Connection.RemoteIpAddress) is { } wait)
            return Busy(context, wait, null);

        var config = await family.SettingsAsync(ct);
        if (Allowed(config, token, context) is not { } path)
            return NotFound();

        // The token keeps a stranger out, but on the home network there is no token, and a
        // web page anybody in the house opens could otherwise post reports on their behalf.
        if (!context.Request.HasFormContentType || !await antiforgery.IsRequestValidAsync(context))
            return Results.BadRequest();

        var form = await context.Request.ReadFormAsync(ct);
        var what = form["what"].ToString();
        var message = form["message"].ToString();
        var reporter = form["name"].ToString();

        // Refilled into the form as typed, trimmed to the limits — never shown anywhere else.
        message = message.Length > FamilyText.MaxMessage * 2 ? message[..(FamilyText.MaxMessage * 2)] : message;
        reporter = reporter.Length > FamilyText.MaxReporter * 2 ? reporter[..(FamilyText.MaxReporter * 2)] : reporter;

        if (what.Length == 0)
            return await FormPageAsync(context, family, settings, antiforgery, config, path, null, message, reporter,
                "Pick what isn't working — or “Something else”.", 400, ct);

        if (what != FamilyStatus.SomethingElse && config.Items.All(i => i.Id != what))
            return await FormPageAsync(context, family, settings, antiforgery, config, path, null, message, reporter,
                "That one isn't on the page any more. Pick again?", 400, ct);

        // Counted only once the form is sound, so a mis-tap costs nobody a report.
        if (throttle.Report(context.Connection.RemoteIpAddress) is { } reportWait)
            return Busy(context, reportWait, path);

        await family.ReportAsync(config, what, message, reporter, ct);

        // Post, then redirect, so a refresh of the thank-you does not send it again.
        return Results.Redirect($"{path}?sent=1", permanent: false, preserveMethod: false);
    }

    private static async Task<IResult> FormPageAsync(HttpContext context, FamilyStatus family, AppSettingsStore settings,
        IAntiforgery antiforgery, FamilyStatusSettings config, string path, string? chosen, string? message,
        string? reporter, string? error, int status, CancellationToken ct)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        var view = await family.ViewAsync(config, ct);
        context.Response.StatusCode = status;
        return Page(new()
        {
            [nameof(FamilyPage.Screen)] = FamilyPage.Screens.Report,
            [nameof(FamilyPage.View)] = view,
            [nameof(FamilyPage.Look)] = await LookAsync(settings, ct),
            [nameof(FamilyPage.Base)] = path,
            [nameof(FamilyPage.AntiforgeryField)] = tokens.FormFieldName,
            [nameof(FamilyPage.AntiforgeryValue)] = tokens.RequestToken ?? "",
            [nameof(FamilyPage.Chosen)] = chosen,
            [nameof(FamilyPage.Message)] = message,
            [nameof(FamilyPage.Reporter)] = reporter,
            [nameof(FamilyPage.Error)] = error,
        }, status);
    }

    private static async Task<Appearance> LookAsync(AppSettingsStore settings, CancellationToken ct) =>
        Appearance.From(await settings.AllAsync(ct));

    private static RazorComponentResult<FamilyPage> Page(Dictionary<string, object?> parameters, int status = 200) =>
        new(parameters) { StatusCode = status };

    /// <summary>
    /// The same page for every refusal: feature off, wrong link, not on the home network.
    /// Nothing in it says which, and nothing in it depends on the settings.
    /// </summary>
    private static RazorComponentResult<FamilyPage> NotFound() =>
        Page(new() { [nameof(FamilyPage.Screen)] = FamilyPage.Screens.NotFound }, StatusCodes.Status404NotFound);

    private static RazorComponentResult<FamilyPage> Busy(HttpContext context, TimeSpan wait, string? path)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Page(new()
        {
            [nameof(FamilyPage.Screen)] = FamilyPage.Screens.Busy,
            [nameof(FamilyPage.RetryAfter)] = TimeSpan.FromSeconds(seconds),
            [nameof(FamilyPage.Base)] = path ?? Prefix,
            // Only a link back when the visitor has already shown they may see the page.
            [nameof(FamilyPage.View)] = path is null ? null : new FamilyView("", [], false, null, DateTimeOffset.Now),
        }, StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    /// Headers for a page reached by a secret in its URL: not cached anywhere, not framed,
    /// not indexed, and never sending that URL on as a referrer. The content policy allows
    /// the two stylesheets, the icon and posting back to itself, and nothing else — there
    /// is no script on the page, so none is allowed.
    /// </summary>
    private static void Harden(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Robots-Tag"] = "noindex, nofollow";
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers.ContentSecurityPolicy =
            "default-src 'none'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; " +
            "form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
    }
}
