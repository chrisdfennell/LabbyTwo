using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// A self-hosted MyPersonalGit server (github.com/chrisdfennell/MyPersonalGit) over its
/// Gitea-shaped REST API, authenticated with a personal access token.
///
/// One overview costs one call for the repository list and two more per repository, so the
/// result is cached for a couple of minutes and shared: the health probe, the tiles, the
/// repository table and the pull-request list all read the same fetch rather than each
/// setting off their own fan-out.
/// </summary>
public sealed class MyPersonalGitProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "mypersonalgit";
    public string DisplayName => "MyPersonalGit";
    public string Icon => "🐙";
    public string Category => "Development";
    public string Description =>
        "A self-hosted MyPersonalGit server — repositories, open pull requests, open issues and stars.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.50:3000", Required: true,
            Help: "Reached by the LabbyTwo server, so it has to resolve from wherever LabbyTwo runs."),
        new("token", "Access token", FieldKind.Password, Required: true,
            Help: "A personal access token from your MyPersonalGit profile. It starts with mypg_."),
        new("open_url", "Link opens", FieldKind.Url, "leave blank to use the URL above",
            Help: "Optional. Set this when the browser should open a different address than the one probed — a public hostname, say."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("repo_count", "Repositories"),
        new("open_pulls", "Open pull requests"),
        new("open_issues", "Open issues"),
        new("stars", "Stars"),
        new("latency_ms", "Response time", " ms"),
    ];

    // ---- what the widgets read -----------------------------------------------------

    public sealed record Repo(
        string RawName,
        string Description,
        string Owner,
        bool IsPrivate,
        int Stars,
        int Forks,
        int Commits,
        string DefaultBranch,
        DateTimeOffset UpdatedAt,
        int OpenIssues,
        int OpenPulls)
    {
        /// <summary>The API returns the on-disk name, which carries a ".git" nobody wants to read.</summary>
        public string Name =>
            RawName.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? RawName[..^4] : RawName;
    }

    public sealed record Issue(int Number, string Title, string Author, DateTimeOffset CreatedAt, int Comments, string Repo);

    public sealed record Pull(int Number, string Title, string Author, bool IsDraft,
        string SourceBranch, string TargetBranch, DateTimeOffset CreatedAt, string Repo);

    public sealed record Overview(IReadOnlyList<Repo> Repos, IReadOnlyList<Pull> OpenPulls, IReadOnlyList<Issue> OpenIssues)
    {
        public int Stars => Repos.Sum(r => r.Stars);
        public Repo? MostRecent => Repos.MaxBy(r => r.UpdatedAt);
    }

    /// <summary>Where a browser should go for this server — the override if set, else the probed URL.</summary>
    public static string LinkBase(Connection connection) =>
        (connection.Settings.Get("open_url") is { Length: > 0 } custom
            ? custom
            : connection.Settings.Get("url")).TrimEnd('/');

    // ---- probing --------------------------------------------------------------------

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var overview = await OverviewAsync(connection, ct);
            stopwatch.Stop();

            return ProbeResult.Up(
                stopwatch.Elapsed,
                $"{overview.Repos.Count} {(overview.Repos.Count == 1 ? "repository" : "repositories")}",
                new Dictionary<string, double>
                {
                    ["repo_count"] = overview.Repos.Count,
                    ["open_pulls"] = overview.OpenPulls.Count,
                    ["open_issues"] = overview.OpenIssues.Count,
                    ["stars"] = overview.Stars,
                    ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
                },
                overview.MostRecent is { } recent
                    ? new Dictionary<string, string> { ["Last updated"] = recent.Name }
                    : null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, Explain(ex, connection));
        }
    }

    private static string Explain(Exception ex, Connection connection) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } or
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
            "The token was rejected. Check it is a current mypg_ token and has not been revoked.",
        _ => ProbeError.Describe(ex, connection.Settings.Get("url")),
    };

    // ---- the shared fetch -------------------------------------------------------------

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, (Overview Value, DateTimeOffset At)> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Everything the Git page shows. Cached briefly per connection, so a page of six
    /// widgets bound to the same server costs one round of calls rather than six.
    /// </summary>
    public async Task<Overview> OverviewAsync(Connection connection, CancellationToken ct)
    {
        if (Fresh(connection) is { } cached)
            return cached;

        await _lock.WaitAsync(ct);
        try
        {
            // Checked again inside the lock: several widgets render at once on first paint,
            // and without this they would all queue up and then each fetch in turn.
            if (Fresh(connection) is { } stillFresh)
                return stillFresh;

            var overview = await FetchAsync(connection, ct);
            _cache[connection.Id] = (overview, DateTimeOffset.UtcNow);
            return overview;
        }
        finally
        {
            _lock.Release();
        }
    }

    private Overview? Fresh(Connection connection) =>
        _cache.TryGetValue(connection.Id, out var entry) && DateTimeOffset.UtcNow - entry.At < Ttl
            ? entry.Value
            : null;

    private async Task<Overview> FetchAsync(Connection connection, CancellationToken ct)
    {
        var repos = new List<Repo>();
        var pulls = new List<Pull>();
        var issues = new List<Issue>();

        using var list = await GetJsonAsync(connection, "/api/v1/repos", ct);
        if (list.RootElement.ValueKind != JsonValueKind.Array)
            return new Overview([], [], []);

        var bare = list.RootElement.EnumerateArray().Select(ReadRepo).ToList();

        // Open counts need a call each per repository. Six at a time keeps a server with
        // thirty repositories from taking thirty round trips in series, without opening
        // thirty connections to a machine that is probably a NAS.
        foreach (var batch in bare.Chunk(6))
        {
            foreach (var (repo, repoPulls, repoIssues) in await Task.WhenAll(batch.Select(r => DetailAsync(connection, r, ct))))
            {
                repos.Add(repo);
                pulls.AddRange(repoPulls);
                issues.AddRange(repoIssues);
            }
        }

        return new Overview(
            [.. repos.OrderByDescending(r => r.UpdatedAt)],
            [.. pulls.OrderByDescending(p => p.CreatedAt)],
            [.. issues.OrderByDescending(i => i.CreatedAt)]);
    }

    private async Task<(Repo Repo, List<Pull> Pulls, List<Issue> Issues)> DetailAsync(
        Connection connection, Repo repo, CancellationToken ct)
    {
        var name = Uri.EscapeDataString(repo.RawName);
        var pulls = new List<Pull>();
        var issues = new List<Issue>();

        // A repository with issues or pull requests switched off answers with an error
        // rather than an empty list. That is a zero, not a failure of the whole page.
        try
        {
            using var doc = await GetJsonAsync(connection, $"/api/v1/repos/{name}/pulls?state=open", ct);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                pulls.AddRange(doc.RootElement.EnumerateArray().Select(p => ReadPull(p, repo.Name)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Left at zero.
        }

        try
        {
            using var doc = await GetJsonAsync(connection, $"/api/v1/repos/{name}/issues?state=open", ct);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                issues.AddRange(doc.RootElement.EnumerateArray().Select(i => ReadIssue(i, repo.Name)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Left at zero.
        }

        return (repo with { OpenPulls = pulls.Count, OpenIssues = issues.Count }, pulls, issues);
    }

    private static Repo ReadRepo(JsonElement e) => new(
        Str(e, "name"), Str(e, "description"), Str(e, "owner"), Bool(e, "isPrivate"),
        Int(e, "stars"), Int(e, "forks"), Int(e, "commits"), Str(e, "default_branch"),
        Date(e, "updated_at") ?? Date(e, "created_at") ?? DateTimeOffset.MinValue, 0, 0);

    private static Pull ReadPull(JsonElement e, string repo) => new(
        Int(e, "number"), Str(e, "title"), Str(e, "author"), Bool(e, "isDraft"),
        Str(e, "source_branch"), Str(e, "target_branch"),
        Date(e, "created_at") ?? DateTimeOffset.MinValue, repo);

    private static Issue ReadIssue(JsonElement e, string repo) => new(
        Int(e, "number"), Str(e, "title"), Str(e, "author"),
        Date(e, "created_at") ?? DateTimeOffset.MinValue, Int(e, "comment_count"), repo);

    private async Task<JsonDocument> GetJsonAsync(Connection connection, string path, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            throw new InvalidOperationException("No base URL configured.");

        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Settings.Get("token"));

        // MyPersonalGit answers HTTP/1.1 and wants the connection closed after the body.
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        request.Headers.ConnectionClose = true;

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Some builds send a chunked body without the terminating zero-length chunk, so the
        // read throws at EOF even though the whole payload arrived. Buffer it and accept a
        // truncation that happens after we already have bytes; the JSON before it parses.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        try
        {
            await stream.CopyToAsync(buffer, ct);
        }
        catch (HttpIOException) when (buffer.Length > 0)
        {
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: ct);
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
