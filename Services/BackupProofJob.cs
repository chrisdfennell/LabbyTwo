using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>
/// Checks the Backups page's list every five minutes: remembers what the sources proved,
/// records completions and lateness in the change feed, and sends a late backup or a due
/// restore test through the alert channels once. See <see cref="BackupProof"/>.
///
/// Five minutes because nothing here is urgent to the minute — a nightly backup is late by
/// hours — and the readings it judges come from the monitor's sweep anyway. Not at startup:
/// the monitor has not probed anything yet then, so every item proven by a connection would
/// look as though its source had vanished, and one proven only by a remembered date could
/// be called late minutes before the first sweep showed last night's backup.
/// </summary>
public sealed class BackupProofJob(BackupProof proof) : IBackgroundJob
{
    public string Name => "backup-proof";

    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public Task RunAsync(CancellationToken ct) => proof.SweepAsync(DateTimeOffset.Now, ct);
}
