using System.Net;
using System.Reflection;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Providers;

namespace LabbyTwo.Services;

/// <summary>
/// Compares the commit this image was built from against the tip of the branch on GitHub.
///
/// It only ever *reports*. LabbyTwo runs inside the container an update would replace, and
/// a process cannot rebuild and recreate itself — doing that from in here would mean
/// mounting the Docker socket, which is root on the host. Telling you, and handing you the
/// command, is the honest half of the job.
/// </summary>
public sealed class UpdateChecker(IHttpClientFactory httpFactory, ILogger<UpdateChecker> log)
{
    private const string Repository = "chrisdfennell/LabbyTwo";
    private const string Branch = "main";

    /// <param name="Behind">Null when it could not be worked out — an unstamped build, or the check failed.</param>
    public sealed record Result(
        string Installed,
        string? Latest,
        bool? Behind,
        string? Summary,
        DateTimeOffset? Released,
        string? Error)
    {
        /// <summary>
        /// What changed since this build. Between the two releases when this is one, since
        /// that is a fixed range somebody can read; against the branch when this is a
        /// commit, because "everything on main since my build" is the question there and
        /// the tip is allowed to move on afterwards.
        /// </summary>
        public string CompareUrl
        {
            get
            {
                var target = ChannelOf(Installed) == Channel.Release && Latest is { Length: > 0 } tag
                    ? tag
                    : Branch;
                return $"https://github.com/{Repository}/compare/{Installed}...{target}";
            }
        }

        public bool Known => Behind is not null;
    }

    /// <summary>
    /// What the stamp is, which decides what "newest" is compared against. An install from
    /// a release is measured against the newest release; one tracking main is measured
    /// against the tip of main, because it deliberately runs ahead of the last release and
    /// calling that "behind" would be backwards.
    /// </summary>
    public enum Channel
    {
        /// <summary>Built without a stamp. There is nothing to compare.</summary>
        Unstamped,

        /// <summary>A release tag, exactly: <c>v1.0.0</c>.</summary>
        Release,

        /// <summary>A commit — a bare sha, or <c>v1.0.0-3-gabc1234</c> from git describe.</summary>
        Commit,
    }

    /// <summary>
    /// Which of the two an installed stamp is. install.sh writes the tag when it installs a
    /// release and a `git describe` when it tracks main, so the shape of the string is
    /// enough to tell them apart without stamping the channel separately.
    /// </summary>
    public static Channel ChannelOf(string? installed)
    {
        var value = (installed ?? "").Trim();
        if (value.Length == 0 || value == "dev")
            return Channel.Unstamped;

        // A describe string carries the commit after "-g", and that suffix is what makes
        // it a commit rather than the release it is counting from.
        if (CommitOf(value) is not null)
            return Channel.Commit;

        var digits = value.StartsWith('v') ? value[1..] : value;
        var release = digits.Length > 0
            && digits.All(c => char.IsAsciiDigit(c) || c == '.')
            && char.IsAsciiDigit(digits[0]);

        return release ? Channel.Release : Channel.Unstamped;
    }

    /// <summary>
    /// The commit a stamp refers to, or null if it does not name one. Handles both a bare
    /// sha and the <c>-gabc1234</c> tail of a git describe.
    /// </summary>
    public static string? CommitOf(string? installed)
    {
        var value = (installed ?? "").Trim();

        var marker = value.LastIndexOf("-g", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var tail = value[(marker + 2)..];
            return IsSha(tail) ? tail : null;
        }

        return IsSha(value) ? value : null;

        static bool IsSha(string s) =>
            s.Length is >= 7 and <= 40 && s.All(char.IsAsciiHexDigit)
            && s.Any(char.IsAsciiDigit);   // "deadbeef" is hex; so is a word. Require a digit.
    }

    /// <summary>
    /// The commit stamped in at build time by install.sh. "dev" means someone built it by
    /// hand without the stamp, in which case there is nothing to compare against.
    /// </summary>
    public static string Installed => Sanitise(Raw);

    /// <summary>Exactly what the assembly carries, warts and all, for showing on a tooltip.</summary>
    public static string Raw =>
        typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "";

    /// <summary>
    /// A build arg that did not get set can leave anything in here — an empty string, a
    /// stray control character, an unexpanded variable — and rendering that raw put an
    /// unprintable box on the settings page. Anything that is not a plausible version or
    /// commit is reported as unstamped, which is what it actually is.
    /// </summary>
    public static string Sanitise(string? informational)
    {
        var value = (informational ?? "").Split('+')[0].Trim();
        value = new string([.. value.Where(c => !char.IsControl(c))]);

        var plausible = value.Length is > 0 and <= 40
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

        return plausible ? value : "dev";
    }

