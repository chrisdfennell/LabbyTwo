using System.Text.RegularExpressions;
using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>Which containers a search reads.</summary>
public enum LogScope
{
    All,
    Running,
    Chosen,
    Project,
}

/// <summary>
/// What to look for, where and when. Built by the Logs page from its form, from the address
/// bar, or from an incident; nothing about it is stored.
/// </summary>
public sealed record LogSearchQuery
{
    /// <summary>What to find: plain text, or a regular expression when <see cref="IsRegex"/>. Empty with a quick filter chosen means "any of those".</summary>
    public string Text { get; init; } = "";

    public bool IsRegex { get; init; }

    public bool CaseSensitive { get; init; }

    /// <summary>Keys of <see cref="LogSearch.QuickFilters"/>. A line must match one of them as well as the text.</summary>
    public IReadOnlyCollection<string> Quick { get; init; } = [];

    public DateTimeOffset Since { get; init; }

    /// <summary>Null for a window that ends now, which is also what lets Docker hand back the newest lines.</summary>
    public DateTimeOffset? Until { get; init; }

    public bool Stdout { get; init; } = true;

    public bool Stderr { get; init; } = true;

    /// <summary>Lines kept either side of a match, shown when it is opened up.</summary>
    public int ContextLines { get; init; } = LogSearch.DefaultContextLines;

    /// <summary>The most lines read from one container.</summary>
    public int MaxLinesPerContainer { get; init; } = LogSearch.DefaultMaxLines;

    /// <summary>The most bytes read from one container.</summary>
    public long MaxBytesPerContainer { get; init; } = LogSearch.DefaultMaxBytes;

    /// <summary>The most lines — matches and their context — held for the whole search.</summary>
    public int MaxHeld { get; init; } = LogSearch.DefaultMaxHeld;

    /// <summary>How long one line may take a regular expression before it is given up on.</summary>
    public TimeSpan RegexTimeout { get; init; } = LogSearch.DefaultRegexTimeout;
}

/// <summary>One container to read, and the Docker connection it is reached through.</summary>
public sealed record LogTarget(string ConnectionId, string ConnectionName, string Endpoint, TimeSpan Timeout, ContainerRow Container)
{
    public string Key => ConnectionId + "/" + Container.Id;
}

/// <summary>
/// One matching line, already masked, with the lines around it. Immutable once it is
/// handed to the page: its following context is filled in before it is published, so the
/// page never reads a list the reading thread is still writing.
/// </summary>
/// <param name="Spans">Where the match is in <see cref="Line"/>'s text, for highlighting.</param>
public sealed record LogHit(
    LogLine Line,
    IReadOnlyList<(int Start, int Length)> Spans,
    IReadOnlyList<LogLine> Before,
    IReadOnlyList<LogLine> After);

public enum LogGroupState
{
    Waiting,
    Reading,
    Done,
    Failed,
}

/// <summary>
/// What one container gave a search. Written by the reading thread under the run's lock,
/// read by the page through <see cref="Hits"/>, which copies.
/// </summary>
public sealed class LogGroup(LogTarget target, object sync)
{
    private readonly List<LogHit> _hits = [];

    public LogTarget Target { get; } = target;

    public LogGroupState State { get; internal set; }

    public string? Error { get; internal set; }

    /// <summary>Every match found, including any not held because the search hit its cap.</summary>
    public int Matches { get; internal set; }

    public int LinesRead { get; internal set; }

    public long BytesRead { get; internal set; }

    /// <summary>Set when reading stopped at the per-container line or byte cap rather than at the end of the window.</summary>
    public string? Cut { get; internal set; }

    /// <summary>The held matches, oldest first — at most <paramref name="max"/> of them.</summary>
    public IReadOnlyList<LogHit> Hits(int max = int.MaxValue)
    {
        lock (sync)
            return _hits.Count <= max ? [.. _hits] : _hits.GetRange(0, max);
    }

    public int Held
    {
        get
        {
            lock (sync)
                return _hits.Count;
        }
    }

    internal void Add(LogHit hit) => _hits.Add(hit);
}

