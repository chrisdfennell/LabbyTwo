using LabbyTwo.Core;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
// A plugin is a plain class library, so the host's implicit usings have to be named here.
using Microsoft.Extensions.Logging;

namespace LabbyTwo.DropPlugin;

/// <summary>
/// A shelf both devices can reach. The dashboard is already open on the phone and on the
/// desk, which makes it the obvious place to put a file that has to get from one to the
/// other — no cable, no cloud account, no emailing yourself.
/// </summary>
public sealed class DropTabKind : ITabKind
{
    public const string KindKey = "drop";

    public string Kind => KindKey;
    public string DisplayName => "Drop";
    public string Icon => "📥";
    public string Description =>
        "A shared shelf for files and pasted text — put something here on one device, pick it up on another.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("keep_hours", "Keep things for (hours)", FieldKind.Number, Default: "24",
            Help: "0 keeps them until you delete them. Anything else is tidied up by the cleanup job, " +
                  "which is the point of an expiry: a shelf nobody clears is a disk that fills."),

        new("max_upload_mb", "Largest upload (MB)", FieldKind.Number, Default: "512",
            Help: "Uploads travel through your browser's connection to LabbyTwo, so this is a guard " +
                  "against a mistaken drag rather than a limit of the disk."),

        new("read_only", "Read only", FieldKind.Bool, Default: "false",
            Help: "Shows what is on the shelf but allows nothing new. For a display nobody should be posting to."),
    ];

    public Type Component => typeof(DropTab);
}

/// <summary>
/// Handing the file back. A component can list the shelf; only an endpoint can be the
/// target of a download link, which is the whole reason a phone can pick anything up.
/// </summary>
public sealed class DropEndpoints(Db db) : IEndpointExtension
{
    public const string RouteKey = "drop";

    public string Key => RouteKey;

    public static string DownloadUrl(string id) =>
        $"{ExtensionRoutes.PathFor(RouteKey)}/download?id={Uri.EscapeDataString(id)}";

    public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/download", DownloadAsync);

    private async Task<IResult> DownloadAsync(string id, CancellationToken ct, bool inline = false)
    {
        var store = new DropStore(db);

        if (await store.FindAsync(id, ct) is not { } drop)
            return Results.NotFound();

        if (drop.HasExpired(DateTimeOffset.Now))
            return Results.Content("That drop has expired.", "text/plain");

        if (drop.IsText)
            return Results.Text(drop.Text, "text/plain; charset=utf-8");

        var path = store.PathFor(drop.Id);
        if (!File.Exists(path))
            return Results.Content(
                "The file is gone from disk, though its record is still here. The next cleanup will tidy that up.",
                "text/plain");

        // Results.File streams and handles Range itself, so a video on the shelf seeks.
        return Results.File(path,
            drop.ContentType is { Length: > 0 } type ? type : "application/octet-stream",
            fileDownloadName: inline ? null : drop.Name,
            enableRangeProcessing: true);
    }
}

/// <summary>
/// The cleanup. This is what an <see cref="IBackgroundJob"/> is for: nobody opening a page
/// is what makes yesterday's files expire, and before this extension point existed the
/// only way to run it was to disguise it as something else.
/// </summary>
public sealed class DropCleanupJob(Db db, ILogger<DropCleanupJob> log) : IBackgroundJob
{
    public string Name => "drop-cleanup";

    public TimeSpan Interval => TimeSpan.FromHours(1);

    /// <summary>
    /// Yes at startup: a NAS that was off overnight should not keep expired files around
    /// until an hour after it comes back.
    /// </summary>
    public bool RunAtStartup => true;

    public async Task RunAsync(CancellationToken ct)
    {
        var removed = await new DropStore(db).PurgeAsync(ct);
        if (removed > 0)
            log.LogInformation("Drop cleanup removed {Count} expired item(s)", removed);
    }
}
