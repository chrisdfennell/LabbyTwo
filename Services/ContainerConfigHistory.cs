using System.Collections.Concurrent;
using System.Security.Cryptography;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Keeps each container's configuration history, from the container lists the change
/// watcher already compares every sweep (see <see cref="ChangeWatcher"/>).
///
/// A container's settings cannot change without it being recreated — Docker has no "edit",
/// only a new container under the same name — so a container whose id is the one last
/// recorded has nothing new to say, and costs nothing: no inspect, no read, no write. Only a
/// container with a new id (or none recorded yet) is inspected, once, and only one whose
/// settings actually differ from the last version is written. What was last recorded for
/// each host is kept in memory after the first look, so a quiet sweep does not even read
/// the database. The one thing this misses is <c>docker update</c>, which changes a restart
/// policy or a memory limit in place; the next recreate picks that up.
///
/// The first time a container is seen its settings are only remembered — a feed entry for
/// each of forty containers on the day this was installed would be noise. After that, a
/// change is written to the feed as "sonarr config changed: env PUID; ports", with what
/// each entry went from and to. Secret-looking values are hashed, never stored (see
/// <see cref="ContainerConfigs.LooksSecret"/>).
/// </summary>
public sealed class ContainerConfigHistory(ContainerConfigStore store, ChangeStore changes, ILogger<ContainerConfigHistory> log)
{
    /// <summary>The change baseline holding the salt secrets are hashed with.</summary>
    public const string SaltKey = "config:salt";

    /// <summary>How long a socket proxy that refused an inspect is left alone before asking again.</summary>
    private static readonly TimeSpan RefusedFor = TimeSpan.FromHours(1);

    /// <summary>Image inspect payloads by endpoint and image id. An image id names content that never changes.</summary>
    private static readonly ConcurrentDictionary<string, string> Images = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Dictionary<string, (long Id, string ContainerId, ContainerConfig? Config)>> _known = [];
    private readonly ConcurrentDictionary<string, DateTimeOffset> _refused = new(StringComparer.Ordinal);
    private byte[]? _salt;

    /// <summary>Reads one container's inspect payload and its image's, or null when the container has gone.</summary>
    public delegate Task<(string Inspect, string? Image)?> Reader(ContainerRow row, CancellationToken ct);

    /// <summary>
    /// One look at one host, reading from Docker. A socket proxy that refuses inspecting
    /// containers is noted and left alone for an hour: the Containers tab already says which
    /// flag it needs, and the history is not worth a warning every sweep.
    /// </summary>
    public async Task<IReadOnlyList<Change>> NoteAsync(
        Connection connection, string endpoint, TimeSpan timeout, IReadOnlyList<ContainerRow> rows, DateTimeOffset at,
        CancellationToken ct)
    {
        if (_refused.TryGetValue(connection.Id, out var until) && DateTimeOffset.UtcNow < until)
            return [];

        try
        {
            return await NoteAsync(connection, rows, at, (row, c) => ReadAsync(endpoint, timeout, row, c), ct);
        }
        catch (DockerProxyDeniedException ex)
        {
            _refused[connection.Id] = DateTimeOffset.UtcNow + RefusedFor;
            log.LogInformation("No config history for {Connection}: {Reason}", connection.Name, ex.Message);
            return [];
        }
    }

    /// <summary>
    /// One look at one host, with the reading passed in — the whole path, from "which
    /// containers are new" to the feed entry, against a real database and no Docker host.
    /// </summary>
    public async Task<IReadOnlyList<Change>> NoteAsync(
        Connection connection, IReadOnlyList<ContainerRow> rows, DateTimeOffset at, Reader read, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_known.TryGetValue(connection.Id, out var known))
            {
                known = new Dictionary<string, (long, string, ContainerConfig?)>(await store.LatestAsync(connection.Id, ct),
                    StringComparer.Ordinal);
                _known[connection.Id] = known;
            }