/// <summary>
/// "error in the last hour, everywhere": one search over the recent logs of many containers.
///
/// Runs only when somebody presses Search, and only for as long as the page that asked is
/// open — nothing here polls, and nothing is written to the database. Each container's log
/// is asked for with <c>since</c> and <c>until</c> so Docker does the time filtering, read a
/// few containers at a time (<see cref="Concurrency"/>) so a NAS is not asked for forty logs
/// at once, and stopped at a line and byte cap per container. Only matching lines and a few
/// lines around each are kept, and at most <see cref="DefaultMaxHeld"/> of those for the
/// whole search, so a pattern that matches everything costs a bounded amount of memory and
/// says it was capped.
///
/// Every line is masked (<see cref="Mask"/>) before it is matched, so what is found is what
/// is shown — and a search for somebody's password cannot confirm it by matching.
/// </summary>
public sealed class LogSearchRun
{
    private readonly object _sync = new();
    private readonly LogSearchQuery _query;
    private readonly LogMatcher _matcher;
    private readonly List<LogGroup> _groups;
    private readonly Dictionary<string, string> _deniedWhy = new(StringComparer.Ordinal);
    private int _held;
    private int _version;

    /// <exception cref="ArgumentException">The pattern is not one that can be searched for, with a sentence saying why.</exception>
    public LogSearchRun(LogSearchQuery query, IReadOnlyList<LogTarget> targets)
    {
        _query = query;
        _matcher = LogMatcher.Create(query);
        _groups = [.. targets.Select(t => new LogGroup(t, _sync))];
    }

    public LogSearchQuery Query => _query;

    public IReadOnlyList<LogGroup> Groups => _groups;

    /// <summary>Goes up whenever anything a page shows changes; the page redraws when it has moved.</summary>
    public int Version => Volatile.Read(ref _version);

    public bool Finished { get; private set; }

    /// <summary>Why the whole search was stopped early — a pattern too slow to run — or null.</summary>
    public string? Stopped { get; private set; }

    /// <summary>True once the search held all it may; later matches are counted, not kept.</summary>
    public bool Capped { get; private set; }

    public int TotalMatches
    {
        get
        {
            lock (_sync)
                return _groups.Sum(g => g.Matches);
        }
    }

    public int HeldLines => Volatile.Read(ref _held);

    /// <summary>Whether the fast engine could take the pattern, or it fell back to the ordinary one with a timeout.</summary>
    public bool NonBacktracking => _matcher.NonBacktracking;

