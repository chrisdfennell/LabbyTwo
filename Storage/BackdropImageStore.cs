using System.Security.Cryptography;
using LabbyTwo.Core;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Storage;

/// <summary>
/// The uploaded background picture, kept beside the database in a backdrops folder.
///
/// In the data volume for the same reason as <see cref="FontStore"/>'s fonts: wwwroot is
/// part of the image and is replaced on every update. A file rather than a database row,
/// because the page asks for it on every load and a static file with a year-long cache is
/// the cheapest thing a browser can be given — it costs nothing after the first visit. The
/// price is that the database download on the Settings page does not include it; copying
/// the data volume does, as the README says.
///
/// One slot. The file is named after its own content hash, so a new picture is a new URL and
/// no browser can keep showing the old one from its cache. What is kept is checked and
/// stripped by <see cref="BackdropImageFile"/> first; what the browser said about the file —
/// its name, its type — is not used for anything.
///
/// The current picture is held in memory once found, so a page render never touches the
/// disk to ask whether there is one.
/// </summary>
public sealed class BackdropImageStore
{
    private readonly string _directory;
    private readonly Lock _gate = new();
    private BackdropImage? _current;
    private bool _scanned;

    public BackdropImageStore(IOptions<LabbyOptions> options, IHostEnvironment environment)
    {
        var database = Path.GetFullPath(options.Value.DatabasePath, environment.ContentRootPath);
        _directory = Path.Combine(Path.GetDirectoryName(database) ?? ".", "backdrops");
    }

    /// <summary>Raised after a picture is stored or removed.</summary>
    public event Action? Changed;

    /// <summary>Where the file lives on disk.</summary>
    public string Directory => _directory;

    private const string Prefix = "backdrop-";

    /// <summary>The stored picture, or null.</summary>
    public BackdropImage? Current()
    {
        lock (_gate)
        {
            if (!_scanned)
            {
                _current = Scan();
                _scanned = true;
            }
            return _current;
        }
    }

    /// <summary>The file behind the current picture, or null. For the endpoint that serves it.</summary>
    public string? PathOf(BackdropImage image) =>
        image.IsValid ? Path.Combine(_directory, Prefix + image.Hash + image.Extension) : null;

    /// <summary>
    /// Checks, strips and stores a picture, replacing the one there was. Throws
    /// <see cref="InvalidDataException"/> with a reason somebody can act on when it is not
    /// acceptable; nothing is changed on disk in that case.
    /// </summary>
    public async Task<BackdropImage> SaveAsync(Stream content, CancellationToken ct = default)
    {
        // Read at most one byte past the limit, so an oversized upload is caught without
        // reading all of it.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > BackdropImageFile.MaxBytes)
                throw new InvalidDataException($"That is over {BackdropImageFile.MaxBytes / 1024 / 1024} MB, which is the limit.");
        }

        var data = buffer.ToArray();
        var info = BackdropImageFile.Inspect(data);
        var stripped = BackdropImageFile.StripMetadata(data, info);

        // Stripping must leave a file of the same kind and size — a check that the surgery
        // did not damage it, rather than trusting it.
        var after = BackdropImageFile.Inspect(stripped);
        if (after.Format != info.Format || after.Width != info.Width || after.Height != info.Height)
            throw new InvalidDataException("That picture could not be cleaned of its metadata safely, so it was not kept.");

        var hash = Convert.ToHexStringLower(SHA256.HashData(stripped))[..16];
        var image = new BackdropImage(hash, info.Extension, info.Width, info.Height, stripped.Length);

        System.IO.Directory.CreateDirectory(_directory);
        var path = PathOf(image)!;
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, stripped, ct);
        File.Move(temp, path, overwrite: true);

        lock (_gate)
        {
            foreach (var old in Files())
            {
                if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase))
                    TryDelete(old);
            }
            _current = image;
            _scanned = true;
        }

        Changed?.Invoke();
        return image;
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var file in Files())
                TryDelete(file);
            _current = null;
            _scanned = true;
        }
        Changed?.Invoke();
    }

    private IEnumerable<string> Files() =>
        System.IO.Directory.Exists(_directory)
            ? System.IO.Directory.EnumerateFiles(_directory, Prefix + "*").ToList()
            : [];

    /// <summary>
    /// The picture already on disk, after a restart. Its header is read again rather than
    /// trusted from the name, and a file that no longer passes is ignored.
    /// </summary>
    private BackdropImage? Scan()
    {
        foreach (var file in Files().OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(name);
            var hash = Path.GetFileNameWithoutExtension(name)[Prefix.Length..];
            try
            {
                var bytes = File.ReadAllBytes(file);
                var info = BackdropImageFile.Inspect(bytes);
                var image = new BackdropImage(hash, extension, info.Width, info.Height, bytes.Length);
                if (image.IsValid && info.Extension == extension)
                    return image;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // Not one of ours, or damaged: not shown.
            }
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Open in the middle of being served; the next upload or removal tries again.
        }
    }
}
