using System.Collections.Concurrent;

namespace LabbyTwo.Core;

/// <summary>
/// One repository on a Git server, in the shape the cards want to draw rather than the
/// shape any particular API returns.
/// </summary>
/// <param name="Path">
/// Where it lives under the server's base URL — <c>"chris/labbytwo"</c> on Gitea and
/// GitLab, bare <c>"labbytwo"</c> on MyPersonalGit. Kept separate from the name because
/// only the forge knows which of the two its URLs use, and a link built on the wrong
/// guess 404s.
/// </param>
public sealed record GitRepo(
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
    int OpenPulls,
    string Path = "")
{
    /// <summary>The API returns the on-disk name, which carries a ".git" nobody wants to read.</summary>
    public string Name =>
        RawName.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? RawName[..^4] : RawName;

    public string PathOrName => Path.Length > 0 ? Path : Name;
}

/// <param name="RepoPath">The repository's <see cref="GitRepo.Path"/>, so a row can link without a lookup.</param>
public sealed record GitIssue(
    int Number, string Title, string Author, DateTimeOffset CreatedAt, int Comments,
    string Repo, string RepoPath = "");

/// <inheritdoc cref="GitIssue"/>
public sealed record GitPull(
    int Number, string Title, string Author, bool IsDraft,
    string SourceBranch, string TargetBranch, DateTimeOffset CreatedAt,
    string Repo, string RepoPath = "");

public sealed record GitOverview(
    IReadOnlyList<GitRepo> Repos,
    IReadOnlyList<GitPull> OpenPulls,
    IReadOnlyList<GitIssue> OpenIssues)
{
    public static readonly GitOverview Empty = new([], [], []);

    public int Stars => Repos.Sum(r => r.Stars);
    public GitRepo? MostRecent => Repos.MaxBy(r => r.UpdatedAt);
}

/// <summary>
/// A provider that is a Git server. This is the second interface a provider can add to
/// itself — <see cref="IAlertChannel"/> was the first — and it exists for the same reason:
/// the Git summary, repository and open-work cards, and the whole Git server page, want
/// structured data rather than metrics, and there is more than one thing in the world that
/// can supply it.
///
/// Declaring it is all that is needed. The cards accept any provider that does, including
/// one from a plugin they have never heard of, which is the same bargain the media stack
/// makes through <c>Category</c>.
/// </summary>
public interface IGitForge
{
    /// <summary>
    /// Everything the cards show, in one call. Implementations are expected to cache —
    /// six widgets on a page all want this at once. <see cref="CachedGitForge"/> does it.
    /// </summary>
    Task<GitOverview> OverviewAsync(Connection connection, CancellationToken ct);

    /// <summary>
    /// Where a browser should go for this server. Not the probed address: those are often
    /// different, which is what the "Link opens" field on these providers is for.
    /// </summary>
    string LinkBase(Connection connection);

    /// <summary>
    /// What this forge calls a change proposed from a branch. GitLab says "merge request",
    /// and a page that told a GitLab user they had three open pull requests would be
    /// quietly speaking a different product's language at them.
    /// </summary>
    string PullNoun => "pull request";

    string PullNounPlural => PullNoun + "s";

    /// <summary>Link to one repository.</summary>
    string RepoUrl(Connection connection, string repoPath) =>
        $"{LinkBase(connection)}/{repoPath}";
}

/// <summary>
/// The caching half of a forge, which is identical everywhere and worth writing once.
///
/// An overview is expensive — several calls, and on some forges a call per repository — so
/// a Git page holding four cards bound to the same server must not make four rounds of
/// them. The lock is as important as the cache: on first paint every card asks at the same
/// moment, and without it they queue up and then each fetch in turn anyway.
/// </summary>
public abstract class CachedGitForge : IGitForge
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, (GitOverview Value, DateTimeOffset At)> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    protected abstract Task<GitOverview> FetchAsync(Connection connection, CancellationToken ct);

    public abstract string LinkBase(Connection connection);

    public async Task<GitOverview> OverviewAsync(Connection connection, CancellationToken ct)
    {
        if (Fresh(connection) is { } cached)
            return cached;

        await _lock.WaitAsync(ct);
        try
        {
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

    private GitOverview? Fresh(Connection connection) =>
        _cache.TryGetValue(connection.Id, out var entry) && DateTimeOffset.UtcNow - entry.At < Ttl
            ? entry.Value
            : null;
}

/// <summary>Names a widget or a field can use to mean "any Git server", however it is implemented.</summary>
public static class GitForges
{
    /// <summary>
    /// The wildcard. Deliberately unusable as a provider key — <c>Type</c> is a plain
    /// identifier everywhere — so it cannot collide with a real one.
    /// </summary>
    public const string Any = "@forge";

    public static IReadOnlyList<string> Types => [Any];
}
