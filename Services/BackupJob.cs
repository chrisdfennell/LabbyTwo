using LabbyTwo.Core;
using LabbyTwo.Services.Offsite;
using LabbyTwo.Storage;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Services;

/// <summary>
/// A dated copy of the database, nightly, kept for a fortnight.
///
/// <c>/api/backup</c> has always existed, but somebody has to remember to click it — which
/// means the answer to "I deleted the wrong tab" or "the disk did something" has been a
/// shrug. Everything anybody builds here lives in one SQLite file, so a copy of it is the
/// whole dashboard: connections, credentials, tabs, history.
///
/// It uses SQLite's own backup API rather than copying the file, so a copy taken while the
/// health monitor is mid-write is still a valid database.
///
/// That copy lives on the same disk as the database, so each run then hands it to
/// <see cref="OffsiteBackups"/> to be copied somewhere that is not this machine.
/// </summary>
public sealed class BackupJob(
    Db db, AppSettingsStore settings, OffsiteBackups offsite, IOptions<LabbyOptions> options,
    IHostEnvironment environment, ILogger<BackupJob> log) : IBackgroundJob
{
    public const string EnabledKey = "backup_enabled";
    public const string KeepKey = "backup_keep";
    public const string FolderKey = "backup_folder";

    public string Name => "database-backup";

    /// <summary>
    /// Daily. Not hourly: this is insurance against a mistake or a disk, and neither is
    /// helped by fifty copies — while a fortnight of them is enough to notice something
    /// went wrong last Tuesday and go back past it.
    /// </summary>
    public TimeSpan Interval => TimeSpan.FromHours(24);

    /// <summary>
    /// Yes, and deliberately: a NAS that is switched off overnight would otherwise never
    /// reach the daily mark, and would be backed up exactly never.
    /// </summary>
    public bool RunAtStartup => true;

    // The nightly run and the "Back up now" button share one of these, so a press at the
    // moment the timer fires waits for it rather than writing the same file twice at once.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RunAsync(CancellationToken ct)
    {
        var stored = await settings.AllAsync(ct);

        if (!stored.GetBool(EnabledKey, true))
            return;

        var results = await RunChainAsync(ct);

        // Thrown after every destination has had its turn, so the runner records the job as
        // failed — which is what puts it on the health page — without one bad destination
        // stopping the rest.
        if (results.Where(r => !r.Ok).ToList() is { Count: > 0 } failed)
            throw new InvalidOperationException(
                "The local copy was written, but the off-site copy failed: " +
                string.Join(" ", failed.Select(r => $"{r.Destination.Name}: {r.Message}")));
    }

    /// <summary>
    /// The "Back up now" button: the whole chain, local copy then every destination, even
    /// with the nightly backup switched off — pressing it is asking for one.
    /// </summary>
    public Task<IReadOnlyList<OffsiteBackups.Result>> RunNowAsync(CancellationToken ct) => RunChainAsync(ct);

    private async Task<IReadOnlyList<OffsiteBackups.Result>> RunChainAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var stored = await settings.AllAsync(ct);
            var folder = Folder(stored);
            Directory.CreateDirectory(folder);

            var today = DateOnly.FromDateTime(DateTime.Now);
            var path = Path.Combine(folder, $"labbytwo-{today:yyyy-MM-dd}.db");

            // One a day: re-running on the same date replaces that day's copy rather than
            // making a second one, so a NAS rebooted six times still has fourteen days.
            await db.BackupToAsync(path, ct);
            log.LogInformation("Wrote a backup to {Path}", path);

            Prune(folder, Math.Clamp(stored.GetInt(KeepKey, 14), 1, 365));

            return await offsite.CopyAsync(path, today, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Inside the data volume by default, because that is the one thing every install
    /// already keeps — a folder elsewhere is a bind mount somebody has to arrange.
    /// </summary>
    private string Folder(SettingsBag stored)
    {
        if (stored.Get(FolderKey) is { Length: > 0 } configured)
            return configured;

        var data = Path.GetDirectoryName(Path.GetFullPath(options.Value.DatabasePath, environment.ContentRootPath))!;
        return Path.Combine(data, "backups");
    }

    private void Prune(string folder, int keep)
    {
        var copies = new DirectoryInfo(folder)
            .EnumerateFiles("labbytwo-*.db")
            .OrderByDescending(file => file.Name)
            .Skip(keep)
            .ToList();

        foreach (var old in copies)
        {
            try
            {
                old.Delete();
                log.LogDebug("Removed old backup {Name}", old.Name);
            }
            catch (IOException ex)
            {
                // Never let tidying up be the thing that fails a backup that already worked.
                log.LogWarning(ex, "Could not remove the old backup {Name}", old.Name);
            }
        }
    }
}
