using System.Collections.Concurrent;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>Where one container stands against its registry.</summary>
public enum UpdateState
{
    /// <summary>Nobody has asked the registry yet. Everything local — age, digests — is known.</summary>
    Unchecked,

    /// <summary>The digest running is the one the tag points at now.</summary>
    Current,

    /// <summary>The tag points at a digest this container is not running.</summary>
    Behind,

    /// <summary>The image has no repo digest: it was built on this host, not pulled.</summary>
    LocalBuild,

    /// <summary>Created from <c>image@sha256:…</c>, which is a promise never to change.</summary>
    Pinned,

    /// <summary>Something stopped the comparison — a private image, a registry that refused.</summary>
    Unknown,

    /// <summary>An update was started from here and has not been checked since.</summary>
    Started,
}

/// <summary>What <c>GET /images/{id}/json</c> says that the review needs.</summary>
/// <param name="Created">When the image was built — not pulled — which is how old the software in it is.</param>
/// <param name="RepoDigests">"repo@sha256:…" for every registry it was pulled from; empty for a local build.</param>
public sealed record ImageFacts(DateTimeOffset? Created, IReadOnlyList<string> RepoDigests)
{
    /// <summary>Just the digests, repository taken off: content addresses are the same whatever repo they came from.</summary>
    public IReadOnlyList<string> Digests =>
    [
        .. RepoDigests.Select(d => d.Contains('@') ? d[(d.IndexOf('@') + 1)..] : "").Where(d => d.Length > 0).Distinct(),
    ];
}

/// <summary>One container's line in the review.</summary>
/// <param name="Reference">The image reference it was created from, as written in the compose file.</param>
/// <param name="ImageCreated">When the running image was built.</param>
/// <param name="Published">When the tag was last pushed, where the registry says.</param>
/// <param name="Running">The digest running (the first, where there are several).</param>
/// <param name="Latest">The digest the tag points at now.</param>
/// <param name="Note">Why it could not be compared, or anything else worth a tooltip.</param>
/// <param name="Excluded">Labelled <c>com.centurylinklabs.watchtower.enable=false</c>.</param>
public sealed record ContainerUpdate(
    string Id,
    string Name,
    string Reference,
    UpdateState State,
    DateTimeOffset? ImageCreated = null,
    DateTimeOffset? Published = null,
    string? Running = null,
    string? Latest = null,
    string? Note = null,
    bool Excluded = false)
{
    public bool IsBehind => State == UpdateState.Behind;

    /// <summary>Whether this page will offer to update it: behind, and not labelled to be left alone.</summary>
    public bool CanUpdate => IsBehind && !Excluded;

    /// <summary>
    /// What Watchtower's <c>/v1/update?image=</c> compares against: the container's image
    /// name cut at its first colon. That is Watchtower's own rule, copied exactly — including
    /// what it does to a registry with a port — because a name that differs from the one it
    /// computes matches nothing and updates nothing, silently.
    /// </summary>
    public string WatchtowerImage => Reference.Split(':')[0];
}

/// <summary>Every container on one Docker connection, as of one check.</summary>
/// <param name="Error">Why there is no list at all — Docker unreachable, a proxy refusing.</param>
public sealed record UpdateReview(
    string ConnectionId,
    string ConnectionName,
    DateTimeOffset CheckedAt,
    IReadOnlyList<ContainerUpdate> Containers,
    string? Error = null)
{
    public IReadOnlyList<ContainerUpdate> Behind => [.. Containers.Where(c => c.IsBehind).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];

    public int Count(UpdateState state) => Containers.Count(c => c.State == state);

    public ContainerUpdate? For(string containerId) => Containers.FirstOrDefault(c => c.Id == containerId);

    /// <summary>"3 behind, 18 up to date, 2 built here, 1 could not be checked" — what the page and Settings say.</summary>
    public string Summary
    {
        get
        {
            if (Error is { Length: > 0 })
                return Error;

            var parts = new List<string>
            {
                Count(UpdateState.Behind) is var behind and > 0 ? $"{behind} behind" : "nothing behind",
            };
            if (Count(UpdateState.Current) is var current and > 0)
                parts.Add($"{current} up to date");
            if (Count(UpdateState.Started) is var started and > 0)
                parts.Add($"{started} updating");
            if (Count(UpdateState.LocalBuild) is var local and > 0)
                parts.Add($"{local} built here");
            if (Count(UpdateState.Pinned) is var pinned and > 0)
                parts.Add($"{pinned} pinned to a digest");
            if (Count(UpdateState.Unknown) is var unknown and > 0)
                parts.Add($"{unknown} could not be checked");
            return string.Join(", ", parts);
        }
    }
}

