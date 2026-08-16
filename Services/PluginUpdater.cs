using System.IO.Compression;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>
/// Brings the bundled plugins up to the version of LabbyTwo that is running.
///
/// This exists because of a failure that is already documented two files away: a plugin DLL
/// compiled against a different host *half*-loads. Discovery keeps the types that still
/// resolve and drops the rest, so a tab kind can appear in the picker and then throw when
/// somebody opens it. Every host update creates that situation for every installed plugin,
/// and the only fix was to notice the warning on the Settings page and go and unzip thirteen
/// files by hand.
///
/// **It runs before the modules are discovered, not after.** Updating a DLL that is already
/// loaded does nothing until the next restart, so an update that ran as a background job
/// would leave the plugins stale for the entire life of the container it ran in — with
/// Watchtower restarting things unattended, that is every container. Running it in the
/// startup path costs a few seconds once and means one restart is enough.
///
/// **Only plugins that match an asset in the official release are touched.** A third-party
/// plugin has no asset named after it, so it is left exactly alone without needing a list of
/// what is allowed to be replaced. Nothing here can be pointed at another host.
/// </summary>
public static class PluginUpdater
{
    /// <summary>Where the bundled plugins are published — the same repository the app updates from.</summary>
    public const string Repository = "chrisdfennell/LabbyTwo";

    /// <param name="Updated">Assembly names brought up to date.</param>
    /// <param name="Reason">Why nothing happened, when nothing did. Null on success.</param>
    public sealed record Result(IReadOnlyList<string> Updated, string? Reason)
    {
        public static Result Nothing(string reason) => new([], reason);
    }

    /// <summary>The setting that turns this on. Off unless somebody asks for it.</summary>
    public const string EnabledKey = "plugins_auto_update";

    /// <summary>
    /// Replaces every plugin in <paramref name="directory"/> that was built for a different
    /// version with the one published for <paramref name="hostVersion"/>.
    ///
    /// Never throws. This runs in the startup path, and a dashboard that will not boot
    /// because GitHub was slow is a far worse failure than one with a stale plugin in it.
    /// </summary>
    public static async Task<Result> UpdateAsync(
        string directory,
        string hostVersion,
        HttpClient http,
        ILogger? log = null,
        CancellationToken ct = default)
    {
        // A build nobody stamped — every local dotnet run — has nothing to match against, and
        // guessing would replace working plugins with whichever release happened to be newest.
        if (hostVersion.Length == 0)
            return Result.Nothing("This build is not stamped with a version, so there is nothing to match.");

        if (!Directory.Exists(directory))
            return Result.Nothing("No plugins folder.");

        var installed = Directory.GetFiles(directory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is { Length: > 0 })
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (installed.Count == 0)
            return Result.Nothing("No plugins installed.");

        IReadOnlyList<Available> published;
        try
        {
            published = await AvailableAsync(hostVersion, http, ct);
        }
        catch (Exception ex)
        {
            return Result.Nothing(ex.GetBaseException().Message);
        }

        var updated = new List<string>();

        foreach (var plugin in published)
        {
            // Only what is already here. This is an updater; installing something nobody
            // asked for is what the install button is for, and doing it automatically would
            // turn switching this on into installing all fourteen.
            if (!installed.Contains(plugin.Name))
                continue;

            try
            {
                await ReplaceAsync(directory, plugin.Name, plugin.Url, http, ct);
                updated.Add(plugin.Name);
            }
            catch (Exception ex)
            {
                // One plugin that will not download must not stop the other twelve.
                log?.LogWarning(ex, "Could not update the {Plugin} plugin", plugin.Name);
            }
        }

        return new Result(updated, null);
    }

    /// <param name="Name">The assembly's own name, which is also what is on disk.</param>
    /// <param name="Bytes">The archive's size, so a list can say what a click will fetch.</param>
    public sealed record Available(string Name, string Url, long Bytes);

    /// <summary>
    /// Every plugin published for this version of LabbyTwo.
    ///
    /// The release is the catalogue: each bundled plugin is attached as
    /// "LabbyTwo.TerminalPlugin-v1.3.9.zip", so the part before the version is the assembly
    /// name and the list needs no index file to maintain alongside it. A plugin published
    /// for a *different* version is not offered at all — installing one would reproduce
    /// exactly the half-loading this whole area exists to prevent.
    /// </summary>
    public static async Task<IReadOnlyList<Available>> AvailableAsync(
        string hostVersion, HttpClient http, CancellationToken ct = default)
    {
        if (hostVersion.Length == 0)
            throw new InvalidOperationException(
                "This build is not stamped with a version, so there is no release to list.");

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/tags/{hostVersion}");
        request.Headers.TryAddWithoutValidation("User-Agent", "LabbyTwo");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"No published release for {hostVersion}.");

