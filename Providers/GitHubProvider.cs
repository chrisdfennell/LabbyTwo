using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// GitHub, and GitHub Enterprise with it.
///
/// The Git page and its three cards were written against <see cref="IGitForge"/> rather
/// than against any particular server, so this is the fourth thing to answer that question
/// and needs no changes anywhere else to appear on all of them.
///
/// The one thing it does differently from the self-hosted forges: those show you everything
/// the token can see, which on your own server is the right default because everything on
/// it is yours. On GitHub a token can see thousands of repositories you merely have access
/// to, so this asks what you actually want to watch — see <c>scope</c>.
/// </summary>
public sealed class GitHubProvider(IHttpClientFactory httpFactory) : CachedGitForge, IConnectionProvider
{
    public string Type => "github";
    public string DisplayName => "GitHub";
    public string Icon => "🐙";
    public string Category => "Development";

    public string Description =>
        "GitHub or GitHub Enterprise — repositories, open pull requests and issues, for your account, "
        + "an organisation, or a named handful.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("token", "Access token", FieldKind.Password, Required: true,
            Help: "Settings → Developer settings → Personal access tokens, on GitHub. Read-only is enough: "
                + "this never writes. A fine-grained token needs Contents, Issues and Pull requests, all read."),

        new("scope", "What to watch", FieldKind.Select, Default: ScopeMine,
            Help: "A token can see every repository you have access to, which on GitHub is usually far "
                + "more than you want on a dashboard.",
            Options:
            [
                new(ScopeMine, "Repositories I own"),
                new(ScopeUser, "A user's public repositories"),
                new(ScopeOrg, "An organisation's repositories"),
                new(ScopeList, "Just the ones I name"),
            ]),

        new("owner", "User or organisation", FieldKind.Text, "chrisdfennell",
            Help: "Only for the middle two choices above. Ignored otherwise."),

        new("repos", "Repositories", FieldKind.Textarea, "chrisdfennell/LabbyTwo",
            Help: "Only for “Just the ones I name”. One owner/name per line."),

        new("url", "GitHub Enterprise URL", FieldKind.Url, "leave blank for github.com",
            Help: "Only for a self-hosted GitHub Enterprise. The API path is added for you.")
        { Advanced = true },

        new("limit", "Repositories to read", FieldKind.Number, Default: "50",
            Help: "The most recently updated this many.")
        { Advanced = true },
    ];

    public const string ScopeMine = "mine";
    public const string ScopeUser = "user";
    public const string ScopeOrg = "org";
    public const string ScopeList = "list";

    /// <summary>
    /// GitHub's search API allows 30 requests a minute and this makes two of them per
    /// fetch. <see cref="CachedGitForge"/>'s two-minute cache already keeps a page of cards
    /// to one round, and this keeps the monitor's own sweep from being the thing that
    /// exhausts the budget on a dashboard left open all day.
    /// </summary>
    public TimeSpan MinimumInterval => TimeSpan.FromMinutes(5);

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("repo_count", "Repositories"),
        new("open_pulls", "Open pull requests"),
        new("open_issues", "Open issues"),
        new("stars", "Stars"),
        new("api_calls_left", "API calls left this hour"),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Review queue is backing up", "open_pulls", Comparison.Above, 10, ForMinutes: 60,
            Why: "Pull requests nobody has merged. Worth knowing on a repository other people contribute to."),

        new("Running out of API calls", "api_calls_left", Comparison.Below, 500, ClearThreshold: 1500,
            Why: "An authenticated token gets 5,000 an hour. Dropping this low means something is asking far "
               + "more often than a dashboard needs to, and the symptom of running out is cards going blank."),
    ];

    public override string LinkBase(Connection connection) =>
        connection.Settings.Get("url").Trim().TrimEnd('/') is { Length: > 0 } enterprise
            ? enterprise
            : "https://github.com";

    /// <summary>
    /// github.com serves its API from a different host; Enterprise serves it from a path on
    /// the same one. Getting this wrong is the most likely setup mistake, so it is derived
    /// rather than asked for.
    /// </summary>
    private static string ApiBase(Connection connection) =>
        connection.Settings.Get("url").Trim().TrimEnd('/') is { Length: > 0 } enterprise
            ? $"{enterprise}/api/v3"
            : "https://api.github.com";

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var overview = await OverviewAsync(connection, ct);
            stopwatch.Stop();

            var readings = new Dictionary<string, double>
            {
                ["repo_count"] = overview.Repos.Count,
                ["open_pulls"] = overview.OpenPulls.Count,
                ["open_issues"] = overview.OpenIssues.Count,
                ["stars"] = overview.Stars,
                ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
            };

            if (_remaining.TryGetValue(connection.Id, out var left))
                readings["api_calls_left"] = left;

            return ProbeResult.Up(
                stopwatch.Elapsed,
                $"{overview.Repos.Count} {(overview.Repos.Count == 1 ? "repository" : "repositories")}, "
                + $"{overview.OpenPulls.Count} open PR{(overview.OpenPulls.Count == 1 ? "" : "s")}",
                readings);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, Explain(ex, connection));
        }
    }

    private static string Explain(Exception ex, Connection connection) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
            "The token was rejected. A GitHub token is shown once and cannot be read back, so a "
            + "half-copied one looks exactly like this.",

        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
            "GitHub refused. Either the hourly API budget is spent — it resets on the hour — or a "
            + "fine-grained token is missing read access to Contents, Issues or Pull requests.",

        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            connection.Settings.Get("scope") switch
            {
                ScopeOrg => $"No organisation called “{connection.Settings.Get("owner")}”, or the token cannot see it.",
                ScopeUser => $"No user called “{connection.Settings.Get("owner")}”.",
                _ => "GitHub returned 404. Check the repository names — they are owner/name, and they are case "
                     + "sensitive to the API even though the website forgives it.",
            },

        InvalidOperationException => ex.Message,

        _ => ProbeError.Describe(ex, ApiBase(connection)),
    };

    /// <summary>
    /// What GitHub last said was left of the hourly budget, per connection. Read from a
    /// response header rather than by asking, because /rate_limit is itself a request.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _remaining = new();

    protected override async Task<GitOverview> FetchAsync(Connection connection, CancellationToken ct)
    {
        var limit = Math.Clamp(connection.Settings.GetInt("limit", 50), 1, 100);
        var scope = connection.Settings.Get("scope", ScopeMine);
        var owner = connection.Settings.Get("owner").Trim();

        var repos = new List<GitRepo>();
        string qualifier;

        switch (scope)
        {
            case ScopeList:
                // One call each. Deliberate: naming five repositories is the scope you pick
                // *because* you do not want the other two hundred, so five calls is cheaper
                // than listing an account and throwing most of it away.
                var named = Named(connection).Take(limit).ToList();
                if (named.Count == 0)
                    throw new InvalidOperationException("No repositories named. One owner/name per line.");

                foreach (var path in named)
                {
                    using var one = await GetJsonAsync(connection, $"/repos/{path}", ct);
                    repos.Add(ReadRepo(one.RootElement));
                }
                qualifier = string.Join(' ', named.Select(path => $"repo:{path}"));
                break;

            case ScopeUser or ScopeOrg:
                if (owner.Length == 0)
                    throw new InvalidOperationException(
                        "No user or organisation set, which the chosen scope needs.");

                var where = scope == ScopeOrg ? "orgs" : "users";
                using (var list = await GetJsonAsync(
                    connection, $"/{where}/{owner}/repos?sort=updated&per_page={limit}", ct))
                {
                    Add(repos, list.RootElement);
                }
                qualifier = scope == ScopeOrg ? $"org:{owner}" : $"user:{owner}";
                break;

            default:
                // affiliation=owner rather than the default, which also returns everything
                // you have ever been added to as a collaborator.
                using (var list = await GetJsonAsync(
                    connection, $"/user/repos?affiliation=owner&sort=updated&per_page={limit}", ct))
                {
                    Add(repos, list.RootElement);
                }

                // The search API has no "whatever this token is" qualifier, so the login has
                // to be asked for. One extra call every two minutes, and only for this scope.
                using (var me = await GetJsonAsync(connection, "/user", ct))
                {
                    qualifier = $"user:{Str(me.RootElement, "login")}";
                }
                break;
        }

        var pulls = await SearchAsync(connection, $"is:open is:pr {qualifier}", limit, ReadPull, ct);
        var issues = await SearchAsync(connection, $"is:open is:issue {qualifier}", limit, ReadIssue, ct);

        // Folded back from the search rather than taken from the repository's own
        // open_issues_count, which on GitHub counts pull requests as issues — a repository
        // with three PRs and no issues reports three, and the table would say so.
        var pullsBy = pulls.GroupBy(p => p.RepoPath).ToDictionary(g => g.Key, g => g.Count());
        var issuesBy = issues.GroupBy(i => i.RepoPath).ToDictionary(g => g.Key, g => g.Count());

        return new GitOverview(
            [.. repos
                .Select(r => r with
                {
                    OpenPulls = pullsBy.GetValueOrDefault(r.Path),
                    OpenIssues = issuesBy.GetValueOrDefault(r.Path),
                })
                .OrderByDescending(r => r.UpdatedAt)],
            [.. pulls.OrderByDescending(p => p.CreatedAt)],
            [.. issues.OrderByDescending(i => i.CreatedAt)]);
    }

    // ---- workflow runs ---------------------------------------------------------------

    /// <summary>
    /// The most recent workflow runs across the watched repositories.
    ///
    /// Kept off <see cref="ProbeAsync"/> deliberately. Runs are a call per repository, and
    /// making the monitor fetch them every sweep would spend the hourly budget on a number
    /// nothing alerts on. The card asks, and only while somebody is looking at it.
    /// </summary>
    public async Task<IReadOnlyList<CiRun>> RunsAsync(Connection connection, int limit, CancellationToken ct)
    {
        if (_runs.TryGetValue(connection.Id, out var cached)
            && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(2))
        {
            return cached.Value;
        }

        var overview = await OverviewAsync(connection, ct);

        // Newest repositories first, and only a handful of them: a run list is per
        // repository, and a card showing eight rows does not need thirty calls to fill.
        var repos = overview.Repos.Take(Math.Clamp(limit, 1, 8)).ToList();

        var runs = new List<CiRun>();
        foreach (var repo in repos)
        {
            try
            {
                using var doc = await GetJsonAsync(
                    connection, $"/repos/{repo.Path}/actions/runs?per_page={Math.Clamp(limit, 1, 10)}", ct);

                if (!doc.RootElement.TryGetProperty("workflow_runs", out var list)
                    || list.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                runs.AddRange(list.EnumerateArray().Select(e => ReadRun(e, repo.Name)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A repository with Actions switched off answers 404. One quiet repository
                // must not empty the whole card.
            }
        }

        var newest = (IReadOnlyList<CiRun>)[.. runs.OrderByDescending(run => run.At).Take(limit)];
        _runs[connection.Id] = (newest, DateTimeOffset.UtcNow);
        return newest;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string, (IReadOnlyList<CiRun> Value, DateTimeOffset At)> _runs = new();

    private static CiRun ReadRun(JsonElement e, string repo)
    {
        var started = Date(e, "run_started_at") ?? Date(e, "created_at") ?? DateTimeOffset.MinValue;
        var finished = Date(e, "updated_at") ?? started;

        return new CiRun(
            Str(e, "name") is { Length: > 0 } name ? name : Str(e, "display_title"),
            repo,
            Str(e, "head_branch"),
            Str(e, "status"),
            // Empty while a run is still going, which is what the card keys "running" on.
            Str(e, "conclusion"),
            started,
            finished > started ? finished - started : TimeSpan.Zero,
            Str(e, "html_url"));
    }

    private static void Add(List<GitRepo> repos, JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            repos.AddRange(root.EnumerateArray().Select(ReadRepo));
    }

    private static IEnumerable<string> Named(Connection connection) =>
        connection.Settings.Get("repos")
            .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // Tolerating a pasted URL, because that is what is on the clipboard when you go
            // looking for a repository's name.
            .Select(entry => entry.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(entry, UriKind.Absolute, out var url)
                    ? url.AbsolutePath.Trim('/')
                    : entry.Trim('/'))
            .Where(entry => entry.Contains('/'));

    /// <summary>
    /// Both open lists in one call each, whatever the number of repositories. A failure here
    /// is an empty list rather than a failed page: the repository list is the part that
    /// matters, and search is the first thing to be rate-limited.
    /// </summary>
    private async Task<List<T>> SearchAsync<T>(
        Connection connection, string query, int limit, Func<JsonElement, T> read, CancellationToken ct)
    {
        var found = new List<T>();
        try
        {
            using var doc = await GetJsonAsync(
                connection, $"/search/issues?q={Uri.EscapeDataString(query)}&per_page={limit}", ct);

            if (doc.RootElement.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Array)
            {
                found.AddRange(items.EnumerateArray().Select(read));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        return found;
    }

    private static GitRepo ReadRepo(JsonElement e)
    {
        var path = Str(e, "full_name");
        return new GitRepo(
            Str(e, "name"),
            Str(e, "description"),
            Owner(e),
            Bool(e, "private"),
            Int(e, "stargazers_count"),
            Int(e, "forks_count"),
            0,                                   // Not on the repository object, and not worth a call each.
            Str(e, "default_branch"),
            Date(e, "pushed_at") ?? Date(e, "updated_at") ?? DateTimeOffset.MinValue,
            0, 0,
            path.Length > 0 ? path : Str(e, "name"));
    }

    private static GitPull ReadPull(JsonElement e)
    {
        var path = RepoPath(e);
        return new GitPull(
            Int(e, "number"),
            Str(e, "title"),
            User(e, "user"),
            Bool(e, "draft"),
            // Search results carry no branch names. The cards fall back to the numbers and
            // the title, which is what a dashboard row shows anyway; fetching each pull
            // request to fill these in would be a call per row.
            "", "",
            Date(e, "created_at") ?? DateTimeOffset.MinValue,
            Leaf(path),
            path);
    }

    private static GitIssue ReadIssue(JsonElement e)
    {
        var path = RepoPath(e);
        return new GitIssue(
            Int(e, "number"),
            Str(e, "title"),
            User(e, "user"),
            Date(e, "created_at") ?? DateTimeOffset.MinValue,
            Int(e, "comments"),
            Leaf(path),
            path);
    }

    private static string Leaf(string path) =>
        path.Contains('/') ? path[(path.IndexOf('/') + 1)..] : path;

    private async Task<JsonDocument> GetJsonAsync(Connection connection, string path, CancellationToken ct)
    {
        var token = connection.Settings.Get("token");
        if (token.Length == 0)
            throw new InvalidOperationException("No access token configured.");

        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase(connection) + path);

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        // GitHub rejects a request with no User-Agent outright, with a 403 that says nothing
        // about the real cause. Every other provider here gets away without one.
        request.Headers.TryAddWithoutValidation("User-Agent", "LabbyTwo");

        using var response = await http.SendAsync(request, ct);

        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
            && double.TryParse(values.FirstOrDefault(), out var left))
        {
            _remaining[connection.Id] = left;
        }

        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    // ---- reading GitHub's shapes -----------------------------------------------------

    private static string Owner(JsonElement e) =>
        e.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object
            ? Str(owner, "login")
            : "";

    private static string User(JsonElement e, string name) =>
        e.TryGetProperty(name, out var user) && user.ValueKind == JsonValueKind.Object
            ? Str(user, "login")
            : "";

    /// <summary>
    /// Search results name their repository only as an API URL, which ends in the path:
    /// …/repos/{owner}/{name}
    /// </summary>
    private static string RepoPath(JsonElement e)
    {
        var url = Str(e, "repository_url");
        const string marker = "/repos/";
        var at = url.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            return "";

        var rest = url[(at + marker.Length)..].Split('/');
        return rest.Length >= 2 ? $"{rest[0]}/{rest[1]}" : "";
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), out var parsed)
            ? parsed.ToLocalTime()
            : null;
}
