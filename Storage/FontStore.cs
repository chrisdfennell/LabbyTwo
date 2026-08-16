using Microsoft.Extensions.Options;

namespace LabbyTwo.Storage;

/// <summary>
/// An uploaded typeface, kept beside the database.
///
/// In the data volume rather than in wwwroot on purpose: wwwroot is part of the image and is
/// replaced on every update, so a font left there would vanish the first time Watchtower
/// pulled a new version. Here it is backed up by the same "copy the volume" the README
/// already promises, and survives updates like everything else somebody configured.
/// </summary>
public sealed class FontStore
{
    private readonly string _directory;

    public FontStore(IOptions<LabbyOptions> options, IHostEnvironment environment)
    {
        var database = Path.GetFullPath(options.Value.DatabasePath, environment.ContentRootPath);
        _directory = Path.Combine(Path.GetDirectoryName(database) ?? ".", "fonts");
    }

    /// <summary>Where the files live, for the static file middleware that serves them.</summary>
    public string Directory
    {
        get
        {
            System.IO.Directory.CreateDirectory(_directory);
            return _directory;
        }
    }

    /// <summary>The URL prefix these are served from.</summary>
    public const string Route = "/fonts";

    /// <summary>
    /// What a browser will actually load. Anything else is either not a font or is a format
    /// no current browser reads, and accepting it would produce a page that silently renders
    /// in the fallback with nothing to say why.
    /// </summary>
    public static readonly string[] Extensions = [".woff2", ".woff", ".ttf", ".otf"];

    /// <summary>
    /// Generous for a font and mean for an upload box. A woff2 of a full family is well under
    /// a megabyte; anything at this size is a mistake or a probe.
    /// </summary>
    public const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Stores one, replacing whatever was there. Returns the stored name.
    ///
    /// The name is rebuilt from scratch rather than sanitised, because sanitising a filename
    /// is a game you lose eventually — this keeps the extension, which is checked against a
    /// fixed list, and nothing else the browser said.
    /// </summary>
    public async Task<string> SaveAsync(Stream content, string originalName, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(originalName).ToLowerInvariant();
        if (!Extensions.Contains(extension))
            throw new InvalidOperationException($"A font has to be one of {string.Join(", ", Extensions)}.");

        System.IO.Directory.CreateDirectory(_directory);

        // One slot. Two uploaded fonts would need a picker, a way to delete one of them and a
        // reference count; one is what the setting can express, so one is what is kept.
        Clear();

        var stored = "custom" + extension;
        var path = Path.Combine(_directory, stored);

        await using (var file = File.Create(path))
            await content.CopyToAsync(file, ct);

        if (new FileInfo(path).Length == 0)
        {
            File.Delete(path);
            throw new InvalidOperationException("That file was empty.");
        }

        return stored;
    }

    /// <summary>The stored font's name, or null if there is not one.</summary>
    public string? Current()
    {
        if (!System.IO.Directory.Exists(_directory))
            return null;

        return Extensions
            .Select(extension => "custom" + extension)
            .FirstOrDefault(name => File.Exists(Path.Combine(_directory, name)));
    }

    public void Clear()
    {
        if (!System.IO.Directory.Exists(_directory))
            return;

        foreach (var extension in Extensions)
        {
            var path = Path.Combine(_directory, "custom" + extension);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// The @font-face rule for the stored file, or empty when there is none. The family is
    /// fixed rather than taken from the user's stack, so the rule cannot be steered by what
    /// somebody types into the family box.
    /// </summary>
    public const string Family = "LabbyCustom";

    public string FaceRule()
    {
        if (Current() is not { } name)
            return "";

        var format = Path.GetExtension(name) switch
        {
            ".woff2" => "woff2",
            ".woff" => "woff",
            ".otf" => "opentype",
            _ => "truetype",
        };

        // font-display: swap — text is readable in the fallback while the file loads rather
        // than the page sitting blank, which on a wall tablet is the difference between a
        // dashboard and a black rectangle for a second.
        return $"@font-face{{font-family:'{Family}';src:url('{Route}/{name}') format('{format}');font-display:swap;}}";
    }
}
