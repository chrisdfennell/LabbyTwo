using LabbyTwo.Core;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LabbyTwo.GoogleCalendarPlugin;

/// <summary>
/// The OAuth landing strip, for installs that have an https address. Google will only
/// redirect to https or to loopback, so on a plain LAN install this route is never reached
/// and the calendar page falls back to asking for the code by hand — same exchange, one
/// more paste. Registering <c>https://your-host/ext/google-calendar/callback</c> as the
/// redirect URI is what turns that paste back into a single click.
/// </summary>
public sealed class GoogleCalendarEndpoints(
    ConfigStore config, GoogleCalendarProvider google) : IEndpointExtension
{
    public string Key => "google-calendar";

    /// <summary>
    /// The login still applies. Google sends the user's own browser here, and that browser
    /// is already signed in to LabbyTwo — while an endpoint that took an authorisation code
    /// from anyone would be a way to attach somebody else's calendar to this dashboard.
    /// </summary>
    public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/callback", CompleteAsync);

    private async Task<IResult> CompleteAsync(
        string? code, string? state, string? error, CancellationToken ct)
    {
        if (error is { Length: > 0 })
            return Results.Content($"Google declined: {error}. Nothing was changed.", "text/plain");

        if (code is not { Length: > 0 } || state is not { Length: > 0 })
            return Results.Content(
                "That link is missing its code. Start again from the calendar page.", "text/plain");

        var connection = await config.ConnectionAsync(state, ct);
        if (connection is null || connection.Provider != "google-calendar")
            return Results.NotFound();

        try
        {
            var refresh = await google.ExchangeAsync(connection, code, ct);

            // Saved through ConfigStore so it lands encrypted, like every other password
            // field — a refresh token is a standing key to the calendar.
            var settings = connection.Settings.Clone();
            settings["refresh_token"] = refresh;
            await config.SaveConnectionAsync(connection with { Settings = settings }, ct);
        }
        catch (Exception ex)
        {
            return Results.Content(ex.GetBaseException().Message, "text/plain");
        }

        // Back to the calendar page if there is one, otherwise the connections list.
        var tab = (await config.TabsAsync(ct)).FirstOrDefault(t => t.Kind == GoogleCalendarTabKind.KindKey);
        return Results.LocalRedirect(tab is null ? "/settings/connections" : $"/t/{tab.Slug}");
    }
}