    /// <summary>
    /// Reads every container, <paramref name="concurrency"/> at a time, until done or
    /// cancelled. Never throws for one container's failure — that is written on its group —
    /// and returns quietly when cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken ct, int concurrency = LogSearch.Concurrency)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = _groups.Select(async group =>
        {
            try
            {
                await gate.WaitAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await ReadAsync(group, stop);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_sync)
            {
                foreach (var group in _groups.Where(g => g.State is LogGroupState.Waiting or LogGroupState.Reading))
                {
                    group.State = LogGroupState.Failed;
                    group.Error ??= Stopped ?? "Not read — the search was stopped.";
                }
                Finished = true;
            }
            Bump();
        }
    }

    private async Task ReadAsync(LogGroup group, CancellationTokenSource stop)
    {
        var target = group.Target;
        lock (_sync)
        {
            // One refusal from a socket proxy is every refusal on that endpoint: the flag is
            // per proxy, not per container, so the other thirty-nine are not asked.
            if (_deniedWhy.TryGetValue(target.Endpoint, out var why))
            {
                group.State = LogGroupState.Failed;
                group.Error = why;
                Bump();
                return;
            }
            group.State = LogGroupState.Reading;
        }
        Bump();

        var q = _query;
        var ct = stop.Token;
        try
        {
            var path = DockerLogs.WindowPath(target.Container.Id, q.Since, q.Until,
                tail: q.Until is null ? q.MaxLinesPerContainer : null, q.Stdout, q.Stderr);
            await using var stream = await DockerSocket.OpenStreamAsync(target.Endpoint, target.Timeout, path, ct);
            var reader = new SniffingLogReader();
            var scan = new Scanner(this, group, q.ContextLines);
            var buffer = new byte[16 * 1024];
            int read;
            string? cut = null;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                lock (_sync)
                    group.BytesRead += read;
                if (!scan.Take(reader.Feed(buffer.AsSpan(0, read))))
                {
                    cut = $"stopped after {q.MaxLinesPerContainer:N0} lines";
                    break;
                }
                if (group.BytesRead >= q.MaxBytesPerContainer)
                {
                    cut = $"stopped after {LogSearch.Size(q.MaxBytesPerContainer)}";
                    break;
                }
                if (_matcher.Timeouts >= LogSearch.MaxTimeouts)
                    break;
            }
            if (cut is null)
                scan.Take(reader.Flush());
            scan.End();

            if (_matcher.Timeouts >= LogSearch.MaxTimeouts)
            {
                lock (_sync)
                    Stopped ??= "The pattern took too long on some lines, so the search was stopped. " +
                                "Try a simpler regular expression, or plain text.";
                stop.Cancel();
            }

            lock (_sync)
            {
                group.Cut = cut;
                group.State = LogGroupState.Done;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_sync)
            {
                group.State = LogGroupState.Failed;
                group.Error = Stopped ?? "Not finished — the search was stopped.";
            }
        }
        catch (DockerProxyDeniedException ex)
        {
            lock (_sync)
            {
                _deniedWhy.TryAdd(target.Endpoint, ex.Message);
                group.State = LogGroupState.Failed;
                group.Error = ex.Message;
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                group.State = LogGroupState.Failed;
                group.Error = DockerContainers.Explain(ex, target.Endpoint);
            }
        }
        Bump();
    }

    private void Bump() => Interlocked.Increment(ref _version);

    /// <summary>
    /// Adds a finished hit to its group, if the search may still hold it. The match is counted
    /// either way, so a capped search still says how many there were.
    /// </summary>
    private void Publish(LogGroup group, LogHit hit)
    {
        var weight = 1 + hit.Before.Count + hit.After.Count;
        lock (_sync)
        {
            if (_held + weight <= _query.MaxHeld)
            {
                group.Add(hit);
                _held += weight;
            }
            else
            {
                Capped = true;
            }
        }
        Bump();
    }

    /// <summary>
    /// One container's lines going past: masks each, matches it, and keeps the few before
    /// and after a match. A match waits for its following lines before it is published.
    /// </summary>
    private sealed class Scanner(LogSearchRun run, LogGroup group, int context)
    {
        private readonly Queue<LogLine> _before = new();
        private readonly List<(LogLine Line, IReadOnlyList<(int, int)> Spans, LogLine[] Before, List<LogLine> After)> _pending = [];
        private int _lines;

        /// <returns>False once the container's line cap is reached.</returns>
        public bool Take(IReadOnlyList<LogLine> lines)
        {
            var q = run._query;
            foreach (var raw in lines)
            {
                if (_lines >= q.MaxLinesPerContainer)
                    return false;
                _lines++;

                // Docker filters by time already; this only guards against a daemon that
                // ignored `until`, which old API versions did.
                if (raw.Time is { } time && (time < q.Since || (q.Until is { } until && time > until)))
                    continue;

                var line = raw with { Text = LogSearch.Mask(raw.Text) };

                for (var i = _pending.Count - 1; i >= 0; i--)
                {
                    var hit = _pending[i];
                    hit.After.Add(line);
                    if (hit.After.Count >= context)
                    {
                        _pending.RemoveAt(i);
                        run.Publish(group, new LogHit(hit.Line, hit.Spans, hit.Before, hit.After));
                    }
                }

                if (run._matcher.Match(line.Text) is { } spans)
                {
                    lock (run._sync)
                        group.Matches++;
                    var before = _before.ToArray();
                    if (context == 0)
                        run.Publish(group, new LogHit(line, spans, before, []));
                    else
                        _pending.Insert(0, (line, spans, before, new List<LogLine>(context)));
                }

                if (context > 0)
                {
                    _before.Enqueue(line);
                    if (_before.Count > context)
                        _before.Dequeue();
                }
            }

            lock (run._sync)
                group.LinesRead = _lines;
            return true;
        }

        /// <summary>The stream ended: matches still waiting for context go out with what they have.</summary>
        public void End()
        {
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                var hit = _pending[i];
                run.Publish(group, new LogHit(hit.Line, hit.Spans, hit.Before, hit.After));
            }
            _pending.Clear();
            lock (run._sync)
                group.LinesRead = _lines;
        }
    }
}

