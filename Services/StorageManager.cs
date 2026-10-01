using System.Globalization;
using System.Text;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>Something the storage page started and is waiting on: a tidy-up or a compaction.</summary>
/// <param name="Stage">What it is doing now, in words.</param>
/// <param name="Outcome">What it did, once finished.</param>
public sealed record StorageTask(bool Running, DateTimeOffset? StartedAt, string Stage, DateTimeOffset? FinishedAt, string? Outcome, string? Error)
{
    public static readonly StorageTask Idle = new(false, null, "", null, null, null);
}

/// <summary>
/// What the storage page shows and starts, kept between visits: the latest survey of what
/// takes the space, the database's size hour by hour (for "growth per day", and for
/// LabbyTwo noticing it growing faster than usual), and the tidy-up or compaction running
/// in the background.
///
/// <para>The survey is the one thing here that reads history, and it is never done while a
/// page draws. <see cref="StorageSurveyJob"/> runs it twice a day, a series at a time with a
/// pause between, and the page shows the last result from memory — or from the settings
/// table after a restart, where it is kept as one row. "Survey now" runs it on request.</para>
///
/// <para>The size history is one point an hour from the database header (page count less
/// free pages: the data, not the file, which never shrinks by itself), kept for a fortnight
/// in one settings row so a restart does not forget what "usual" is.</para>
/// </summary>
public sealed class StorageManager(HistoryStore history, AppSettingsStore settings, ILogger<StorageManager> log)
{
    public const string SurveyKey = "storage_survey";
    public const string SizesKey = "storage_sizes";

    /// <summary>How old the survey may get before the hourly job runs it again.</summary>
    public static readonly TimeSpan SurveyEvery = TimeSpan.FromHours(12);

    /// <summary>
    /// Between one series and the next. Seventy series is then under a second of pauses in
    /// all, and a page or the monitor wanting the disk never waits behind more than one
    /// series' handful of seeks.
    /// </summary>
    public static readonly TimeSpan SurveyPause = TimeSpan.FromMilliseconds(10);

    private static readonly TimeSpan SizesKept = TimeSpan.FromDays(15);

    private readonly SemaphoreSlim _load = new(1, 1);
    private readonly Lock _gate = new();
    private bool _loaded;
    private StorageSurvey? _survey;
    private List<(long At, long Used)> _sizes = [];
    private Task<StorageSurvey>? _surveying;
    private StorageTask _tidy = StorageTask.Idle;
    private StorageTask _compaction = StorageTask.Idle;

    /// <summary>Raised when anything here changes, so an open storage page can redraw.</summary>
    public event Action? Changed;

    public StorageSurvey? Survey => _survey;

    public bool Surveying => _surveying is { IsCompleted: false };

    public StorageTask Tidy => _tidy;

    public StorageTask Compaction => _compaction;

