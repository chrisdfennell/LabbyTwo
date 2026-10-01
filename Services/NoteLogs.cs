using System.Globalization;
using System.Text.RegularExpressions;
using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>
/// <c>{{logs: plex last=15m errors}}</c>: the last few matching lines from one container's log,
/// inside a note. The Logs page's search, narrowed to what fits under a heading — one
/// container, one window, a handful of lines — and read the same way: Docker is asked for the
/// window, every line is masked (<see cref="LogSearch.Mask"/>) before it is matched, and the
/// reading stops at a line and byte cap.
///
/// Read only when somebody is looking at the note, never by a sweep; see <c>LiveLogs</c> for
/// how often.
/// </summary>
public static partial class NoteLogs
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ShortestWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LongestWindow = TimeSpan.FromHours(24);

    public const int DefaultLines = 10;
    public const int MaxLines = 50;

    /// <summary>The newest lines asked of Docker. Matches are looked for among these, so "errors in the last day" on a chatty container means "in its last 5,000 lines".</summary>
    public const int ReadLines = 5000;

    /// <summary>The most bytes read from the log, whatever the line count.</summary>
    public const long ReadBytes = 2L * 1024 * 1024;

    /// <summary>The quick filters <c>errors</c> switches on: the Logs page's error, exception, fatal and "failed".</summary>
    public static readonly IReadOnlyList<string> ErrorFilters = ["error", "exception", "fatal", "failed"];

    /// <summary>What a <c>{{logs: …}}</c> asks for.</summary>
    /// <param name="Container">A container's name, or a Compose service's.</param>
    /// <param name="Connection">The Docker connection named in <c>connection=</c>; empty for the first one.</param>
    /// <param name="Match">Plain text a line must contain, from <c>match="…"</c>.</param>
    public sealed record Request(string Container, string Connection, TimeSpan Window, bool Errors, string Match, int Lines);

    /// <summary>
    /// Reads <c>{{logs: plex last=15m errors lines=20 match="timeout" connection=NAS}}</c>. The
    /// first word is the container; <c>errors</c> may be a bare word after it or
    /// <c>errors=true</c>.
    /// </summary>
    public static Request? Read(Shortcode code, out string? problem)
    {
        problem = null;
        var words = code.Target.SelectMany(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var container = code.Option("container", words.FirstOrDefault() ?? "");
        if (container.Length == 0)
        {
            problem = "Say which container: {{logs: plex last=15m errors}}.";
            return null;
        }

        var errors = false;
        foreach (var word in words.Skip(code.Options.ContainsKey("container") ? 0 : 1))
        {
            if (word.Equals("errors", StringComparison.OrdinalIgnoreCase) || word.Equals("error", StringComparison.OrdinalIgnoreCase))
            {
                errors = true;
                continue;
            }
            problem = $"“{word}” is not something {{{{logs}}}} understands. Write options like last=15m, errors, match=\"text\", lines=10.";
            return null;
        }
        if (code.Option("errors") is { Length: > 0 } flag)
            errors = flag.ToLowerInvariant() is not ("false" or "no" or "off" or "0");

        var window = Window(code.Option("last"), out var badWindow);
        if (window is null)
        {
            problem = badWindow;
            return null;
        }

        var lines = DefaultLines;
        if (code.Option("lines") is { Length: > 0 } written)
        {
            if (!int.TryParse(written, NumberStyles.None, CultureInfo.InvariantCulture, out lines) || lines < 1 || lines > MaxLines)
            {
                problem = $"lines= is a number from 1 to {MaxLines}; “{written}” is not.";
                return null;
            }
        }

        var match = code.Option("match").Trim();
        if (match.Length > LogSearch.MaxPatternLength)
        {
            problem = $"match= is at most {LogSearch.MaxPatternLength} characters.";
            return null;
        }

        return new Request(container, code.Option("connection"), window.Value, errors, match, lines);
    }

    /// <summary><c>last=</c>: five minutes to a day. Blank is fifteen minutes.</summary>
    public static TimeSpan? Window(string? written, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(written))
            return DefaultWindow;
        var match = WindowWord().Match(written.Trim());
        if (!match.Success)
        {
            problem = $"“{written}” is not a length of time. Write it like 15m, 1h or 24h.";
            return null;
        }
        var count = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var span = match.Groups[2].Value.ToLowerInvariant() is "h" ? TimeSpan.FromHours(count)
            : match.Groups[2].Value.ToLowerInvariant() is "d" ? TimeSpan.FromDays(count)
            : TimeSpan.FromMinutes(count);
        if (span < ShortestWindow || span > LongestWindow)
        {
            problem = $"{{{{logs}}}} looks between 5m and 24h back; “{written}” is outside that.";
            return null;
        }
        return span;
    }

    [GeneratedRegex(@"^(\d{1,4})\s*(m|min|h|d)$", RegexOptions.IgnoreCase)]
    private static partial Regex WindowWord();

    /// <summary>
    /// The container a name means: its own name first, then a Compose service of that name —
    /// so <c>plex</c> finds <c>media-plex-1</c> when that is how Compose named it. Running
    /// ones are preferred where a service has several.
    /// </summary>
    public static ContainerRow? Find(IEnumerable<ContainerRow> rows, string name)
    {
        var wanted = name.Trim().TrimStart('/');
        var list = rows as IReadOnlyCollection<ContainerRow> ?? [.. rows];
        return list.FirstOrDefault(r => string.Equals(r.Name.TrimStart('/'), wanted, StringComparison.OrdinalIgnoreCase))
            ?? list.Where(r => string.Equals(r.Service, wanted, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.IsRunning)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
    }

    /// <summary>The search a request is, for the matcher: the text, and the error filters when asked.</summary>
    public static LogSearchQuery Query(Request request, DateTimeOffset now) => new()
    {
        Text = request.Match,
        Quick = request.Errors ? ErrorFilters : [],
        Since = now - request.Window,
        ContextLines = 0,
    };

    /// <summary>
    /// The newest <see cref="Request.Lines"/> matching lines in the window, oldest first,
    /// masked. Reads the window's last <see cref="ReadLines"/> lines and at most
    /// <see cref="ReadBytes"/>, keeping only a ring of the newest matches, so a log that
    /// matches on every line costs no more than one that matches on none.
    /// </summary>
    public static async Task<IReadOnlyList<LogLine>> ReadAsync(
        string endpoint, TimeSpan timeout, string containerId, Request request, DateTimeOffset now, CancellationToken ct)
    {
        var query = Query(request, now);
        // No filter at all is the plain tail, which the search page has no reason to offer.
        var matcher = query.Text.Length == 0 && query.Quick.Count == 0 ? null : LogMatcher.Create(query);
        var path = DockerLogs.WindowPath(containerId, query.Since, until: null, tail: ReadLines);
        await using var stream = await DockerSocket.OpenStreamAsync(endpoint, timeout, path, ct);

        var reader = new SniffingLogReader();
        var kept = new Queue<LogLine>(request.Lines + 1);
        var buffer = new byte[16 * 1024];
        long bytes = 0;
        int read;

        void Take(IReadOnlyList<LogLine> lines)
        {
            foreach (var raw in lines)
            {
                // Docker filters by time already; this guards against one that ignored it.
                if (raw.Time is { } time && time < query.Since)
                    continue;
                var line = raw with { Text = LogSearch.Mask(raw.Text) };
                if (matcher is not null && matcher.Match(line.Text) is null)
                    continue;
                kept.Enqueue(line);
                if (kept.Count > request.Lines)
                    kept.Dequeue();
            }
        }

        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            Take(reader.Feed(buffer.AsSpan(0, read)));
            bytes += read;
            if (bytes >= ReadBytes)
                break;
        }
        if (bytes < ReadBytes)
            Take(reader.Flush());
        return [.. kept];
    }

    /// <summary>
    /// The Logs page, filled in for the same search and running it — the window rounded up
    /// to the nearest range the page offers.
    /// </summary>
    public static string PageLink(Request request, string dockerId, string container)
    {
        var range = LogSearch.Ranges.FirstOrDefault(r => r.Span >= request.Window).Key ?? LogSearch.Ranges[^1].Key;
        var link = $"logs?connection={Uri.EscapeDataString(dockerId)}&container={Uri.EscapeDataString(container)}&last={range}";
        if (request.Errors)
            link += "&quick=" + string.Join(',', ErrorFilters);
        if (request.Match.Length > 0)
            link += "&q=" + Uri.EscapeDataString(request.Match);
        // The page needs something to look for before it will search; with no filter it
        // opens filled in and waits for one.
        return request.Errors || request.Match.Length > 0 ? link + "&run=1" : link;
    }

    /// <summary>"no errors in the last 15 minutes", "no lines matching “timeout” in the last hour".</summary>
    public static string Nothing(Request request)
    {
        var what = (request.Errors, request.Match.Length > 0) switch
        {
            (true, true) => $"no errors mentioning “{request.Match}”",
            (true, false) => "no errors",
            (false, true) => $"no lines mentioning “{request.Match}”",
            _ => "nothing logged",
        };
        return $"{what} in the last {ChangeWindow.Describe(request.Window)}";
    }
}