/// <summary>
/// Whether a line matches, and where. Text is found with <see cref="string.IndexOf(string, StringComparison)"/>;
/// a regular expression with <see cref="RegexOptions.NonBacktracking"/> when the pattern allows
/// it — linear time whatever the input, so a pattern cannot hang the server — and otherwise
/// with the ordinary engine and a per-line timeout. A line that times out counts as no match,
/// and the search gives up after <see cref="LogSearch.MaxTimeouts"/> of them.
/// </summary>
public sealed class LogMatcher
{
    private readonly string _text;
    private readonly StringComparison _comparison;
    private readonly Regex? _regex;
    private readonly Regex? _quick;
    private int _timeouts;

    private LogMatcher(string text, StringComparison comparison, Regex? regex, Regex? quick, bool nonBacktracking)
    {
        _text = text;
        _comparison = comparison;
        _regex = regex;
        _quick = quick;
        NonBacktracking = nonBacktracking;
    }

    public bool NonBacktracking { get; }

    /// <summary>How many lines have taken the pattern longer than its timeout.</summary>
    public int Timeouts => Volatile.Read(ref _timeouts);

    /// <exception cref="ArgumentException">With a plain-English reason: nothing to look for, a pattern too long, or one that is not valid.</exception>
    public static LogMatcher Create(LogSearchQuery query)
    {
        var text = query.Text;
        var quickKeys = query.Quick.Where(k => LogSearch.QuickFilters.Any(f => f.Key == k)).Distinct().ToList();
        if (text.Length == 0 && quickKeys.Count == 0)
            throw new ArgumentException("Type something to look for, or pick one of the quick filters.");
        if (text.Length > LogSearch.MaxPatternLength)
            throw new ArgumentException($"That is longer than {LogSearch.MaxPatternLength} characters — search for part of it.");

        Regex? quick = null;
        if (quickKeys.Count > 0)
        {
            var pattern = string.Join("|", LogSearch.QuickFilters.Where(f => quickKeys.Contains(f.Key)).Select(f => $"(?:{f.Pattern})"));
            quick = new Regex(pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, query.RegexTimeout);
        }

        if (!query.IsRegex || text.Length == 0)
            return new LogMatcher(text, query.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase,
                null, quick, nonBacktracking: true);

        var options = RegexOptions.CultureInvariant | (query.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new LogMatcher(text, StringComparison.Ordinal,
                new Regex(text, options | RegexOptions.NonBacktracking, query.RegexTimeout), quick, nonBacktracking: true);
        }
        catch (NotSupportedException)
        {
            // Back-references, look-arounds and atomic groups need the backtracking engine.
            // It is allowed, with the timeout as the guard.
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("That is not a valid regular expression: " + ex.Message);
        }

        try
        {
            return new LogMatcher(text, StringComparison.Ordinal, new Regex(text, options, query.RegexTimeout), quick,
                nonBacktracking: false);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("That is not a valid regular expression: " + ex.Message);
        }
    }

    /// <summary>Where the line matches — the text's spans and the quick filter's — or null when it does not.</summary>
    public IReadOnlyList<(int Start, int Length)>? Match(string line)
    {
        try
        {
            var spans = new List<(int, int)>();
            if (_regex is not null)
            {
                foreach (System.Text.RegularExpressions.Match match in _regex.Matches(line))
                {
                    if (match.Length > 0)
                        spans.Add((match.Index, match.Length));
                    if (spans.Count >= LogSearch.MaxSpans)
                        break;
                }
                if (spans.Count == 0 && !_regex.IsMatch(line))
                    return null;
            }
            else if (_text.Length > 0)
            {
                var at = 0;
                while (at <= line.Length - _text.Length && spans.Count < LogSearch.MaxSpans)
                {
                    var hit = line.IndexOf(_text, at, _comparison);
                    if (hit < 0)
                        break;
                    spans.Add((hit, _text.Length));
                    at = hit + _text.Length;
                }
                if (spans.Count == 0)
                    return null;
            }

            if (_quick is not null)
            {
                var found = false;
                foreach (System.Text.RegularExpressions.Match match in _quick.Matches(line))
                {
                    found = true;
                    if (spans.Count < LogSearch.MaxSpans * 2)
                        spans.Add((match.Index, match.Length));
                }
                if (!found)
                    return null;
            }

            return LogSearch.Merge(spans);
        }
        catch (RegexMatchTimeoutException)
        {
            Interlocked.Increment(ref _timeouts);
            return null;
        }
    }
}