    /// <summary>Reads what was kept across the last restart. Once; later calls return at once.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_loaded)
            return;
        await _load.WaitAsync(ct);
        try
        {
            if (_loaded)
                return;
            var bag = await settings.AllAsync(ct);
            try
            {
                if (bag.Get(SurveyKey) is { Length: > 0 } json && JsonSerializer.Deserialize<StorageSurvey>(json) is { } survey)
                    _survey ??= survey;
            }
            catch (JsonException ex)
            {
                log.LogDebug(ex, "The stored storage survey could not be read; the next one replaces it");
            }
            lock (_gate)
                _sizes = ParseSizes(bag.Get(SizesKey));
            _loaded = true;
        }
        finally
        {
            _load.Release();
        }
    }

    // ---------- The survey ----------

    /// <summary>
    /// Runs the survey, or joins the one already running. Kept in memory and in one settings
    /// row; the page is told when it lands. <paramref name="ct"/> stops the waiting, not the
    /// survey: a page closed half way should not throw away work a second visitor is about
    /// to ask for.
    /// </summary>
    public Task<StorageSurvey> SurveyNowAsync(CancellationToken ct = default)
    {
        Task<StorageSurvey> task;
        lock (_gate)
        {
            if (_surveying is { IsCompleted: false } running)
                return running.WaitAsync(ct);
            task = _surveying = Task.Run(() => RunSurveyAsync(CancellationToken.None));
        }
        Changed?.Invoke();
        return task.WaitAsync(ct);
    }

    private async Task<StorageSurvey> RunSurveyAsync(CancellationToken ct)
    {
        try
        {
            await LoadAsync(ct);
            var survey = await history.SurveyAsync(DateTimeOffset.Now, SurveyPause, exact: false, ct);
            _survey = survey;
            log.LogInformation("Surveyed {Series} history series in {Ms} ms, reading {Read}",
                survey.Series.Count, (long)survey.Took.TotalMilliseconds, StorageFormat.Bytes(survey.PagesRead * survey.PageSize));
            try
            {
                await settings.SaveAsync(SurveyKey, JsonSerializer.Serialize(survey), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogDebug(ex, "Could not keep the storage survey for after a restart");
            }
            return survey;
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Whether the survey is old enough, or missing, for the job to run it again.</summary>
    public bool SurveyDue(DateTimeOffset now) => _survey is not { } survey || now - survey.At >= SurveyEvery;

    // ---------- Growth ----------

    /// <summary>
    /// Notes the database's data size, at most once every fifty minutes, and keeps a
    /// fortnight of them. Called by the hourly job with figures it has just read.
    /// </summary>
    public async Task RecordSizeAsync(DatabaseFacts facts, DateTimeOffset now, CancellationToken ct = default)
    {
        await LoadAsync(ct);
        string stored;
        lock (_gate)
        {
            var at = now.ToUnixTimeSeconds();
            if (_sizes.Count > 0 && at - _sizes[^1].At < 50 * 60)
                return;
            _sizes.Add((at, facts.UsedBytes));
            _sizes.RemoveAll(point => at - point.At > (long)SizesKept.TotalSeconds);
            stored = FormatSizes(_sizes);
        }
        await settings.SaveAsync(SizesKey, stored, ct);
        Changed?.Invoke();
    }

    /// <summary>The last day's growth against the usual, from memory.</summary>
    public GrowthFacts Growth(DateTimeOffset now)
    {
        lock (_gate)
            return GrowthOf(_sizes, now);
    }

    /// <summary>
    /// Growth from hourly size points. The last day is the newest point against the one
    /// nearest a day before it; "usual" is the median of the whole days before that, back a
    /// week, each measured the same way — a median so that one day the rollup caught up (and
    /// the size fell) or one odd day does not set the bar. Points more than an hour and a half
    /// from where one is wanted do not count, so a gap while LabbyTwo was off is a gap rather
    /// than a day that looks twice as long.
    /// </summary>
    public static GrowthFacts GrowthOf(IReadOnlyList<(long At, long Used)> points, DateTimeOffset now)
    {
        if (points.Count < 2 || now.ToUnixTimeSeconds() - points[^1].At > 3 * 3600)
            return new GrowthFacts(null, null, 0);

        var newest = points[^1];
        long? At(long when)
        {
            var best = points.MinBy(p => Math.Abs(p.At - when));
            return Math.Abs(best.At - when) <= 90 * 60 ? best.Used : null;
        }

        long? lastDay = At(newest.At - 86400) is { } dayAgo ? newest.Used - dayAgo : null;
        var days = new List<long>();
        for (var d = 1; d <= 7; d++)
        {
            if (At(newest.At - d * 86400L) is { } end && At(newest.At - (d + 1) * 86400L) is { } start)
                days.Add(end - start);
        }
        days.Sort();
        long? usual = days.Count >= 3 ? days[days.Count / 2] : null;
        return new GrowthFacts(lastDay, usual, days.Count);
    }

    /// <summary>"unix:bytes;unix:bytes" — short, and readable in a backup.</summary>
    private static string FormatSizes(IEnumerable<(long At, long Used)> points)
    {
        var text = new StringBuilder();
        foreach (var (at, used) in points)
            text.Append(CultureInfo.InvariantCulture, $"{at}:{used};");
        return text.ToString();
    }

    private static List<(long At, long Used)> ParseSizes(string stored)
    {
        var points = new List<(long, long)>();
        foreach (var part in stored.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Split(':') is [var at, var used]
                && long.TryParse(at, CultureInfo.InvariantCulture, out var a)
                && long.TryParse(used, CultureInfo.InvariantCulture, out var u))
            {
                points.Add((a, u));
            }
        }
        points.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        return points;
    }

    // ---------- Tidy up now ----------

    /// <summary>
    /// Applies the retention settings now rather than at the next quarter-hour: the same
    /// rollup the job runs, the same short batches, run until it has nothing left. False if
    /// one is already running or a compaction is.
    /// </summary>
    public bool StartTidy()
    {
        lock (_gate)
        {
            if (_tidy.Running || _compaction.Running)
                return false;
            _tidy = new StorageTask(true, DateTimeOffset.Now, "Summarising and removing old readings", null, null, null);
        }
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            long rows = 0, freed = 0;
            try
            {
                HistoryStore.RollupResult result;
                do
                {
                    result = await history.RollupAsync(TimeSpan.FromMinutes(5));
                    rows += result.RowsDeleted;
                    freed += result.FreedBytes;
                    SetTidy(_tidy with { Stage = $"Removed {StorageFormat.Rows(rows)} readings so far" });
                }
                while (!result.Finished && !history.Writes.Compacting);

                await history.PruneAsync(CancellationToken.None);
                SetTidy(_tidy with
                {
                    Running = false, FinishedAt = DateTimeOffset.Now, Stage = "",
                    Outcome = rows == 0
                        ? "Nothing was due: everything kept is inside its retention."
                        : $"Removed {StorageFormat.Rows(rows)} readings, freeing about {StorageFormat.Bytes(freed)} inside the file.",
                });
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Tidying history up failed");
                SetTidy(_tidy with { Running = false, FinishedAt = DateTimeOffset.Now, Stage = "", Error = ex.GetBaseException().Message });
            }
        });
        return true;
    }

    private void SetTidy(StorageTask task)
    {
        lock (_gate)
            _tidy = task;
        Changed?.Invoke();
    }

    // ---------- Compact now ----------

    /// <summary>
    /// Starts a compaction in the background, unless it should not: one is already running,
    /// a tidy-up is, or — for a full one — the disk has not room for it. Returns why not, or
    /// null when it has started. Never called by anything but a person pressing the button.
    /// </summary>
    public string? StartCompaction(DatabaseFacts facts, bool makeIncremental)
    {
        if (!facts.Incremental && facts.FullCompactionProblem() is { } problem)
            return problem;

        lock (_gate)
        {
            if (_compaction.Running)
                return "A compaction is already running.";
            if (_tidy.Running)
                return "A tidy-up is running; compact once it has finished, so it has the most to give back.";
            _compaction = new StorageTask(true, DateTimeOffset.Now, facts.Incremental ? "Giving free pages back" : "Rewriting the database", null, null, null);
        }
        Changed?.Invoke();
        log.LogInformation("Compacting the database ({Kind}), asked for from the storage page",
            facts.Incremental ? "incrementally" : "with VACUUM");

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await history.CompactAsync(makeIncremental,
                    stage => SetCompaction(_compaction with { Stage = stage }), CancellationToken.None);
                var dropped = result.Dropped > 0 ? $" {StorageFormat.Rows(result.Dropped)} readings were not recorded while it ran." : "";
                log.LogInformation("Compacted the database from {Before} to {After} in {Seconds:0} s",
                    StorageFormat.Bytes(result.BytesBefore), StorageFormat.Bytes(result.BytesAfter), result.Took.TotalSeconds);
                SetCompaction(_compaction with
                {
                    Running = false, FinishedAt = DateTimeOffset.Now, Stage = "",
                    Outcome = $"The database went from {StorageFormat.Bytes(result.BytesBefore)} to {StorageFormat.Bytes(result.BytesAfter)} " +
                              $"in {SelfWatchRules.Words(result.Took)}.{dropped}",
                });
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Compacting the database failed");
                SetCompaction(_compaction with { Running = false, FinishedAt = DateTimeOffset.Now, Stage = "", Error = ex.GetBaseException().Message });
            }
        });
        return null;
    }

    private void SetCompaction(StorageTask task)
    {
        lock (_gate)
            _compaction = task;
        Changed?.Invoke();
    }
}
