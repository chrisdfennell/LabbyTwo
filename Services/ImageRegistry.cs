using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>
/// Asks a container registry which image a tag points at today, so the Containers tab can
/// say whether what is running is behind.
///
/// Two ways of asking, because the registries differ in what they make easy:
/// <list type="bullet">
/// <item>Docker Hub has a tag API (<c>hub.docker.com/v2/repositories/…/tags/…</c>) that
/// answers with the digest and when the tag was last pushed, in one unauthenticated call.
/// The date is worth having — "published three days ago" is how you decide whether to
/// wait for a point release — so Hub is asked that way first.</item>
/// <item>Everything else — ghcr.io, lscr.io, quay.io, a registry of your own — speaks the
/// registry v2 API. A <c>HEAD</c> on the tag's manifest answers with
/// <c>Docker-Content-Digest</c>, after the usual dance: the registry says 401 and names a
/// token service in <c>WWW-Authenticate</c>, and that service hands an anonymous pull
/// token to anyone who asks. A HEAD costs nothing against Docker Hub's pull limit either,
/// which is why Hub falls back to it when its tag API says nothing useful.</item>
/// </list>
///
/// The digest wanted is the one Docker records in an image's <c>RepoDigests</c> when it
/// pulls it. For a multi-arch image that is the digest of the index (the manifest list),
/// not of the one platform's manifest inside it — so the Accept header asks for the index
/// types first, and only falls back to a single manifest for an image that has no index.
/// Comparing against the per-platform digest would call every multi-arch image on the
/// host out of date, for ever.
///
/// Registries are shared, rate-limited services that owe a home lab nothing. So: at most
/// <see cref="PerHostConcurrency"/> calls to one host at once; an answer is reused for
/// <see cref="Reuse"/> however many times somebody presses the button; and a 429 stops
/// every further call to that host until it said to come back, rather than hammering it
/// through the rest of the list.
///
/// Nothing here runs because a page was opened. It is called by the "Check for updates"
/// button and, only if somebody switched it on, by <see cref="ContainerUpdateCheckJob"/> —
/// see <see cref="SelfUpdater.StatusAsync"/> for the promise that is keeping.
/// </summary>
public sealed class ImageRegistry(IHttpClientFactory httpFactory, ILogger<ImageRegistry> log)
{
    /// <summary>Calls one registry host may have in flight at once.</summary>
    public const int PerHostConcurrency = 2;

    /// <summary>How long an answer about one tag stands before the registry is asked again.</summary>
    public static readonly TimeSpan Reuse = TimeSpan.FromMinutes(10);

