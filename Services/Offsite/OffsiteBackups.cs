using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Storage;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Services.Offsite;

/// <summary>
/// Copies the nightly backup somewhere else. The local copy in <c>data/backups</c> sits on
/// the same disk, in the same volume, on the same NAS as the database it protects — good
/// for "I deleted the wrong tab", no use at all for the NAS dying, being stolen, or being
/// encrypted by something nasty. This is the part that answers those.
///
/// It runs straight after <see cref="BackupJob"/> writes the local copy, and from the
/// "Back up now" button through the same job. Each destination is tried on its own, so a
/// bucket that is down does not stop the USB disk getting its copy.
/// </summary>
public sealed class OffsiteBackups(
    OffsiteSettingsStore store,
    IHttpClientFactory httpFactory,
    AlertService alerts,
    IOptions<LabbyOptions> options,
    IHostEnvironment environment,
    ILogger<OffsiteBackups> log)
{
    /// <summary>One destination's outcome from one run.</summary>
    public sealed record Result(OffsiteDestination Destination, bool Ok, string Message, long Size, string FileName);

    /// <summary>
    /// Waits between attempts. Transient failures — a 503, a dropped connection, a share
    /// that blinked — are common enough at 3am on a home connection that giving up on the
    /// first one would page somebody for nothing. Settable so tests do not sleep.
    /// </summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; set; } = [TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)];

    /// <summary>PBKDF2 rounds for new bundles. Settable so tests do not spend a second per test on it.</summary>
    public int BundleIterations { get; set; } = BackupBundle.DefaultIterations;

    /// <summary>The DataProtection keyring, which Program.cs keeps beside the database.</summary>
    public string KeysDirectory => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(options.Value.DatabasePath, environment.ContentRootPath))!, "keys");

    /// <summary>
    /// Sends <paramref name="backupPath"/> — a finished local backup — to every enabled
    /// destination, then records and, where the state changed, announces the outcome.
    /// Never throws for a destination's failure; the caller reads the results.
    /// </summary>
    public async Task<IReadOnlyList<Result>> CopyAsync(string backupPath, DateOnly date, CancellationToken ct)
    {
        var destinations = (await store.DestinationsAsync(ct)).Where(d => d.Enabled).ToList();
        if (destinations.Count == 0)
            return [];

        var passphrase = await store.PassphraseAsync(ct);
        var stamp = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        string artifact, name;
        var temporary = false;
        if (passphrase.Length > 0)
        {
            // Written next to the local backups rather than in /tmp, which in a container is
            // the image's own writable layer and may be far smaller than the data volume.
            artifact = Path.Combine(Path.GetDirectoryName(backupPath)!, $".offsite-{Guid.NewGuid():n}.tmp");
            name = $"labbytwo-{stamp}{BackupBundle.Extension}";
            temporary = true;
            var keys = KeysDirectory;
            var iterations = BundleIterations;
            await Task.Run(() =>
            {
                using var file = new FileStream(artifact, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024);
                BackupBundle.Write(file, backupPath, keys, passphrase, iterations);
            }, ct);
        }
        else
        {
            artifact = backupPath;
            name = $"labbytwo-{stamp}.db";
        }

        var results = new List<Result>();
        try
        {
            var size = new FileInfo(artifact).Length;
            foreach (var destination in destinations)
                results.Add(await SendAsync(destination, artifact, name, size, date, ct));
        }
        finally
        {
            if (temporary)
                TryDelete(artifact);
        }

        await RecordAsync(results, DateTimeOffset.Now, ct);
        return results;
    }

    /// <summary>
    /// Proves a destination works without waiting for tonight: writes a small file, lists,
    /// and deletes it again. Returns what it found; throws with advice if it cannot write.
    /// </summary>
    public async Task<string> TestAsync(OffsiteDestination destination, CancellationToken ct)
    {
        if (destination.Problem() is { } problem)
            throw new InvalidOperationException(problem);

        const string probe = "labbytwo-write-test.txt";
        var bytes = System.Text.Encoding.UTF8.GetBytes($"Written by LabbyTwo at {DateTimeOffset.Now:O} to check it can. Safe to delete.\n");

        try
        {
            if (!destination.IsS3)
            {
                RequireFolder(destination.Path);
                var path = Path.Combine(destination.Path, probe);
                await File.WriteAllBytesAsync(path, bytes, ct);
                File.Delete(path);
                var ours = Directory.EnumerateFiles(destination.Path).Count(f => Retention.DateOf(Path.GetFileName(f)) is not null);
                return $"LabbyTwo can write to {destination.Path}. {ours} backup(s) there now.";
            }

            var s3 = Client(destination);
            var key = destination.NormalisedPrefix + probe;
            await s3.PutBytesAsync(key, bytes, ct);
            var listed = await s3.ListAsync(destination.NormalisedPrefix, ct);
            var count = listed.Count(o => Retention.DateOf(o.Key[destination.NormalisedPrefix.Length..]) is not null);
            try
            {
                await s3.DeleteAsync(key, ct);
            }
            catch (S3Client.S3Exception ex) when (ex.Status == System.Net.HttpStatusCode.Forbidden)
            {
                return $"It can write and list, but not delete — so old copies will not be tidied up. That is a fine " +
                       $"setup against ransomware if you set Keep to 0 and expire them with a lifecycle rule instead. " +
                       $"Remove {key} by hand. {count} backup(s) there now.";
            }
            return $"Connected: it can write, list and delete in {destination.Bucket}. {count} backup(s) there now.";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(Describe(ex, destination), ex);
        }
    }

    private async Task<Result> SendAsync(
        OffsiteDestination destination, string artifact, string name, long size, DateOnly date, CancellationToken ct)
    {
        if (destination.Problem() is { } problem)
            return new Result(destination, false, problem, 0, name);

        try
        {
            await WithRetriesAsync(destination, token => destination.IsS3
                ? Client(destination).PutFileAsync(destination.NormalisedPrefix + name, artifact, token)
                : CopyToFolderAsync(destination.Path, artifact, name, token), ct);

            log.LogInformation("Copied {Name} ({Bytes} bytes) to {Destination}", name, size, destination.Name);

            // Tidying up is separate from the copy on purpose: a key that may write but not
            // delete is a sensible, ransomware-resistant setup, and must not make every
            // night's successful copy look like a failure.
            var note = "";
            try
            {
                var removed = await PruneAsync(destination, date, ct);
                if (removed > 0)
                    note = $" Removed {removed} older cop{(removed == 1 ? "y" : "ies")}.";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Could not remove old backups from {Destination}", destination.Name);
                note = $" Old copies were not tidied up: {Describe(ex, destination)}";
            }

            return new Result(destination, true, $"Copied {name} ({Bytes(size)}).{note}", size, name);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Off-site backup to {Destination} failed", destination.Name);
            return new Result(destination, false, Describe(ex, destination), 0, name);
        }
    }

    private async Task WithRetriesAsync(OffsiteDestination destination, Func<CancellationToken, Task> attempt, CancellationToken ct)
    {
        for (var tries = 0; ; tries++)
        {
            try
            {
                await attempt(ct);
                return;
            }
            catch (Exception ex) when (tries < RetryDelays.Count && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                log.LogInformation("Off-site backup to {Destination} failed ({Reason}); trying again in {Delay}",
                    destination.Name, ex.GetBaseException().Message, RetryDelays[tries]);
                await Task.Delay(RetryDelays[tries], ct);
            }
        }
    }

    /// <summary>Worth another go: the network or the far end hiccupped, rather than the request being wrong.</summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        S3Client.S3Exception s3 => s3.Transient,
        HttpRequestException => true,
        TimeoutException => true,
        DirectoryNotFoundException => false,
        IOException => true,
        _ => false,
    };

    private static async Task CopyToFolderAsync(string folder, string artifact, string name, CancellationToken ct)
    {
        RequireFolder(folder);

        // Under a name nothing will mistake for a backup until it is whole, so a copy cut
        // off half-way — the share dropping, the container stopping — never sits there
        // looking like last night's good one.
        var partial = Path.Combine(folder, $".{name}.partial");
        try
        {
            await using (var source = new FileStream(artifact, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous))
                await source.CopyToAsync(target, ct);

            File.Move(partial, Path.Combine(folder, name), overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>
    /// A missing folder is refused rather than created. If a share is not mounted, the
    /// path is just an empty directory inside the container, and "creating" it would put
    /// the off-site backup on the very disk it is meant to be off.
    /// </summary>
    private static void RequireFolder(string folder)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException(
                $"The folder {folder} does not exist. If it is a share or a USB disk, it is not mapped into the container " +
                "— add it under volumes: in docker-compose.yml.");
    }

    private async Task<int> PruneAsync(OffsiteDestination destination, DateOnly today, CancellationToken ct)
    {
        if (destination.Keep <= 0)
            return 0;

        if (!destination.IsS3)
        {
            var names = Directory.EnumerateFiles(destination.Path).Select(f => Path.GetFileName(f)!);
            var doomed = Retention.ToDelete(names, destination.Keep, destination.KeepUnit, today);
            foreach (var name in doomed)
                File.Delete(Path.Combine(destination.Path, name));
            return doomed.Count;
        }

        var s3 = Client(destination);
        var prefix = destination.NormalisedPrefix;

        // Directly under the prefix only: a nested "folder" is somebody else's.
        var listed = (await s3.ListAsync(prefix, ct))
            .Select(o => o.Key[prefix.Length..])
            .Where(rest => !rest.Contains('/'));

        var delete = Retention.ToDelete(listed, destination.Keep, destination.KeepUnit, today);
        foreach (var name in delete)
            await WithRetriesAsync(destination, token => s3.DeleteAsync(prefix + name, token), ct);
        return delete.Count;
    }

    private S3Client Client(OffsiteDestination destination) =>
        new(httpFactory.CreateClient(ProviderHttp.TransferClientName), destination);

    /// <summary>
    /// Records every result, and alerts only where a destination's state changed: the first
    /// failure after a success, and the first success after an announced failure. A share
    /// that stays unmounted for a week is one message, not seven.
    /// </summary>
    private async Task RecordAsync(IReadOnlyList<Result> results, DateTimeOffset now, CancellationToken ct)
    {
        var previous = await store.StatusesAsync(ct);
        var known = (await store.DestinationsAsync(ct)).Select(d => d.Id).ToHashSet();

        var next = previous.Where(p => known.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);

        foreach (var result in results)
        {
            var (status, alert) = Next(previous.GetValueOrDefault(result.Destination.Id), result.Destination.Name, result, now);

            if (alert is not null)
            {
                var sent = await alerts.BroadcastAsync(alert, ct);
                if (alert.Level == AlertLevel.Down && sent > 0)
                    status = status with { FailureAnnounced = true };
            }

            next[result.Destination.Id] = status;
        }

        await store.SaveStatusesAsync(next, ct);
    }

    /// <summary>
    /// The new status, and the alert the change deserves if any. Pure, so "once, not
    /// nightly" is tested without a clock or a channel.
    /// </summary>
    public static (DestinationStatus Status, Alert? Alert) Next(
        DestinationStatus? previous, string name, Result result, DateTimeOffset now)
    {
        var before = previous ?? new DestinationStatus { Ok = true };

        if (result.Ok)
        {
            var status = before with
            {
                LastRunAt = now, Ok = true, Message = result.Message,
                LastSuccessAt = now, LastSize = result.Size, LastName = result.FileName,
                FailureAnnounced = false,
            };
            var recovered = before.FailureAnnounced
                ? new Alert(AlertLevel.Up, $"Off-site backup to {name} is working again", result.Message)
                : null;
            return (status, recovered);
        }

        var failed = before with { LastRunAt = now, Ok = false, Message = result.Message, LastFailureAt = now };
        var alert = before.FailureAnnounced
            ? null
            : new Alert(AlertLevel.Down, $"Off-site backup to {name} failed",
                result.Message + (before.LastSuccessAt is { } good
                    ? $" The last good copy there is from {good.ToLocalTime():ddd d MMM}."
                    : " There is no good copy there yet."));
        return (failed, alert);
    }

    /// <summary>What went wrong, as something to go and fix.</summary>
    public static string Describe(Exception ex, OffsiteDestination destination)
    {
        var host = destination.IsS3
            ? (Uri.TryCreate(destination.Endpoint, UriKind.Absolute, out var uri) ? uri.Host : "s3.amazonaws.com")
            : null;

        return ex switch
        {
            S3Client.S3Exception or BundleException or DirectoryNotFoundException or TimeoutException => ex.Message,
            UnauthorizedAccessException =>
                $"LabbyTwo is not allowed to write to {destination.Path}. Check the share's or disk's permissions for the user the container runs as.",
            HttpRequestException => ProbeError.Describe(ex, host),
            IOException io when destination.IsS3 => ProbeError.Describe(io, host),
            _ => ex.GetBaseException().Message,
        };
    }

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left behind is untidy, not harmful: it is a temporary name nothing reads.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
