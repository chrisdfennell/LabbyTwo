using LabbyTwo.Core;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace LabbyTwo.Services.Offsite;

/// <summary>
/// Opens an encrypted off-site backup in the browser: upload the .l2backup with its
/// passphrase, get the zip of database and keyring back. The restore instructions also give
/// a Python script, but somebody restoring a dead NAS onto a fresh install should not need
/// a toolchain to read their own backup.
///
/// An endpoint rather than a component, because a bundle can be hundreds of megabytes and
/// that should neither squeeze through the SignalR circuit nor be buffered: the upload is
/// read as a stream and decrypted straight into the response.
///
/// There is no antiforgery check, deliberately. Validating the token means reading the
/// form, which buffers the whole upload; and there is nothing to forge — the endpoint
/// changes nothing and hands the result back to whoever sent the file.
/// </summary>
public sealed class BackupBundleEndpoints : IEndpointExtension
{
    public string Key => "backup-bundle";

    public const string DecryptPath = "/ext/backup-bundle/decrypt";

    public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/decrypt", DecryptAsync);

    private static async Task DecryptAsync(HttpContext context)
    {
        var ct = context.RequestAborted;

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = null;

        var boundary = MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var type)
            ? HeaderUtilities.RemoveQuotes(type.Boundary).Value
            : null;
        if (string.IsNullOrEmpty(boundary))
        {
            await FailAsync(context, "Send the file from the form on the Settings page.");
            return;
        }

        var reader = new MultipartReader(boundary, context.Request.Body);
        string? passphrase = null;

        // The form lists the passphrase before the file, and browsers send fields in that
        // order — which is what lets the file be decrypted as it arrives.
        while (await reader.ReadNextSectionAsync(ct) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                continue;

            var field = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
            if (field == "passphrase")
            {
                using var text = new StreamReader(section.Body);
                passphrase = await text.ReadToEndAsync(ct);
                continue;
            }

            if (field != "file" || !disposition.IsFileDisposition())
                continue;

            if (string.IsNullOrEmpty(passphrase))
            {
                await FailAsync(context, "Enter the passphrase as well as choosing the file.");
                return;
            }

            BundleReader bundle;
            try
            {
                bundle = await BundleReader.OpenAsync(section.Body, passphrase, ct);
            }
            catch (BundleException ex)
            {
                await FailAsync(context, ex.Message);
                return;
            }

            var uploaded = HeaderUtilities.RemoveQuotes(disposition.FileName).Value ?? "labbytwo-backup";
            var name = Path.GetFileNameWithoutExtension(Path.GetFileName(uploaded)) + ".zip";

            context.Response.ContentType = "application/zip";
            context.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = name,
                FileNameStar = name,
            }.ToString();

            try
            {
                await bundle.CopyToAsync(context.Response.Body, ct);
            }
            catch (BundleException)
            {
                // Headers are gone, so the only honest thing left is to break the download:
                // a zip that stops half-way must not arrive looking like a finished one.
                context.Abort();
            }
            return;
        }

        await FailAsync(context, "No backup file arrived. Choose the .l2backup file and try again.");
    }

    private static async Task FailAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync($"{message}\n\nGo back and try again.", context.RequestAborted);
    }
}