/// <summary>
/// Whether each container's registry has published something newer than what it runs.
///
/// The comparison is digests, not tags or dates: the running image's <c>RepoDigests</c>
/// — what Docker recorded when it pulled — against what the tag points at in the registry
/// now (<see cref="ImageRegistry"/>). Equal means current; different means somebody pushed.
/// A local build has no RepoDigests, and is said to be one rather than guessed about.
///
/// Two halves, kept apart on purpose. The local half — which image each container runs,
/// when that image was built, whether it was built here — asks only Docker, so the
/// Containers tab can show image ages whenever it likes. The registry half runs only when
/// somebody presses "Check for updates" or has switched on the schedule
/// (<see cref="ContainerUpdateCheckJob"/>, off by default): LabbyTwo does not phone home
/// just because a page was opened, and that includes other people's registries.
///
/// Results are kept in memory per Docker connection, so the tab, Settings and a runbook's
/// <c>{{updates}}</c> all show the last check without making another. A restart forgets
/// them, which costs one press of the button.
/// </summary>
public sealed class ContainerUpdates(ImageRegistry registry, ConfigStore config, ILogger<ContainerUpdates> log)
{
    /// <summary>The label Watchtower reads to leave a container alone. This page honours it too.</summary>
    public const string ExcludeLabel = "com.centurylinklabs.watchtower.enable";

    /// <summary>App setting: hours between scheduled checks, or 0 (the default) for never.</summary>
    public const string ScheduleKey = "container_updates_hours";

    /// <summary>The schedules Settings offers. Nothing faster than six hours: images are not published that often.</summary>
    public static readonly IReadOnlyList<(int Hours, string Label)> Schedules =
    [
        (0, "Only when I press the button"),
        (6, "Every 6 hours"),
        (24, "Once a day"),
        (168, "Once a week"),
    ];

    private static readonly ConcurrentDictionary<string, ImageFacts> FactsCache = new();

    private readonly ConcurrentDictionary<string, UpdateReview> _reviews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<UpdateReview>> _running = new(StringComparer.Ordinal);

    /// <summary>Raised after a check or an update changes what is known, so open pages redraw.</summary>
    public event Action? Changed;

    /// <summary>The last review of one connection, or null if it has never been checked.</summary>
    public UpdateReview? Last(string connectionId) =>
        _reviews.TryGetValue(connectionId, out var review) ? review : null;

