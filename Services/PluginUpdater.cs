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

        JsonDocument release;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/tags/{hostVersion}");
            request.Headers.TryAddWithoutValidation("User-Agent", "LabbyTwo");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return Result.Nothing($"No published release for {hostVersion}.");

            release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            return Result.Nothing($"Could not reach GitHub: {ex.GetBaseException().Message}");
        }

        using var _ = release;

        if (!release.RootElement.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return Result.Nothing($"The {hostVersion} release has nothing attached.");
        }

        var updated = new List<string>();

        foreach (var asset in assets.EnumerateArray())
        {
            var file = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";

            // "LabbyTwo.TerminalPlugin-v1.3.6.zip" — the part before the version is the
            // assembly's own name, which is what is on disk.
            var suffix = $"-{hostVersion}.zip";
            if (!file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || url.Length == 0)
                continue;

            var name = file[..^suffix.Length];
            if (!installed.Contains(name))
                continue;   // not installed here — this is not an installer

            try
            {
                await ReplaceAsync(directory, name, url, http, ct);
                updated.Add(name);
            }
            catch (Exception ex)
            {
                // One plugin that will not download must not stop the other twelve.
                log?.LogWarning(ex, "Could not update the {Plugin} plugin", name);
            }
        }

        return new Result(updated, null);
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

            foreach (var source in Directory.GetFiles(unpacked, "*", SearchOption.AllDirectories))
            {
                // Flattened deliberately: the plugin loader scans one folder, and an archive
                // with a stray subdirectory in it would otherwise install a plugin nothing
                // ever looks at.
                var target = Path.Combine(directory, Path.GetFileName(source));
                File.Copy(source, target, overwrite: true);
            }
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