/// <summary>
/// The rules of the log search: the caps, the quick filters, the masking, and turning an
/// incident into a search.
/// </summary>
public static partial class LogSearch
{
    /// <summary>How many containers' logs are read at once. The stats gate's number, for the same reason.</summary>
    public const int Concurrency = DockerContainers.StatsConcurrency;

    public const int DefaultMaxLines = 5000;

    public const long DefaultMaxBytes = 4L * 1024 * 1024;

    /// <summary>
    /// Lines held for the page — matches and their context — across the whole search. At
    /// the reader's 16 KB a line this is the ceiling, not the usual: most lines are short.
    /// </summary>
    public const int DefaultMaxHeld = 20_000;

    public const int DefaultContextLines = 3;

    public const int MaxPatternLength = 500;

    /// <summary>How many lines may time out on a regular expression before the search gives up on it.</summary>
    public const int MaxTimeouts = 3;

    /// <summary>Highlights per line; a pattern matching every letter does not need ten thousand marks.</summary>
    public const int MaxSpans = 50;

    public static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The one-click filters. Each is a whole word, case-insensitive, and a line must match
    /// one of those chosen as well as whatever is typed. "exception" matches inside a word on
    /// purpose, so <c>NullReferenceException</c> counts.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Label, string Pattern)> QuickFilters =
    [
        ("error", "error", @"\berrors?\b|\bERR\b"),
        ("warn", "warn", @"\bwarn(ing)?s?\b|\bWRN\b"),
        ("exception", "exception", "exception"),
        ("fatal", "fatal", @"\bfatal\b|\bpanic\b|\bFTL\b"),
        ("failed", "“failed”", @"\bfailed\b"),
    ];

    /// <summary>The time ranges offered, by the key the address bar uses.</summary>
    public static readonly IReadOnlyList<(string Key, string Label, TimeSpan Span)> Ranges =
    [
        ("15m", "Last 15 minutes", TimeSpan.FromMinutes(15)),
        ("1h", "Last hour", TimeSpan.FromHours(1)),
        ("6h", "Last 6 hours", TimeSpan.FromHours(6)),
        ("24h", "Last 24 hours", TimeSpan.FromHours(24)),
    ];