    /// <summary>How long a host that said 429 without a Retry-After is left alone.</summary>
    public static readonly TimeSpan DefaultBackOff = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The manifest types asked for, index first. Docker's manifest list and OCI's image
    /// index are the multi-arch shapes; the two single manifests are for an image published
    /// for one platform only, where that manifest's digest is what RepoDigests records.
    /// </summary>
    public static readonly IReadOnlyList<string> ManifestTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.docker.distribution.manifest.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
    ];

    /// <summary>Where Docker Hub's tag API lives. Settable so a test can stand in for it.</summary>
    public string HubApi { get; set; } = "https://hub.docker.com";

    /// <param name="Digest">What the tag points at now, or null when that could not be found out.</param>
    /// <param name="PushedAt">When the tag was last pushed, where the registry says (Docker Hub does).</param>
    /// <param name="Problem">Why there is no digest, in words for the page.</param>
    public sealed record Published(string? Digest, DateTimeOffset? PushedAt, string? Problem, DateTimeOffset CheckedAt);

    private readonly ConcurrentDictionary<string, Published> _answers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _backOff = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forgets every remembered answer and back-off. For tests.</summary>
    public void Forget()
    {
        _answers.Clear();
        _backOff.Clear();
    }

    /// <summary>
    /// What <paramref name="image"/>'s tag points at now. Never throws for anything the
    /// registry did: a refusal, a missing tag or a network failure comes back as
    /// <see cref="Published.Problem"/>, because one private image must not spoil the list.
    /// </summary>
    public async Task<Published> LatestAsync(ImageRef image, CancellationToken ct = default)
    {
        var key = image.ToString();
        if (_answers.TryGetValue(key, out var known) && DateTimeOffset.UtcNow - known.CheckedAt < Reuse)
            return known;

        if (image.IsPinned)
            return new Published(null, null, "Pinned to a digest, so there is no tag to follow.", DateTimeOffset.UtcNow);

        var host = image.IsDockerHub ? DockerHubKey : image.RegistryHost;
        if (_backOff.TryGetValue(host, out var until) && until > DateTimeOffset.UtcNow)
            return new Published(null, null, RateLimited(image, until), DateTimeOffset.UtcNow);

        var gate = _gates.GetOrAdd(host, _ => new SemaphoreSlim(PerHostConcurrency, PerHostConcurrency));
        await gate.WaitAsync(ct);
        Published answer;
        try
        {
            // A host that started refusing while this call waited its turn is not asked again.
            if (_backOff.TryGetValue(host, out until) && until > DateTimeOffset.UtcNow)
                return new Published(null, null, RateLimited(image, until), DateTimeOffset.UtcNow);

            answer = image.IsDockerHub ? await FromHubAsync(image, ct) : await FromRegistryAsync(image, ct);
        }
        catch (RateLimitedException limited)
        {
            _backOff[host] = limited.Until;
            answer = new Published(null, null, RateLimited(image, limited.Until), DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or IOException
                                       && !ct.IsCancellationRequested)
        {
            log.LogInformation("Could not ask {Registry} about {Image}: {Message}", image.Registry, image, ex.Message);
            answer = new Published(null, null,
                $"Could not reach {Describe(image)}: {ProbeError.Describe(ex, image.RegistryHost)}", DateTimeOffset.UtcNow);
        }
        finally
        {
            gate.Release();
        }

        // A failure is remembered as well, for the same few minutes: pressing the button
        // again straight away should not ask a registry that just refused.
        _answers[key] = answer;
        return answer;
    }

    private const string DockerHubKey = "docker.io";

    private static string Describe(ImageRef image) => image.IsDockerHub ? "Docker Hub" : image.Registry;

    private static string RateLimited(ImageRef image, DateTimeOffset until) =>
        $"{Describe(image)} is rate-limiting requests from this address, so nothing more is asked of it until " +
        $"{until.ToLocalTime():HH:mm}.";

    private sealed class RateLimitedException(DateTimeOffset until) : Exception("Rate limited")
    {
        public DateTimeOffset Until { get; } = until;
    }

    private static DateTimeOffset BackOffFrom(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta && delta > TimeSpan.Zero)
            return DateTimeOffset.UtcNow + delta;
        if (retry?.Date is { } date && date > DateTimeOffset.UtcNow)
            return date;
        return DateTimeOffset.UtcNow + DefaultBackOff;
    }

    private HttpClient Client()
    {
        var http = httpFactory.CreateClient();
        http.Timeout = CallTimeout;
        return http;
    }

    // ---- Docker Hub ---------------------------------------------------------------

    /// <summary>
    /// Hub's tag API. Its top-level <c>digest</c> is the index's for a multi-arch tag — the
    /// per-platform ones are under <c>images</c> — so it compares straight against
    /// RepoDigests. An old tag sometimes has no top-level digest at all; the registry API
    /// is asked then instead of guessing from a platform's.
    /// </summary>
    private async Task<Published> FromHubAsync(ImageRef image, CancellationToken ct)
    {
        var url = $"{HubApi.TrimEnd('/')}/v2/repositories/{image.HubRepository}/tags/{Uri.EscapeDataString(image.Tag)}";
        using var http = Client();
        using var response = await http.GetAsync(url, ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitedException(BackOffFrom(response));

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new Published(null, null,
                $"Docker Hub has no public tag “{image.Tag}” for {image.HubRepository} — a private image, or a tag that " +
                "has since been deleted.", DateTimeOffset.UtcNow);
        }

        if (!response.IsSuccessStatusCode)
            return new Published(null, null, $"Docker Hub answered HTTP {(int)response.StatusCode}.", DateTimeOffset.UtcNow);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        var digest = root.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        DateTimeOffset? pushed = null;
        foreach (var field in new[] { "tag_last_pushed", "last_updated" })
        {
            if (root.TryGetProperty(field, out var at) && at.ValueKind == JsonValueKind.String &&
                DockerTime.Parse(at.GetString()) is { Year: > 1 } when)
            {
                pushed = when;
                break;
            }
        }

        if (digest is { Length: > 0 })
            return new Published(digest, pushed, null, DateTimeOffset.UtcNow);

        var fromRegistry = await FromRegistryAsync(image, ct);
        return fromRegistry with { PushedAt = fromRegistry.PushedAt ?? pushed };
    }

    // ---- registry v2 --------------------------------------------------------------

    /// <summary>
    /// Plain http only for a registry on this machine — Docker itself trusts 127.0.0.0/8 as
    /// an insecure registry by default, and nothing else without being told to.
    /// </summary>
    public static string BaseUrl(string host)
    {
        var name = host.Split(':')[0];
        var loopback = name.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                       (IPAddress.TryParse(name, out var address) && IPAddress.IsLoopback(address));
        return $"{(loopback ? "http" : "https")}://{host}";
    }

    private async Task<Published> FromRegistryAsync(ImageRef image, CancellationToken ct)
    {
        var url = $"{BaseUrl(image.RegistryHost)}/v2/{image.RegistryRepository}/manifests/{Uri.EscapeDataString(image.Tag)}";
        using var http = Client();

        string? token = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await SendManifestAsync(http, HttpMethod.Head, url, token, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized && token is null &&
                Challenge(response) is { } challenge)
            {
                if (!challenge.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
                    return NeedsLogin(image);

                token = await TokenAsync(http, challenge.Parameters, image, ct);
                if (token is null)
                    return NeedsLogin(image);

                // lscr.io answers with a redirect to ghcr.io, and a redirect drops the
                // Authorization header — so the token is taken straight to where the
                // challenge came from, not sent round the redirect to be thrown away.
                if (response.RequestMessage?.RequestUri is { } answeredFrom)
                    url = answeredFrom.ToString();
                continue;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new RateLimitedException(BackOffFrom(response));

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return NeedsLogin(image);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new Published(null, null,
                    $"{Describe(image)} has no tag “{image.Tag}” for {image.RegistryRepository}.", DateTimeOffset.UtcNow);
            }

            if (!response.IsSuccessStatusCode)
                return new Published(null, null, $"{Describe(image)} answered HTTP {(int)response.StatusCode}.", DateTimeOffset.UtcNow);

            if (Digest(response) is { } digest)
                return new Published(digest, null, null, DateTimeOffset.UtcNow);

            // A registry that leaves the header off a HEAD — the spec says SHOULD, not MUST.
            // The digest is by definition the hash of the manifest's bytes, so fetch them.
            using var full = await SendManifestAsync(http, HttpMethod.Get, url, token, ct);
            if (!full.IsSuccessStatusCode)
                return new Published(null, null, $"{Describe(image)} answered HTTP {(int)full.StatusCode}.", DateTimeOffset.UtcNow);
            if (Digest(full) is { } header)
                return new Published(header, null, null, DateTimeOffset.UtcNow);

            var bytes = await full.Content.ReadAsByteArrayAsync(ct);
            return new Published("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), null, null, DateTimeOffset.UtcNow);
        }

        return NeedsLogin(image);
    }

    private static Published NeedsLogin(ImageRef image) =>
        new(null, null,
            $"{Describe(image)} wants a login to read {image.RegistryRepository}, so it is probably private. " +
            "LabbyTwo only asks anonymously.", DateTimeOffset.UtcNow);

    private static async Task<HttpResponseMessage> SendManifestAsync(
        HttpClient http, HttpMethod method, string url, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        foreach (var type in ManifestTypes)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(type));
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static string? Digest(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Docker-Content-Digest", out var values) &&
        values.FirstOrDefault() is { Length: > 0 } digest
            ? digest.Trim()
            : null;

    /// <param name="Parameters">realm, service and scope, as the challenge gave them.</param>
    public sealed record AuthChallenge(string Scheme, IReadOnlyDictionary<string, string> Parameters);

    private static AuthChallenge? Challenge(HttpResponseMessage response) =>
        response.Headers.WwwAuthenticate.FirstOrDefault() is { } header
            ? ParseChallenge($"{header.Scheme} {header.Parameter}")
            : null;

    /// <summary>
    /// <c>Bearer realm="https://ghcr.io/token",service="ghcr.io",scope="repository:x/y:pull"</c>
    /// pulled apart. Quoted values can hold commas (a scope for two repositories does), so
    /// this reads name="value" pairs rather than splitting on commas.
    /// </summary>
    public static AuthChallenge? ParseChallenge(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return null;

        var text = header.Trim();
        var space = text.IndexOf(' ');
        var scheme = space < 0 ? text : text[..space];
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (space > 0)
        {
            foreach (Match pair in ChallengePair.Matches(text[(space + 1)..]))
                parameters[pair.Groups[1].Value] = pair.Groups[2].Success ? pair.Groups[2].Value : pair.Groups[3].Value;
        }
        return new AuthChallenge(scheme, parameters);
    }

    private static readonly Regex ChallengePair =
        new(@"([A-Za-z_][A-Za-z0-9_-]*)\s*=\s*(?:""([^""]*)""|([^,\s]*))", RegexOptions.CultureInvariant);

    /// <summary>
    /// An anonymous pull token from the service the challenge named. Null when the service
    /// will not give one without credentials — a private image, said as such by the caller.
    /// </summary>
    private static async Task<string?> TokenAsync(
        HttpClient http, IReadOnlyDictionary<string, string> challenge, ImageRef image, CancellationToken ct)
    {
        if (!challenge.TryGetValue("realm", out var realm) || !Uri.TryCreate(realm, UriKind.Absolute, out var realmUri) ||
            realmUri.Scheme is not ("https" or "http"))
            return null;

        var query = new List<string>();
        if (challenge.TryGetValue("service", out var service))
            query.Add("service=" + Uri.EscapeDataString(service));
        var scope = challenge.TryGetValue("scope", out var asked) && asked.Length > 0
            ? asked
            : $"repository:{image.RegistryRepository}:pull";
        query.Add("scope=" + Uri.EscapeDataString(scope));

        var url = realm + (realm.Contains('?') ? "&" : "?") + string.Join('&', query);
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitedException(BackOffFrom(response));
        if (!response.IsSuccessStatusCode)
            return null;

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        foreach (var field in new[] { "token", "access_token" })
        {
            if (document.RootElement.TryGetProperty(field, out var value) && value.GetString() is { Length: > 0 } token)
                return token;
        }
        return null;
    }
}