    private Result? _cached;
    private DateTimeOffset _checkedAt;

    /// <summary>Whatever the last check found, without going near the network.</summary>
    public Result? Last => _cached;

    public async Task<Result> CheckAsync(CancellationToken ct = default)
    {
        // A minute's grace so a double-click, or two people on the dashboard at once, is
        // one request. GitHub allows 60 an hour unauthenticated and this is nowhere near.
        if (_cached is not null && DateTimeOffset.UtcNow - _checkedAt < TimeSpan.FromMinutes(1))
            return _cached;

        var installed = Installed;
        var channel = ChannelOf(installed);
        if (channel == Channel.Unstamped)
        {
            return Store(new Result(installed, null, null, null, null,
                "This build was not stamped with a version, so there is nothing to compare. " +
                "Installs made by install.sh and install.ps1 are stamped; a plain " +
                "\"docker compose build\" is not."));
        }

        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);

            // A release install is measured against the newest release; one tracking main
            // against the tip of main. Comparing a main build to the last release would
            // report it behind for being ahead.
            var url = channel == Channel.Release
                ? $"https://api.github.com/repos/{Repository}/releases/latest"
                : $"https://api.github.com/repos/{Repository}/commits/{Branch}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // GitHub rejects requests with no user agent.
            request.Headers.TryAddWithoutValidation("User-Agent", "LabbyTwo");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // A repository with no releases yet answers 404 here, which is a fact about
                // the project rather than a failure worth showing as one.
                if (channel == Channel.Release && response.StatusCode == HttpStatusCode.NotFound)
                {
                    return Store(new Result(installed, null, null, null, null,
                        "There are no published releases to compare against yet."));
                }

                return Store(new Result(installed, null, null, null, null,
                    $"GitHub answered {(int)response.StatusCode}. Rate limiting, or no route out from here."));
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;

            if (channel == Channel.Release)
                return Store(ReadRelease(installed, root));

            var sha = root.TryGetProperty("sha", out var shaElement) ? shaElement.GetString() ?? "" : "";
            if (sha.Length == 0)
                return Store(new Result(installed, null, null, null, null, "GitHub returned no commit."));

            var commit = root.TryGetProperty("commit", out var c) ? c : default;
            var message = commit.ValueKind == JsonValueKind.Object &&
                          commit.TryGetProperty("message", out var m) ? m.GetString() : null;
            var date = commit.ValueKind == JsonValueKind.Object &&
                       commit.TryGetProperty("author", out var author) &&
                       author.TryGetProperty("date", out var d) &&
                       d.TryGetDateTimeOffset(out var parsed) ? parsed : (DateTimeOffset?)null;

            var latest = sha[..Math.Min(12, sha.Length)];

            // The stamp is a short sha, or a describe carrying one after "-g". Either way
            // compare by prefix rather than equality, since it is shorter than the full sha.
            var mine = CommitOf(installed) ?? installed;
            var behind = !sha.StartsWith(mine, StringComparison.OrdinalIgnoreCase);

            return Store(new Result(installed, latest, behind, FirstLine(message), date, null));
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Update check failed");
            return Store(new Result(installed, null, null, null, null,
                ProbeError.Describe(ex, "api.github.com")));
        }
    }

    /// <summary>
    /// The newest release, read out of GitHub's own release object. Compared by name
    /// rather than by ordering: "is this the current one" is the question, and inventing a
    /// version-number comparison here would be wrong the first time somebody tags
    /// v1.10.0 after v1.9.0.
    /// </summary>
    public static Result ReadRelease(string installed, JsonElement root)
    {
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (tag.Length == 0)
            return new Result(installed, null, null, null, null, "GitHub returned no release tag.");

        var title = root.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(title) || title.Trim() == tag)
            title = FirstLine(root.TryGetProperty("body", out var b) ? b.GetString() : null);

        var date = root.TryGetProperty("published_at", out var p) && p.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : (DateTimeOffset?)null;

        // A "v" on one side and not the other is the same release either way.
        var behind = !string.Equals(Bare(tag), Bare(installed), StringComparison.OrdinalIgnoreCase);

        return new Result(installed, tag, behind, title, date, null);

        static string Bare(string v) => v.TrimStart('v', 'V');
    }

    /// <summary>Commit messages are a subject line then a body; only the subject is wanted here.</summary>
    public static string? FirstLine(string? message) =>
        message?.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();

    private Result Store(Result result)
    {
        _cached = result;
        _checkedAt = DateTimeOffset.UtcNow;
        return result;
    }
}
