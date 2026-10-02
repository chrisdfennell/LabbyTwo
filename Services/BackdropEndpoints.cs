using LabbyTwo.Core;
using LabbyTwo.Storage;
using Microsoft.Net.Http.Headers;

namespace LabbyTwo.Services;

/// <summary>
/// Serves the uploaded background picture.
///
/// An endpoint rather than a static-file folder, because only one name is ever right — the
/// current picture's — and anything else, an old hash or a guess, is a 404 without the
/// file system being asked. The name is the content's hash, so the response can be cached
/// for a year as immutable: a browser asks once, and a new picture is a new address. The
/// ETag is the same hash, for the browser that revalidates anyway.
///
/// The content type is the one the server detected when the file was stored, and nosniff
/// stops a browser second-guessing it. It sits behind the login like every other page —
/// the only screens that draw it are ones that need one.
/// </summary>
public static class BackdropEndpoints
{
    public static IEndpointConventionBuilder MapBackdrop(this IEndpointRouteBuilder app) =>
        app.MapGet(BackdropImage.Route + "/{file}", (string file, BackdropImageStore store, HttpContext context) =>
        {
            if (store.Current() is not { } image || !string.Equals(file, image.Hash + image.Extension, StringComparison.Ordinal)
                || store.PathOf(image) is not { } path || !File.Exists(path))
                return Results.NotFound();

            var headers = context.Response.Headers;
            headers.CacheControl = "private, max-age=31536000, immutable";
            headers.XContentTypeOptions = "nosniff";
            // Opened on its own it is a picture and nothing more.
            headers.ContentSecurityPolicy = "default-src 'none'; sandbox";

            return Results.File(path, image.ContentType,
                lastModified: File.GetLastWriteTimeUtc(path),
                entityTag: new EntityTagHeaderValue($"\"{image.Hash}\""));
        });
}