    /// <summary>"4 MB", "512 KB".</summary>
    public static string Size(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB" : $"{bytes / 1024.0:0.#} KB";

    /// <summary>Sorted spans with overlaps joined, so highlighting never cuts a mark in two.</summary>
    public static IReadOnlyList<(int Start, int Length)> Merge(List<(int Start, int Length)> spans)
    {
        if (spans.Count < 2)
            return spans;
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int Length)> { spans[0] };
        foreach (var (start, length) in spans.Skip(1))
        {
            var last = merged[^1];
            var end = last.Start + last.Length;
            if (start <= end)
                merged[^1] = (last.Start, Math.Max(end, start + length) - last.Start);
            else
                merged.Add((start, length));
        }
        return merged;
    }

    /// <summary>The containers a scope picks, in name order.</summary>
    /// <param name="chosen">Container names, for <see cref="LogScope.Chosen"/>.</param>
    /// <param name="project">The Compose project, for <see cref="LogScope.Project"/>.</param>
    public static IReadOnlyList<ContainerRow> Pick(
        IEnumerable<ContainerRow> rows, LogScope scope, IReadOnlyCollection<string>? chosen = null, string? project = null) =>
    [
        .. rows.Where(row => scope switch
            {
                LogScope.Running => row.IsRunning || row.IsRestarting,
                LogScope.Chosen => chosen?.Contains(row.Name, StringComparer.OrdinalIgnoreCase) == true,
                LogScope.Project => project is { Length: > 0 } && string.Equals(row.Project, project, StringComparison.OrdinalIgnoreCase),
                _ => true,
            })
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
    ];

    // ---- masking ------------------------------------------------------------------

    /// <summary>
    /// <c>PASSWORD=hunter2</c>, <c>"api_key": "abc"</c>, <c>?token=abc&amp;</c>, <c>secret: abc</c> — any
    /// name the config history treats as a secret (<see cref="ContainerConfigs.SecretWords"/>),
    /// then <c>=</c> or <c>:</c>, then the value. "Bearer" and "Basic" are left for
    /// <see cref="Bearer"/>, which hides the token after them instead of the word.
    /// </summary>
    [GeneratedRegex(
        @"(?<key>[\w.-]{0,40}(?:" + ContainerConfigs.SecretWords + @")[\w.-]{0,40})(?<sep>[""']?\s*[:=]\s*[""']?)(?!(?:Bearer|Basic)\b)(?<value>[^\s""',;&}\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MaskTimeoutMs)]
    private static partial Regex SecretPair();

    [GeneratedRegex(@"\b(?<scheme>Bearer|Basic)\s+(?<value>[A-Za-z0-9._~+/=-]{6,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MaskTimeoutMs)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?<head>[a-z][a-z0-9+.-]*://[^/\s:@]*:)[^/\s@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MaskTimeoutMs)]
    private static partial Regex CredentialUrl();

    /// <summary>
    /// A log line with secret-looking values replaced by <see cref="ContainerSafety.Mask"/>.
    /// Best effort, and the page says so: it catches the shapes programs usually print
    /// secrets in, not a password somebody wrote into a sentence.
    /// </summary>
    public static string Mask(string text)
    {
        if (text.Length == 0)
            return text;
        try
        {
            var masked = CredentialUrl().Replace(text, m => m.Groups["head"].Value + ContainerSafety.Mask + "@");
            masked = Bearer().Replace(masked, m => m.Groups["scheme"].Value + " " + ContainerSafety.Mask);
            return SecretPair().Replace(masked, m =>
                m.Groups["value"].Value == ContainerSafety.Mask
                    ? m.Value
                    : m.Groups["key"].Value + m.Groups["sep"].Value + ContainerSafety.Mask);
        }
        catch (RegexMatchTimeoutException)
        {
            // A line the masks cannot get through in time is not shown at all, rather than
            // shown unchecked.
            return MaskGaveUp;
        }
    }

    /// <summary>What a line too awkward to check for secrets is shown as.</summary>
    public const string MaskGaveUp = "(line hidden — too long to check for secrets)";

    /// <summary>How long the masks may take over one line. They are linear; this is the backstop.</summary>
    private const int MaskTimeoutMs = 250;

    // ---- incidents ----------------------------------------------------------------

    /// <summary>The Logs page, filled in for one incident and searching straight away.</summary>
    public static string IncidentLink(long id) => $"logs?incident={id}";

    /// <summary>
    /// What "search logs around this incident" fills in: the incident's timeline window,
    /// and the containers its services are reached through — tied the way the Containers tab
    /// ties them (<see cref="ContainerSafety.ConnectionsReaching"/>), by the host name in a
    /// connection's address.
    /// </summary>
    /// <param name="Until">Null while the incident is open, so the search reaches now.</param>
    /// <param name="Containers">Docker connection id and container name, for each container involved.</param>
    public sealed record IncidentPrefill(
        DateTimeOffset Since,
        DateTimeOffset? Until,
        IReadOnlyList<(string ConnectionId, string Container)> Containers);

    /// <param name="rowsByDocker">Each Docker connection's containers, by the connection's id.</param>
    public static IncidentPrefill ForIncident(
        Incident incident, IReadOnlyList<Connection> connections,
        IReadOnlyDictionary<string, IReadOnlyList<ContainerRow>> rowsByDocker, DateTimeOffset now)
    {
        var involved = incident.Members.Select(m => m.ConnectionId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var containers = new List<(string, string)>();
        foreach (var (dockerId, rows) in rowsByDocker.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            foreach (var row in rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (ContainerSafety.ConnectionsReaching(row, connections).Any(c => involved.Contains(c.Id)))
                    containers.Add((dockerId, row.Name));
            }
        }

        return new IncidentPrefill(
            IncidentRules.TimelineFrom(incident),
            incident.EndedAt is null ? null : IncidentRules.TimelineTo(incident, now),
            containers);
    }
}