    public IReadOnlyList<UpdateReview> All =>
        [.. _reviews.Values.OrderBy(r => r.ConnectionName, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// How long ago, in the unit an image's age is thought of in: "3 months", "12 days",
    /// "2 years". Hours would be false precision for software, and <see cref="Ago"/>'s
    /// "412d 3h" is unreadable at that scale. Written to follow "image … old" or
    /// "published … ago".
    /// </summary>
    public static string Age(DateTimeOffset when, DateTimeOffset? now = null)
    {
        var days = ((now ?? DateTimeOffset.UtcNow) - when).TotalDays;
        return days switch
        {
            < 1 => "less than a day",
            < 2 => "1 day",
            < 60 => $"{(int)days} days",
            < 730 => $"{(int)(days / 30.44)} months",
            _ => $"{(int)(days / 365.25)} years",
        };
    }

    public static bool IsExcluded(IReadOnlyDictionary<string, string> labels) =>
        labels.TryGetValue(ExcludeLabel, out var value) && value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);

    // ---- checking -----------------------------------------------------------------

    /// <summary>
    /// Checks one Docker connection against the registries its images came from. Two
    /// presses at once — two people, or a person and the schedule — share one check.
    /// Runs on the pool, whatever thread asks, and never throws for anything Docker or a
    /// registry did: that is in the review.
    /// </summary>
    public Task<UpdateReview> CheckAsync(Connection docker, CancellationToken ct = default)
    {
        Task<UpdateReview> check;
        lock (_running)
        {
            if (!_running.TryGetValue(docker.Id, out check!))
            {
                var endpoint = docker.Settings.Get("endpoint", DockerSocket.EnvironmentEndpoint);
                var timeout = TimeSpan.FromSeconds(Math.Clamp(docker.Settings.GetInt("timeout", 10), 1, 120));
                check = Task.Run(() => RunCheckAsync(docker.Id, docker.Name, endpoint, timeout));
                _running[docker.Id] = check;
            }
        }
        return check.WaitAsync(ct);
    }

    /// <summary>Whether a check of this connection is running now, for the button to say so.</summary>
    public bool IsChecking(string connectionId)
    {
        lock (_running)
            return _running.ContainsKey(connectionId);
    }

    private async Task<UpdateReview> RunCheckAsync(string id, string name, string endpoint, TimeSpan timeout)
    {
        try
        {
            var review = await ReviewAsync(id, name, endpoint, timeout, registry, CancellationToken.None);
            _reviews[id] = review;
            log.LogInformation("Checked {Connection} for newer images: {Summary}", name, review.Summary);
            return review;
        }
        finally
        {
            lock (_running)
                _running.Remove(id);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Every enabled Docker connection whose last check is older than <paramref name="every"/>,
    /// or that has none — what the schedule runs. Reads the connection list, so from a page
    /// it belongs on the pool.
    /// </summary>
    public async Task<IReadOnlyList<UpdateReview>> CheckDueAsync(TimeSpan every, CancellationToken ct = default)
    {
        var connections = await config.ConnectionsAsync();
        var due = connections
            .Where(c => c.Enabled && c.Provider == "docker")
            .Where(c => Last(c.Id) is not { } last || DateTimeOffset.UtcNow - last.CheckedAt >= every)
            .ToList();

        var reviews = new List<UpdateReview>();
        foreach (var docker in due)
            reviews.Add(await CheckAsync(docker, ct));
        return reviews;
    }

    /// <summary>
    /// The review itself: list, local facts, one registry question per distinct image
    /// reference (twenty containers on <c>linuxserver/…:latest</c> images are twenty
    /// questions, but three containers on the same postgres tag are one), then classify.
    /// </summary>
    public static async Task<UpdateReview> ReviewAsync(
        string connectionId, string connectionName, string endpoint, TimeSpan timeout, ImageRegistry? registry,
        CancellationToken ct)
    {
        IReadOnlyList<ContainerRow> rows;
        IReadOnlyDictionary<string, LocalImage> local;
        try
        {
            rows = await DockerContainers.ListAsync(endpoint, timeout, ct);
            local = await LocalAsync(endpoint, timeout, rows, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new UpdateReview(connectionId, connectionName, DateTimeOffset.UtcNow, [],
                DockerContainers.Explain(ex, endpoint));
        }

        var answers = new Dictionary<string, ImageRegistry.Published>(StringComparer.OrdinalIgnoreCase);
        if (registry is not null)
        {
            var asks = local.Values
                .Where(l => NeedsRegistry(l))
                .Select(l => ImageRef.Parse(l.Reference))
                .DistinctBy(image => image.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var published = await Task.WhenAll(asks.Select(image => registry.LatestAsync(image, ct)));
            for (var i = 0; i < asks.Count; i++)
                answers[asks[i].ToString()] = published[i];
        }

        var containers = rows
            .Select(row =>
            {
                var facts = local.TryGetValue(row.Id, out var l) ? l : new LocalImage(row.Image, null, null);
                ImageRegistry.Published? answer = registry is null
                    ? null
                    : answers.TryGetValue(ImageRef.Parse(facts.Reference).ToString(), out var a) ? a : null;
                return Classify(row, facts, answer, checkedRegistry: registry is not null);
            })
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new UpdateReview(connectionId, connectionName, DateTimeOffset.UtcNow, containers);
    }

    private static bool NeedsRegistry(LocalImage local) =>
        local.Facts is { } facts && facts.Digests.Count > 0 && !ImageRef.Parse(local.Reference).IsPinned &&
        !IsImageId(local.Reference);

    private static bool IsImageId(string reference) =>
        reference.StartsWith("sha256:", StringComparison.Ordinal) ||
        System.Text.RegularExpressions.Regex.IsMatch(reference, "^[0-9a-f]{12,64}$");

    /// <summary>
    /// Marks containers as being updated, so the page stops offering the button and the
    /// summary stops counting them as behind until the next check says what happened.
    /// </summary>
    public void MarkStarted(string connectionId, IReadOnlyCollection<string> containerIds)
    {
        if (!_reviews.TryGetValue(connectionId, out var review))
            return;

        _reviews[connectionId] = review with
        {
            Containers =
            [
                .. review.Containers.Select(c => containerIds.Contains(c.Id)
                    ? c with { State = UpdateState.Started, Note = "Update started from here. Check again to see how it went." }
                    : c),
            ],
        };
        Changed?.Invoke();
    }

    /// <summary>Forgets every review. For tests.</summary>
    public void Forget() => _reviews.Clear();

    // ---- the local half -----------------------------------------------------------

    /// <param name="Reference">The reference the container was created from.</param>
    /// <param name="Facts">The running image's details, or null when Docker would not say.</param>
    /// <param name="Problem">Why Facts is null.</param>
    public sealed record LocalImage(string Reference, ImageFacts? Facts, string? Problem);

    /// <summary>
    /// What Docker alone can say about each container's image. One inspect per distinct
    /// image — remembered for good, since an image id names content that cannot change —
    /// plus an inspect for any container whose tag has moved on, because the list then shows
    /// only an id and only the inspect still says which reference it was created from.
    ///
    /// A socket proxy refusing images is thrown, not swallowed: the fix is a flag
    /// (<c>IMAGES=1</c>), and a page of "could not be checked" would hide it.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, LocalImage>> LocalAsync(
        string endpoint, TimeSpan timeout, IReadOnlyList<ContainerRow> rows, CancellationToken ct)
    {
        var result = new Dictionary<string, LocalImage>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var reference = row.Image;
            if (row.ImageIsId)
            {
                try
                {
                    var details = await DockerContainers.InspectAsync(endpoint, timeout, row.Id, ct);
                    if (details.Image.Length > 0)
                        reference = details.Image;
                }
                catch (InvalidOperationException ex) when (ex is not DockerProxyDeniedException)
                {
                    // Gone between the list and now. The id is all there is to show.
                }
            }

            if (row.ImageId.Length == 0)
            {
                result[row.Id] = new LocalImage(reference, null, "Docker did not say which image it runs.");
                continue;
            }

            try
            {
                result[row.Id] = new LocalImage(reference, await FactsAsync(endpoint, timeout, row.ImageId, ct), null);
            }
            catch (InvalidOperationException ex) when (ex is not DockerProxyDeniedException)
            {
                result[row.Id] = new LocalImage(reference, null, $"Docker would not describe its image: {ex.Message}");
            }
        }
        return result;
    }

    private static async Task<ImageFacts> FactsAsync(string endpoint, TimeSpan timeout, string imageId, CancellationToken ct)
    {
        var key = endpoint + "|" + imageId;
        if (FactsCache.TryGetValue(key, out var known))
            return known;

        var payload = await DockerSocket.GetAsync(endpoint, timeout, $"/images/{Uri.EscapeDataString(imageId)}/json", ct);
        var facts = ParseFacts(payload);
        FactsCache[key] = facts;
        return facts;
    }

    public static ImageFacts ParseFacts(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var digests = new List<string>();
        if (root.TryGetProperty("RepoDigests", out var list) && list.ValueKind == JsonValueKind.Array)
            digests.AddRange(list.EnumerateArray().Select(d => d.GetString() ?? "").Where(d => d.Contains('@')));

        var created = root.TryGetProperty("Created", out var at) && at.ValueKind == JsonValueKind.String &&
                      DockerTime.Parse(at.GetString()) is { Year: > 1 } when
            ? when
            : (DateTimeOffset?)null;

        return new ImageFacts(created, digests);
    }

    // ---- classifying --------------------------------------------------------------

    /// <summary>
    /// One container's verdict. Pure, so every case — a multi-arch image, a local build, a
    /// pin, a registry that refused — is pinned by a test rather than by a real registry.
    /// </summary>
    /// <param name="checkedRegistry">False for the local-only view: nothing was asked, so nothing is "unknown".</param>
    public static ContainerUpdate Classify(
        ContainerRow row, LocalImage local, ImageRegistry.Published? published, bool checkedRegistry = true)
    {
        var excluded = IsExcluded(row.Labels);
        var facts = local.Facts;
        var running = facts?.Digests.FirstOrDefault();
        var line = new ContainerUpdate(row.Id, row.Name, local.Reference, UpdateState.Unchecked,
            facts?.Created, Running: running, Excluded: excluded,
            Note: excluded ? "Labelled com.centurylinklabs.watchtower.enable=false, so it is left alone." : null);

        if (facts is null)
            return line with { State = UpdateState.Unknown, Note = local.Problem ?? "Docker would not describe its image." };

        if (ImageRef.Parse(local.Reference).IsPinned)
        {
            return line with
            {
                State = UpdateState.Pinned,
                Note = "Created from an image pinned by digest, which never changes. Edit the digest to update it.",
            };
        }

        if (facts.Digests.Count == 0)
        {
            return line with
            {
                State = UpdateState.LocalBuild,
                Note = "Built on this host rather than pulled, so there is no published image to compare it with.",
            };
        }

        if (IsImageId(local.Reference))
        {
            return line with
            {
                State = UpdateState.Unknown,
                Note = "Docker no longer says which image reference this was created from, so there is no tag to ask about.",
            };
        }

        if (!checkedRegistry)
            return line;

        if (published?.Digest is not { Length: > 0 } latest)
            return line with { State = UpdateState.Unknown, Note = published?.Problem ?? "The registry was not asked." };

        var current = facts.Digests.Contains(latest, StringComparer.OrdinalIgnoreCase);
        return line with
        {
            State = current ? UpdateState.Current : UpdateState.Behind,
            Latest = latest,
            Published = published.PushedAt,
            Note = excluded ? line.Note : null,
        };
    }
}

/// <summary>
/// Checks every Docker connection for newer images on a schedule — only if somebody chose
/// one in Settings → Updates. Off by default, for the same reason the check is a button:
/// LabbyTwo does not contact anything on the internet until it is asked to.
///
/// Wakes every quarter of an hour and usually does nothing; the interval that matters is
/// the setting, measured from each connection's last check, so a press of the button
/// counts and a restart does not make it check twice.
/// </summary>
public sealed class ContainerUpdateCheckJob(
    ContainerUpdates updates, AppSettingsStore settings, ILogger<ContainerUpdateCheckJob> log) : IBackgroundJob
{
    public string Name => "container-updates";

    public TimeSpan Interval => TimeSpan.FromMinutes(15);

    public async Task RunAsync(CancellationToken ct)
    {
        var hours = (await settings.AllAsync(ct)).GetInt(ContainerUpdates.ScheduleKey, 0);
        if (hours <= 0)
            return;

        var reviews = await updates.CheckDueAsync(TimeSpan.FromHours(hours), ct);
        if (reviews.Count > 0)
        {
            log.LogInformation("Scheduled container update check: {Summary}",
                string.Join("; ", reviews.Select(r => $"{r.ConnectionName}: {r.Summary}")));
        }
    }
}
