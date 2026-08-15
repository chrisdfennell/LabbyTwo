using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Gitea, and with it Forgejo and Gogs. One provider covers all three because they are the
/// same program twice removed — Gogs came first, Gitea forked it, Forgejo forked Gitea —
/// and all of them still answer the same <c>/api/v1</c>. Adding three providers that
/// differed only in their display name would be three times the surface for no more
/// capability.
///
/// It reports the same metrics as the other forges and implements
/// <see cref="IGitForge"/>, so the Git summary, repository and open-work cards and the
/// whole Git server page work with it without knowing it exists.
/// </summary>
public sealed class GiteaProvider(IHttpClientFactory httpFactory) : CachedGitForge, IConnectionProvider
{
    public string Type => "gitea";
    public string DisplayName => "Gitea / Forgejo";
    public string Icon => "🍵";
    public string Category => "Development";

    public string Description =>
        "A self-hosted Gitea, Forgejo or Gogs server — repositories, open pull requests, open issues and stars.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.50:3000", Required: true,
            Help: "Just the server's address — the API path is added for you. It is reached by LabbyTwo, " +
                  "so it has to resolve from inside its container."),

        new("token", "Access token", FieldKind.Password, Required: true,
            Help: "Settings → Applications → Generate New Token, inside Gitea. Read-only scopes are enough: " +
                  "this never writes anything."),

        new("open_url", "Link opens", FieldKind.Url, "leave blank to use the URL above",
            Help: "Optional. Set this when the browser should open a different address than the one probed — " +
                  "a public hostname, say, where the probe uses a container name."),

        new("limit", "Repositories to read", FieldKind.Number, Default: "50",
            Help: "The newest this many. A server with hundreds is not worth pulling whole for a dashboard card.")
        { Advanced = true },
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("repo_count", "Repositories"),
        new("open_pulls", "Open pull requests"),
        new("open_issues", "Open issues"),
        new("stars", "Stars"),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Review queue is backing up", "open_pulls", Comparison.Above, 10, ForMinutes: 60,
            Why: "Pull requests nobody has merged. Worth knowing on a server you share with other people, " +
                 "and easy to set to something meaningless on one you do not."),
    ];

    public override string LinkBase(Connection connection) =>
        (connection.Settings.Get("open_url") is { Length: > 0 } custom
            ? custom
            : connection.Settings.Get("url")).TrimEnd('/');

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var overview = await OverviewAsync(connection, ct);
            stopwatch.Stop();

            return ProbeResult.Up(
                stopwatch.Elapsed,
                $"{overview.Repos.Count} {(overview.Repos.Count == 1 ? "repository" : "repositories")}, " +
                $"{overview.OpenPulls.Count} open PR{(overview.OpenPulls.Count == 1 ? "" : "s")}",
                new Dictionary<string, double>
                {
                    ["repo_count"] = overview.Repos.Count,
                    ["open_pulls"] = overview.OpenPulls.Count,
                    ["open_issues"] = overview.OpenIssues.Count,
                    ["stars"] = overview.Stars,
                    ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
                });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, Explain(ex, connection));
        }
    }

    private static string Explain(Exception ex, Connection connection) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            "The token was rejected. Settings → Applications inside Gitea, and generate a new one — " +
            "a token is shown once and cannot be read back, so a half-copied one looks exactly like this.",

        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            $"No Gitea API at {connection.Settings.Get("url")}. The address should be the server's root, " +
            "not a path inside it — /api/v1 is added for you.",

        _ => ProbeError.Describe(ex, connection.Settings.Get("url")),
    };

    /// <summary>
    /// Three calls, whatever the server's size. Gitea's issue search spans every repository
    /// the token can see, so the open pull requests and open issues arrive whole rather
    /// than a call at a time per repository — which is what the MyPersonalGit provider has
    /// to do, and what makes it slow against thirty repositories.
    /// </summary>
    protected override async Task<GitOverview> FetchAsync(Connection connection, CancellationToken ct)
    {
        var limit = Math.Clamp(connection.Settings.GetInt("limit", 50), 1, 200);

        var repos = new List<GitRepo>();
        using (var list = await GetJsonAsync(connection, $"/api/v1/user/repos?limit={limit}", ct))
        {
            if (list.RootElement.ValueKind == JsonValueKind.Array)
                repos.AddRange(list.RootElement.EnumerateArray().Select(ReadRepo));
        }

        var pulls = await SearchAsync(connection, "pulls", limit, ReadPull, ct);
        var issues = await SearchAsync(connection, "issues", limit, ReadIssue, ct);

        // The counts the search found, folded back onto each repository, so the table can
        // show them without a call each. A repository with none simply keeps its zero.
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

    /// <summary>
    /// Gitea returns issues and pull requests from the same endpoint, told apart by
    /// <c>type</c>. Older Gogs has no such search, so a failure here is an empty list
    /// rather than a failed page — the repository list is the part that matters, and a
    /// server that cannot search is still a server worth showing.
    /// </summary>
    private async Task<List<T>> SearchAsync<T>(
        Connection connection, string type, int limit, Func<JsonElement, T> read, CancellationToken ct)
    {
        var found = new List<T>();
        try
        {
            using var doc = await GetJsonAsync(
                connection, $"/api/v1/repos/issues/search?state=open&type={type}&limit={limit}", ct);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                found.AddRange(doc.RootElement.EnumerateArray().Select(read));
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
            Int(e, "stars_count"),
            Int(e, "forks_count"),
            0,                                   // Gitea does not carry a commit count on the repo.
            Str(e, "default_branch"),
            Date(e, "updated_at") ?? Date(e, "created_at") ?? DateTimeOffset.MinValue,
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
            Ref(e, "head"),
            Ref(e, "base"),
            Date(e, "created_at") ?? DateTimeOffset.MinValue,
            path.Contains('/') ? path[(path.IndexOf('/') + 1)..] : path,
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
            path.Contains('/') ? path[(path.IndexOf('/') + 1)..] : path,
            path);
    }

    private async Task<JsonDocument> GetJsonAsync(Connection connection, string path, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            throw new InvalidOperationException("No base URL configured.");

        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);

        // "token", not "Bearer". Recent Gitea accepts both, Gogs and older Gitea only this
        // one, and it costs nothing to speak the older dialect that everything understands.
        request.Headers.TryAddWithoutValidation(
            "Authorization", $"token {connection.Settings.Get("token")}");

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    // ---- reading Gitea's shapes ------------------------------------------------------

    /// <summary>Gitea nests the owner as an object; MyPersonalGit has it as a string.</summary>
    private static string Owner(JsonElement e) =>
        e.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object
            ? Str(owner, "login")
            : Str(e, "owner");

    private static string User(JsonElement e, string name) =>
        e.TryGetProperty(name, out var user) && user.ValueKind == JsonValueKind.Object
            ? Str(user, "login")
            : "";

    /// <summary>A branch name off a pull request's head or base object.</summary>
    private static string Ref(JsonElement e, string name) =>
        e.TryGetProperty(name, out var side) && side.ValueKind == JsonValueKind.Object
            ? Str(side, "ref")
            : "";

    /// <summary>
    /// Which repository a searched issue belongs to. The search result carries a
    /// <c>repository</c> object; failing that, the API URL it came from ends in the path.
    /// </summary>
    private static string RepoPath(JsonElement e)
    {
        if (e.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.Object)
        {
            if (Str(repo, "full_name") is { Length: > 0 } full)
                return full;
            if (Str(repo, "name") is { Length: > 0 } name)
                return Str(repo, "owner") is { Length: > 0 } owner ? $"{owner}/{name}" : name;
        }

        // …/api/v1/repos/{owner}/{name}/issues/{n}
        var url = Str(e, "url");
        var marker = "/repos/";
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
