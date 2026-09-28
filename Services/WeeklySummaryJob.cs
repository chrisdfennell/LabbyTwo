using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Sends the weekly summary through the alert channels somebody already has, on the day and
/// at the time they chose.
///
/// A background job that wakes every minute and usually does nothing, rather than one with
/// a week-long interval: the runner counts intervals from process start, so a weekly
/// interval would drift with every restart, and a container that restarts nightly would
/// never send at all. Each wake is one read of the in-memory settings and a little date
/// arithmetic in <see cref="WeeklySchedule"/>.
///
/// Whether this week's has gone out is kept in app settings, not in memory, because a
/// restart within a minute of the send is exactly when a second copy would otherwise go
/// out. The claim is written before sending, not after: a crash mid-send loses one
/// digest, which is the lesser fault next to a restart loop sending one every minute.
/// </summary>
public sealed class WeeklySummaryJob(
    AppSettingsStore settings,
    WeeklySummaryGatherer gatherer,
    AlertService alerts,
    ILogger<WeeklySummaryJob> log) : IBackgroundJob
{
    /// <summary>
    /// After every channel refuses, how long to leave it before trying again. Without it a
    /// broken webhook would be retried every minute until the next week; with it the send
    /// still recovers the same morning once the channel does.
    /// </summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _sending = new(1, 1);
    private DateTimeOffset? _failedAt;

    public string Name => "weekly-summary";

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    /// <summary>
    /// Yes: the first wake is where the catch-up happens for an app that was down at the
    /// scheduled time, and it costs nothing when nothing is due.
    /// </summary>
    public bool RunAtStartup => true;

    public Task RunAsync(CancellationToken ct) => TickAsync(DateTimeOffset.Now, TimeZoneInfo.Local, ct);

    /// <summary>
    /// One wake: send if due, otherwise nothing. The clock and the zone are parameters so a
    /// test can walk it across a week, a clock change and a restart without waiting for any
    /// of them.
    /// </summary>
    /// <returns>True if a summary was sent.</returns>
    public async Task<bool> TickAsync(DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct)
    {
        var bag = await settings.AllAsync(ct);
        var schedule = WeeklySchedule.From(bag);
        if (!schedule.Enabled)
            return false;

        var covered = WeeklySchedule.ParseInstant(bag.Get(WeeklySchedule.CoveredKey));
        if (covered is null)
        {
            // Switched on by something other than the Settings page — an imported
            // configuration, a hand-edited database. Arm from now, so it waits for the next
            // scheduled time rather than sending the moment it notices.
            await settings.SaveAsync(WeeklySchedule.CoveredKey, WeeklySchedule.FormatInstant(now), ct);
            return false;
        }

        var decision = schedule.Decide(covered, AlertPolicy.From(bag), now, zone);
        if (!decision.SendNow)
            return false;

        if (_failedAt is { } failed && now - failed < RetryAfter && now >= failed)
            return false;

        await _sending.WaitAsync(ct);
        try
        {
            // Claimed before anything slow happens. A restart from here on finds this week
            // already dealt with.
            await settings.SaveAsync(WeeklySchedule.CoveredKey, WeeklySchedule.FormatInstant(now), ct);

            // Until something has actually been sent, any failure — including shutting down
            // mid-gather — hands the claim back, so the restart sends it instead.
            WeeklyDigest digest;
            try
            {
                digest = WeeklySummary.Build(await gatherer.GatherAsync(now, zone, ct));
            }
            catch
            {
                await ReleaseAsync(covered.Value, now);
                throw;
            }

            // From here a channel may have delivered it, so the claim stands even if the
            // process is stopped halfway through the sends.
            var (sent, attempted, _) = await SendAsync(digest, schedule.Channels, ct);

            if (attempted > 0 && sent == 0)
            {
                // Nothing got through anywhere, so nobody has this week's summary: give the
                // claim back and try again later, rather than counting a failure as sent.
                await ReleaseAsync(covered.Value, now);
                throw new InvalidOperationException(
                    $"No alert channel accepted the weekly summary; trying again in {RetryAfter.TotalMinutes:0} minutes.");
            }

            _failedAt = null;
            await settings.SaveAsync(WeeklySchedule.LastSentKey, WeeklySchedule.FormatInstant(now), ct);
            await gatherer.RememberAsync(ct);

            if (attempted == 0)
                log.LogInformation("Weekly summary due, but there are no alert channels to send it to");
            else
                log.LogInformation("Sent the weekly summary through {Sent} of {Attempted} channel(s)", sent, attempted);
            return attempted > 0;
        }
        finally
        {
            _sending.Release();
        }
    }

    private async Task ReleaseAsync(DateTimeOffset previous, DateTimeOffset now)
    {
        _failedAt = now;
        await settings.SaveAsync(WeeklySchedule.CoveredKey, WeeklySchedule.FormatInstant(previous), CancellationToken.None);
    }

    /// <summary>
    /// Gathers and builds this week's summary without sending it, for the preview on the
    /// Settings page. On the thread pool: gathering is a few hundred SQLite queries, and
    /// SQLite's async is synchronous underneath, so awaiting it from a page would hold the
    /// page's circuit for as long as they took.
    /// </summary>
    public Task<WeeklyDigest> PreviewAsync(CancellationToken ct = default) =>
        Task.Run(async () =>
            WeeklySummary.Build(await gatherer.GatherAsync(DateTimeOffset.Now, TimeZoneInfo.Local, ct)), ct);

    /// <summary>
    /// Sends a summary to the chosen channels, or to every channel when none are chosen.
    ///
    /// Not <see cref="AlertService.BroadcastAsync"/>: that checks quiet hours itself and can
    /// only narrow to one channel. The schedule has already waited out quiet hours, and the
    /// preview button is somebody asking for a message right now, so both skip that check.
    /// Never marked urgent — a summary is never a reason to break through Do Not Disturb.
    /// </summary>
    public async Task<(int Sent, int Attempted, IReadOnlyList<string> Errors)> SendAsync(
        WeeklyDigest digest, IReadOnlyList<string> channelIds, CancellationToken ct)
    {
        var channels = await alerts.ChannelsAsync(ct);
        if (channelIds.Count > 0)
        {
            var chosen = channels.Where(pair => channelIds.Contains(pair.Connection.Id)).ToList();

            // The same rule as a threshold rule pointing at a deleted channel: better somewhere
            // unexpected than nowhere at all.
            if (chosen.Count > 0)
                channels = chosen;
            else
                log.LogWarning("The weekly summary's chosen channels no longer exist; sending to all instead");
        }

        var alert = new Alert(AlertLevel.Info, digest.Title, digest.Text) { Markdown = digest.Markdown };
        var errors = new List<string>();

        var results = await Task.WhenAll(channels.Select(async pair =>
        {
            try
            {
                await pair.Channel.SendAsync(pair.Connection, alert, ct);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogError(ex, "Could not send the weekly summary through {Channel}", pair.Connection.Name);
                lock (errors)
                    errors.Add($"{pair.Connection.Name}: {ex.GetBaseException().Message}");
                return false;
            }
        }));

        return (results.Count(ok => ok), results.Length, errors);
    }
}