        using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        if (!release.RootElement.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var suffix = $"-{hostVersion}.zip";
        var found = new List<Available>();

        foreach (var asset in assets.EnumerateArray())
        {
            var file = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;

            if (!file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || url.Length == 0)
                continue;

            found.Add(new Available(file[..^suffix.Length], url, size));
        }

        return [.. found.OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Installs one by name, whether or not it is already here.
    ///
    /// The same download and unpack as an update — the only difference is that this one is
    /// asked for rather than inferred, which is the entire reason the updater refuses to do
    /// it on its own.
    /// </summary>
    public static async Task InstallAsync(
        string directory, string hostVersion, string name, HttpClient http, CancellationToken ct = default)
    {
        var plugin = (await AvailableAsync(hostVersion, http, ct))
            .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Nothing called “{name}” is published for {hostVersion}.");

        Directory.CreateDirectory(directory);
        await ReplaceAsync(directory, plugin.Name, plugin.Url, http, ct);
    }

    /// <summary>
    /// Takes one out. The files it brought with it go too, except any a still-installed
    /// plugin also uses.
    /// </summary>
    public static IReadOnlyList<string> Remove(string directory, string name)
    {
        var removed = new List<string>();

        foreach (var file in PluginManifest.RemovableFor(directory, name))
        {
            try
            {
                var path = Path.Combine(directory, file);
                if (!File.Exists(path))
                    continue;

                File.Delete(path);
                removed.Add(file);
            }
            catch (IOException)
            {
                // Windows holds a loaded assembly open, so a plugin cannot always delete
                // itself while running. What could go, went; the rest goes on the restart
                // that is needed anyway.
            }
        }

        PluginManifest.Forget(directory, name);
        return removed;
    }

    /// <summary>
    /// Downloads one and unpacks it over the top.
    ///
    /// The whole archive, not just the DLL named after it: some plugins carry dependencies
    /// the host does not ship — the terminal one needs SSH.NET — and replacing only the
    /// plugin would leave it beside a stale copy of its own library.
    /// </summary>
    private static async Task ReplaceAsync(
        string directory, string name, string url, HttpClient http, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "LabbyTwo");

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        // Staged in a temp folder and only moved into place once the whole archive has
        // unpacked. Writing straight into the plugins folder means a download that fails
        // halfway leaves a working plugin replaced by half of a new one.
        var staging = Path.Combine(Path.GetTempPath(), $"labby-plugin-{Guid.NewGuid():n}");
        Directory.CreateDirectory(staging);

        try
        {
            var archive = Path.Combine(staging, "plugin.zip");
            await using (var file = File.Create(archive))
                await response.Content.CopyToAsync(file, ct);

            var unpacked = Path.Combine(staging, "unpacked");
            ZipFile.ExtractToDirectory(archive, unpacked);

            var written = new List<string>();

            foreach (var source in Directory.GetFiles(unpacked, "*", SearchOption.AllDirectories))
            {
                // Flattened deliberately: the plugin loader scans one folder, and an archive
                // with a stray subdirectory in it would otherwise install a plugin nothing
                // ever looks at.
                var leaf = Path.GetFileName(source);
                File.Copy(source, Path.Combine(directory, leaf), overwrite: true);
                written.Add(leaf);
            }

            PluginManifest.Record(directory, name, written);
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing an update over.
            }
        }
    }
}

/// <summary>
/// What each installed archive put in the plugins folder.
///
/// Kept because removal cannot be guessed at. A plugin's zip is not one DLL — the terminal
/// one carries SSH.NET beside it — so "delete the file named after it" leaves the library
/// behind, and "delete everything that arrived with it" would take a shared dependency out
/// from under a plugin still using it. Recording what each one wrote makes both answerable.
///
/// A plain JSON file in the plugins folder rather than a table: it describes that folder, it
/// has to survive a container being replaced, and it is read before there is a database.
/// Losing it costs nothing worse than removal falling back to the obvious file.
/// </summary>
public static class PluginManifest
{
    private const string FileName = ".labby-plugins.json";

    public static Dictionary<string, List<string>> Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
            return [];

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    public static void Record(string directory, string name, IReadOnlyList<string> files)
    {
        try
        {
            var all = Read(directory);
            all[name] = [.. files];
            File.WriteAllText(
                Path.Combine(directory, FileName),
                JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // A manifest that cannot be written is not worth failing an install over — the
            // plugin is already on disk and working. Removal falls back to the obvious file.
        }
    }

    /// <summary>
    /// The files that belong to this plugin and to nothing else. A dependency shared with
    /// another installed plugin is left where it is: an orphaned DLL is inert, and taking one
    /// out from under a plugin that is still using it is not.
    /// </summary>
    public static IReadOnlyList<string> RemovableFor(string directory, string name)
    {
        var all = Read(directory);

        if (!all.TryGetValue(name, out var mine))
        {
            // Nothing recorded — installed by hand, or before the manifest existed. The file
            // named after it is the one thing that is certainly its.
            var guess = name + ".dll";
            return File.Exists(Path.Combine(directory, guess)) ? [guess] : [];
        }

        var others = all
            .Where(entry => !string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(entry => entry.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. mine.Where(file => !others.Contains(file))];
    }

    public static void Forget(string directory, string name)
    {
        try
        {
            var all = Read(directory);
            if (!all.Remove(name))
                return;

            File.WriteAllText(
                Path.Combine(directory, FileName),
                JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Reads the one setting the plugin updater needs, before there is a service provider to
/// ask.
///
/// The update has to happen before <c>AddModules</c>, which is before the host is built, so
/// <see cref="Storage.AppSettingsStore"/> does not exist yet. Rather than build half a
/// container to read one boolean, this opens the database directly — and treats every
/// failure as "off", because a dashboard that will not start because it could not read a
/// preference about plugins is a much worse outcome than one that skipped an update.
/// </summary>
public static class PluginAutoUpdate
{
    public static async Task<bool> EnabledAsync(Storage.LabbyOptions options, IHostEnvironment environment)
    {
        try
        {
            var path = Path.GetFullPath(options.DatabasePath, environment.ContentRootPath);
            if (!File.Exists(path))
                return false;   // first run: there are no plugins yet either

            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                }.ToString());

            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM app_settings WHERE key = $key";
            command.Parameters.AddWithValue("$key", PluginUpdater.EnabledKey);

            return await command.ExecuteScalarAsync() is string value
                && bool.TryParse(value, out var on)
                && on;
        }
        catch
        {
            return false;
        }
    }
}
