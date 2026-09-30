using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Makes the monthly report on the day and at the time somebody chose, and — if they asked —
/// sends a short summary of it through their alert channels.
///
/// Built like <see cref="WeeklySummaryJob"/>: it wakes every minute and almost always does a
/// settings read and a little date arithmetic in <see cref="MonthlySchedule"/>, because a
/// month-long interval counted from process start would drift with every restart and never
/// arrive on a container that restarts nightly. Whether this month's is done is kept in app
/// settings, and claimed before the slow part, so a restart mid-report does not make a second
/// one; a failure before the note is written hands the claim back so the next wake tries
/// again.
///
/// The month's reading happens here, on the background runner, once — never in a page.
/// </summary>
public sealed class MonthlyReportJob(
    AppSettingsStore settings,
    MonthlyReports reports,
    AlertService alerts,
    ILogger<MonthlyReportJob> log) : IBackgroundJob
{
    /// <summary>After a failure, how long to leave it before trying again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _making = new(1, 1);
    private DateTimeOffset? _failedAt;

    public string Name => "monthly-report";

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    /// <summary>Yes: the catch-up for an app that was down on the 1st happens on the first wake, and costs nothing otherwise.</summary>
    public bool RunAtStartup => true;

    public Task RunAsync(CancellationToken ct) => TickAsync(DateTimeOffset.Now, TimeZoneInfo.Local, ct);

    /// <summary>
    /// One wake: make the report if due, otherwise nothing. The clock and zone are
    /// parameters so a test can walk it across a month end and a clock change.
    /// </summary>
    /// <returns>The report made, or null.</returns>
    public async Task<MonthlyReportLink?> TickAsync(DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct)
    {
        var bag = await settings.AllAsync(ct);
        var schedule = MonthlySchedule.From(bag);
        if (!schedule.Enabled)
            return null;

        var covered = WeeklySchedule.ParseInstant(bag.Get(MonthlySchedule.CoveredKey));
        if (covered is null)
        {
            // Switched on by something other than the Settings page: arm from now, so it
            // waits for the next scheduled time rather than making one the moment it notices.
            await settings.SaveAsync(MonthlySchedule.CoveredKey, WeeklySchedule.FormatInstant(now), ct);
            return null;
        }

        if (!schedule.Decide(covered, AlertPolicy.From(bag), now, zone).MakeNow)
            return null;
        if (_failedAt is { } failed && now - failed < RetryAfter && now >= failed)
            return null;

        await _making.WaitAsync(ct);
        try
        {
            await settings.SaveAsync(MonthlySchedule.CoveredKey, WeeklySchedule.FormatInstant(now), ct);

            var month = MonthlySchedule.ReportedMonth(now, zone);
            MonthlyReportNote? note;
            MonthlyReportLink link;
            try
            {
                (note, link) = await reports.MakeAsync(month, zone, replace: false, ct);
            }
            catch
            {
                _failedAt = now;
                await settings.SaveAsync(MonthlySchedule.CoveredKey, WeeklySchedule.FormatInstant(covered.Value), CancellationToken.None);
                throw;
            }

            _failedAt = null;
            await settings.SaveAsync(MonthlySchedule.LastMadeKey, WeeklySchedule.FormatInstant(now), ct);
            log.LogInformation("Made the monthly report for {Month}", MonthlySchedule.Name(month));

            if (schedule.Send)
            {
                // A report made earlier by hand is not gathered again just to be summarised.
                note ??= await reports.PreviewAsync(month, zone, ct);
                var (sent, attempted, _) = await SendAsync(note.Summary, schedule.Channels, ct);
                if (attempted > 0)
                    log.LogInformation("Sent the monthly summary through {Sent} of {Attempted} channel(s)", sent, attempted);
            }
            return link;
        }
        finally
        {
            _making.Release();
        }
    }

    /// <summary>
    /// Sends a summary to the chosen channels, or to every channel when none are chosen —
    /// the weekly summary's rule. Quiet hours are already dealt with by the schedule, and
    /// the Settings page's button is somebody asking for it now. Never urgent.
    /// </summary>
    public async Task<(int Sent, int Attempted, IReadOnlyList<string> Errors)> SendAsync(
        WeeklyDigest summary, IReadOnlyList<string> channelIds, CancellationToken ct)
    {
        var channels = await alerts.ChannelsAsync(ct);
        if (channelIds.Count > 0)
        {
            var chosen = channels.Where(pair => channelIds.Contains(pair.Connection.Id)).ToList();
            if (chosen.Count > 0)
                channels = chosen;
            else
                log.LogWarning("The monthly summary's chosen channels no longer exist; sending to all instead");
        }

        var alert = new Alert(AlertLevel.Info, summary.Title, summary.Text) { Markdown = summary.Markdown };
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
                log.LogError(ex, "Could not send the monthly summary through {Channel}", pair.Connection.Name);
                lock (errors)
                    errors.Add($"{pair.Connection.Name}: {ex.GetBaseException().Message}");
                return false;
            }
        }));
        return (results.Count(ok => ok), results.Length, errors);
    }
}
