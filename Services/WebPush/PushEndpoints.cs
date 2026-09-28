using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services.WebPush;

/// <summary>
/// The one push route that cannot go through a Blazor page: a service worker told by its
/// push service that the subscription has moved (the <c>pushsubscriptionchange</c> event),
/// reporting the new one while no page is open. Everything else — subscribing, naming,
/// removing — happens on Alerts → Browser push, over the page's own circuit.
///
/// Behind the login like every extension route: the worker's fetch carries the same cookie
/// the page does.
/// </summary>
public sealed class PushEndpoints : IEndpointExtension
{
    public string Key => "push";

    public sealed record Keys(string P256dh, string Auth);

    public sealed record Renewal(string OldEndpoint, string Endpoint, Keys Keys);

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/renew", async (Renewal renewal, PushSubscriptionStore store, CancellationToken ct) =>
        {
            if (renewal is not { OldEndpoint.Length: > 0, Endpoint.Length: > 0, Keys.P256dh.Length: > 0, Keys.Auth.Length: > 0 }
                || !renewal.Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest();
            }

            return await store.ReplaceEndpointAsync(renewal.OldEndpoint, renewal.Endpoint,
                    renewal.Keys.P256dh, renewal.Keys.Auth, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });
    }
}
