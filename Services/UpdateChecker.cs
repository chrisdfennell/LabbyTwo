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
        public string CompareUrl => $"https://github.com/{Repository}/compare/{Installed}...{Branch}";
        public bool Known => Behind is not null;
    }

    /// <summary>
    /// The commit stamped in at build time by install.sh. "dev" means someone built it by
    /// hand without the stamp, in which case there is nothing to compare against.
    /// </summary>
    public static string Installed =>
        typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] is { Length: > 0 } version
            ? version
            : "dev";

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
        if (installed == "dev")
        {
            return Store(new Result(installed, null, null, null, null,
                "This build was not stamped with a commit, so there is nothing to compare. " +
                "Installs made by install.sh are stamped; a plain \"docker compose build\" is not."));
        }

        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Repository}/commits/{Branch}");

            // GitHub rejects requests with no user agent.
            request.Headers.TryAddWithoutValidation("User-Agent", "LabbyTwo");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return Store(new Result(installed, null, null, null, null,
                    $"GitHub answered {(int)response.StatusCode}. Rate limiting, or no route out from here."));
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;

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

            // The stamp is a short sha, so compare by prefix rather than equality.
            var behind = !sha.StartsWith(installed, StringComparison.OrdinalIgnoreCase);

            return Store(new Result(installed, latest, behind, FirstLine(message), date, null));
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Update check failed");
            return Store(new Result(installed, null, null, null, null,
                ProbeError.Describe(ex, "api.github.com")));
        }
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
