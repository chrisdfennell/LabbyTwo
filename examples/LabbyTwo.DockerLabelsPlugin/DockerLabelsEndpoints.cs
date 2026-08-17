using LabbyTwo.Core;
using LabbyTwo.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LabbyTwo.DockerLabelsPlugin;

/// <summary>
/// The file the importer eats, fetched from the Docker socket LabbyTwo already has mounted.
///
/// The split is deliberate and is what the two extension points are each good at. An
/// importer is pure — a file in, a plan out — which is what makes it testable and what lets
/// it read output from a Docker host on the other side of the house. But somebody has to
/// produce the file, and a component cannot hand the browser one. So: open this link, save
/// what it gives you, and drop it on the Import page.
///
/// Logged in like every other page, because the answer contains every container name, image
/// and label on the host — which is a fairly complete description of what you run.
/// </summary>
public sealed class DockerLabelsEndpoints : IEndpointExtension
{
    public const string RouteKey = "docker-labels";

    public string Key => RouteKey;

    public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/containers.json", ContainersAsync);

    private static async Task<IResult> ContainersAsync(
        HttpContext context, CancellationToken ct, string? endpoint = null, int timeout = 10)
    {
        try
        {
            var json = await DockerSocket.GetAsync(
                string.IsNullOrWhiteSpace(endpoint) ? DockerSocket.DefaultEndpoint : endpoint,
                TimeSpan.FromSeconds(Math.Clamp(timeout, 1, 120)),
                // all=1 so a container that is stopped still shows up. Something you turned
                // off for the afternoon should not vanish from the dashboard you are in the
                // middle of building.
                "/containers/json?all=1",
                ct);

            // Offered as a download rather than rendered, because the next thing to happen
            // to it is being uploaded on the Import page, and a file that is already on disk
            // saves a round of "save as".
            context.Response.Headers.ContentDisposition = "attachment; filename=containers.json";
            return Results.Content(json, "application/json");
        }
        catch (Exception ex)
        {
            // The message a person can act on. Nine times in ten this is the socket not
            // being mounted, and saying so beats the raw socket error every time.
            return Results.Content(
                $"Could not read the Docker socket: {ex.GetBaseException().Message}\n\n"
                + "LabbyTwo needs the socket mounted to see containers. In docker-compose.override.yml:\n"
                + "    volumes:\n"
                + "      - /var/run/docker.sock:/var/run/docker.sock:ro\n",
                "text/plain",
                statusCode: 502);
        }
    }
}
