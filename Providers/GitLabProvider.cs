using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// A self-hosted GitLab — Community Edition, Enterprise, or gitlab.com if you point it
/// there. Unlike Gitea and Forgejo this is not a Gitea-shaped API at all: different paths,
/// a different auth header, and a different word for the central idea, so it is a provider
/// of its own rather than a flag on that one.
///
/// It implements <see cref="IGitForge"/>, so the Git cards and the Git server page take it
/// without knowing anything about GitLab.
/// </summary>
// IGitForge is listed again even though CachedGitForge already supplies it. That is not
// redundant: PullNoun is a default interface member, and the interface mapping is fixed at
// the class that first declares the interface — so without this, the "merge request"
// override below is simply a property nobody calls, and GitLab pages say "pull request".
public sealed class GitLabProvider(IHttpClientFactory httpFactory)
    : CachedGitForge, IConnectionProvider, IGitForge
{
    public string Type => "gitlab";
    public string DisplayName => "GitLab";
    public string Icon => "🦊";
    public string Category => "Development";

    public string Description =>
        "A self-hosted GitLab — projects, open merge requests, open issues and stars.";

    /// <summary>
    /// GitLab's word, used everywhere the UI would otherwise say "pull request". Telling a
    /// GitLab user they have three open pull requests is speaking a competitor's language
    /// at them about their own server.
    /// </summary>
    public string PullNoun => "merge request";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "https://gitlab.example.com", Required: true,
            Help: "The server's root — /api/v4 is added for you."),

        new("token", "Access token", FieldKind.Password, Required: true,
            Help: "A personal access token with the read_api scope. User settings → Access tokens. " +
                  "read_api is enough; this never writes."),

        new("open_url", "Link opens", FieldKind.Url, "leave blank to use the URL above",
            Help: "Optional, for when the browser should open a different address than the one probed."),

        new("owned", "Only projects I own", FieldKind.Bool, Default: "false",
            Help: "Off lists everything you are a member of, which on a shared server can be a great many. " +
                  "On narrows it to yours."),

        new("limit", "Projects to read", FieldKind.Number, Default: "50") { Advanced = true },
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("repo_count", "Projects"),
        new("open_pulls", "Open merge requests"),
        new("open_issues", "Open issues"),
        new("stars", "Stars"),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Review queue is backing up", "open_pulls", Comparison.Above, 10, ForMinutes: 60,
            Why: "Merge requests nobody has looked at. Worth setting on a server other people push to."),
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
                $"{overview.Repos.Count} project{(overview.Repos.Count == 1 ? "" : "s")}, " +
                $"{overview.OpenPulls.Count} open MR{(overview.OpenPulls.Count == 1 ? "" : "s")}",
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
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
            "The token was rejected. It needs the read_api scope — a token with only read_user " +
            "authenticates fine and then cannot see a single project, which looks like an empty server.",

        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
            "GitLab accepted the token and refused the request, which usually means the token is " +
            "missing the read_api scope.",

        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            $"No GitLab API at {connection.Settings.Get("url")}. That should be the server's root — " +
            "/api/v4 is added for you.",

        _ => ProbeError.Describe(ex, connection.Settings.Get("url")),
    };

    /// <summary>
    /// Three calls. GitLab's <c>scope=all</c> spans every project the token can see, so
    /// merge requests and issues arrive whole rather than per project.
    /// </summary>
    protected override async Task<GitOverview> FetchAsync(Connection connection, CancellationToken ct)
    {
        var limit = Math.Clamp(connection.Settings.GetInt("limit", 50), 1, 200);
        var membership = connection.Settings.GetBool("owned") ? "owned=true" : "membership=true";

        var repos = new List<GitRepo>();
        using (var list = await GetJsonAsync(connection,
            $"/api/v4/projects?{membership}&order_by=last_activity_at&per_page={limit}", ct))
        {
            if (list.RootElement.ValueKind == JsonValueKind.Array)
                repos.AddRange(list.RootElement.EnumerateArray().Select(ReadRepo));
        }

        // Keyed by project id, because that is the only thing the two lists share: an
        // issue carries project_id and nothing about the path.
        var paths = repos.ToDictionary(r => r.Owner, r => r.Path);

        var pulls = await ListAsync(connection, "merge_requests", limit,
            e => ReadPull(e, paths), ct);
        var issues = await ListAsync(connection, "issues", limit,
            e => ReadIssue(e, paths), ct);

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

    private async Task<List<T>> ListAsync<T>(
        Connection connection, string what, int limit, Func<JsonElement, T> read, CancellationToken ct)
    {
        var found = new List<T>();
        try
        {
            using var doc = await GetJsonAsync(
                connection, $"/api/v4/{what}?state=opened&scope=all&per_page={limit}", ct);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                found.AddRange(doc.RootElement.EnumerateArray().Select(read));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A token without the breadth to search everything still lists projects.
        }
        return found;
    }

    /// <summary>
    /// <see cref="GitRepo.Owner"/> carries the project id here rather than a username. It
    /// is the only key GitLab's issue and merge-request lists give back, so it is what the
    /// counts are folded together on; the namespace is already in <c>Path</c>, which is
    /// what anything human-facing uses.
    /// </summary>
    private static GitRepo ReadRepo(JsonElement e) => new(
        Str(e, "name"),
        Str(e, "description"),
        Int(e, "id").ToString(),
        Str(e, "visibility") != "public",
        Int(e, "star_count"),
        Int(e, "forks_count"),
        0,
        Str(e, "default_branch"),
        Date(e, "last_activity_at") ?? Date(e, "created_at") ?? DateTimeOffset.MinValue,
        0, 0,
        Str(e, "path_with_namespace"));

    private static GitPull ReadPull(JsonElement e, IReadOnlyDictionary<string, string> paths)
    {
        var path = paths.GetValueOrDefault(Int(e, "project_id").ToString(), "");
        return new GitPull(
            Int(e, "iid"),
            Str(e, "title"),
            User(e),
            Str(e, "draft") == "true" || Bool(e, "draft") || Bool(e, "work_in_progress"),
            Str(e, "source_branch"),
            Str(e, "target_branch"),
            Date(e, "created_at") ?? DateTimeOffset.MinValue,
            Leaf(path),
            path);
    }

    private static GitIssue ReadIssue(JsonElement e, IReadOnlyDictionary<string, string> paths)
    {
        var path = paths.GetValueOrDefault(Int(e, "project_id").ToString(), "");
        return new GitIssue(
            Int(e, "iid"),
            Str(e, "title"),
            User(e),
            Date(e, "created_at") ?? DateTimeOffset.MinValue,
            Int(e, "user_notes_count"),
            Leaf(path),
            path);
    }

    /// <summary>The project name out of a namespaced path, for a column too narrow for both.</summary>
    private static string Leaf(string path) =>
        path.LastIndexOf('/') is var slash && slash >= 0 ? path[(slash + 1)..] : path;

    private async Task<JsonDocument> GetJsonAsync(Connection connection, string path, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            throw new InvalidOperationException("No base URL configured.");

        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);

        // GitLab's own header rather than Authorization. It accepts a Bearer token too, but
        // only for OAuth ones — a personal access token sent that way is refused, which is
        // an unhelpful way to spend an evening.
        request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", connection.Settings.Get("token"));

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static string User(JsonElement e) =>
        e.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object
            ? Str(author, "username")
            : "";

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