            var recorded = new List<Change>();
            foreach (var row in rows.OrderBy(r => r.Name, StringComparer.Ordinal))
            {
                if (known.TryGetValue(row.Name, out var last) && last.ContainerId == row.Id)
                    continue;

                (string Inspect, string? Image)? payload;
                try
                {
                    payload = await read(row, ct);
                }
                catch (InvalidOperationException ex) when (ex is not DockerProxyDeniedException)
                {
                    // Removed between the list and now. The next look sees whatever replaced it.
                    continue;
                }
                if (payload is not { } found)
                    continue;

                var config = ContainerConfigs.Parse(found.Inspect, found.Image, await SaltAsync(ct));
                if (last.Config is { } before && before.SameAs(config))
                {
                    // Recreated with the same settings: move the version on to the new
                    // container, so it is not inspected again after a restart.
                    await store.TouchAsync(last.Id, row.Id, ct);
                    known[row.Name] = (last.Id, row.Id, before);
                    continue;
                }

                var id = await store.AddAsync(connection.Id, row.Name, row.Id, at, config, ct);
                known[row.Name] = (id, row.Id, config);

                // Nothing to compare with the first time; and a version read with the image's
                // defaults in (or out) against one read the other way would list every
                // default as a change nobody made.
                if (last.Config is not { } previous || previous.ImageDefaultsRemoved != config.ImageDefaultsRemoved)
                    continue;

                var differences = ContainerConfigs.Diff(previous, config);
                if (differences.Count == 0)
                    continue;

                var change = ChangeFor(connection, row.Name, differences, at);
                try
                {
                    recorded.Add(await changes.RecordAsync(change, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Could not record the change \"{Title}\"", change.Title);
                }
            }
            return recorded;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The feed entry for a changed configuration.</summary>
    public static Change ChangeFor(Connection connection, string container, IReadOnlyList<ConfigDifference> differences, DateTimeOffset at)
    {
        const int shown = 8;
        var detail = string.Join("; ", differences.Take(shown).Select(d => d.Describe()));
        if (differences.Count > shown)
            detail += $"; and {differences.Count - shown} more — the container's config history has the rest";
        return new Change(at, ChangeKinds.Container, ChangeActions.Changed, connection.Id, container,
            $"{container} config changed: {ContainerConfigs.Summary(differences)}", detail + ".");
    }

    /// <summary>Forgets what was last recorded for every host, so the next look reads the database again. For tests.</summary>
    public void Forget()
    {
        _gate.Wait();
        try
        {
            _known.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The salt secrets are hashed with: random, made once per install, and kept with the
    /// change detectors' baselines.
    /// </summary>
    private async Task<byte[]> SaltAsync(CancellationToken ct)
    {
        if (_salt is not null)
            return _salt;

        var stored = await changes.BaselineAsync(SaltKey, ct);
        if (stored is { Length: > 0 })
        {
            try
            {
                return _salt = Convert.FromBase64String(stored);
            }
            catch (FormatException)
            {
                // Unreadable: make a new one. Every hidden value then looks changed once.
            }
        }

        var salt = RandomNumberGenerator.GetBytes(32);
        await changes.SetBaselineAsync(SaltKey, Convert.ToBase64String(salt), ct);
        return _salt = salt;
    }

    private static async Task<(string Inspect, string? Image)?> ReadAsync(
        string endpoint, TimeSpan timeout, ContainerRow row, CancellationToken ct)
    {
        var inspect = await DockerSocket.GetAsync(endpoint, timeout, $"/containers/{Uri.EscapeDataString(row.Id)}/json", ct);

        string? image = null;
        if (row.ImageId.Length > 0)
        {
            var key = endpoint + "|" + row.ImageId;
            if (!Images.TryGetValue(key, out image))
            {
                try
                {
                    image = await DockerSocket.GetAsync(endpoint, timeout, $"/images/{Uri.EscapeDataString(row.ImageId)}/json", ct);
                    if (Images.Count < 500)
                        Images[key] = image;
                }
                catch (InvalidOperationException)
                {
                    // Without IMAGES=1 the image's defaults stay in, and the version says so.
                    image = null;
                }
            }
        }
        return (inspect, image);
    }
}
