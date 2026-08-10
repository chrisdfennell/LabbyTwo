using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Storage;
// A plugin is a plain class library, so the web namespaces the host gets implicitly have
// to be asked for by name here.
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LabbyTwo.QnapFilesPlugin;

/// <summary>
/// The download route, and the reason this plugin needs <see cref="IEndpointExtension"/>
/// at all. A component can list a folder perfectly well; it cannot hand the browser three
/// gigabytes of video, and pushing bytes through the Blazor circuit loses Range support,
/// which loses seeking and resuming with it.
///
/// Everything here is mapped under <c>/ext/qnap-files</c> and inherits the app's login,
/// so the endpoint checks that the id names a QNAP connection and then trusts the QNAP
/// account itself to be the boundary — a listing can only ever show what that account can
/// already see.
/// </summary>
public sealed class QnapFilesEndpoints(
    IHttpClientFactory httpFactory, QnapProvider qnap, ConfigStore config) : IEndpointExtension
{
    public const string RouteKey = "qnap-files";

    public string Key => RouteKey;

    /// <summary>
    /// The link the tab renders. Built here rather than in the component so the route and
    /// its callers cannot drift apart.
    /// </summary>
    public static string DownloadUrl(string connectionId, string folder, string name, bool inline = false) =>
        $"{ExtensionRoutes.PathFor(RouteKey)}/download" +
        $"?connection={Uri.EscapeDataString(connectionId)}" +
        $"&path={Uri.EscapeDataString(folder)}" +
        $"&name={Uri.EscapeDataString(name)}" +
        (inline ? "&inline=true" : "");

    public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/download", DownloadAsync);

    private async Task<IResult> DownloadAsync(
        HttpContext context, string connection, string path, string name,
        CancellationToken ct, bool inline = false)
    {
        var nas = await config.ConnectionAsync(connection, ct);
        if (nas is null || !string.Equals(nas.Provider, "qnap", StringComparison.OrdinalIgnoreCase))
            return Results.NotFound();

        var station = new QnapFileStation(httpFactory, qnap);

        HttpResponseMessage upstream;
        try
        {
            upstream = await station.OpenDownloadAsync(nas, path, name, context.Request.Headers.Range, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing has been written yet, so this can still be a readable failure rather
            // than a truncated file the user only notices when it will not open.
            return Results.Problem($"{nas.Name} could not send that file: {ex.GetBaseException().Message}",
                statusCode: StatusCodes.Status502BadGateway);
        }

        using (upstream)
        {
            // 206 and its Content-Range are passed through untouched: that pair is what a
            // <video> element uses to seek and what a download manager uses to resume.
            context.Response.StatusCode = (int)upstream.StatusCode;
            context.Response.Headers.AcceptRanges = "bytes";
            if (upstream.Content.Headers.ContentLength is { } length)
                context.Response.ContentLength = length;
            if (upstream.Content.Headers.ContentRange is { } range)
                context.Response.Headers.ContentRange = range.ToString();

            context.Response.ContentType = ContentTypeFor(name);

            // filename* rather than filename: accented characters and commas are ordinary
            // in a folder of holiday photos, and a bare filename= mangles both.
            context.Response.Headers.ContentDisposition =
                $"{(inline ? "inline" : "attachment")}; filename*=UTF-8''{Uri.EscapeDataString(name)}";

            await using var stream = await upstream.Content.ReadAsStreamAsync(ct);
            await stream.CopyToAsync(context.Response.Body, ct);
        }

        return Results.Empty;
    }

    /// <summary>
    /// Enough types for "open it in the browser" to work on the things people actually
    /// preview. Anything else downloads, which is the harmless answer.
    /// </summary>
    private static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf",
        ".txt" or ".log" or ".md" or ".yml" or ".yaml" or ".conf" => "text/plain; charset=utf-8",
        ".json" => "application/json",
        ".mp4" or ".m4v" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        _ => "application/octet-stream",
    };
}
