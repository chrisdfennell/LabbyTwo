using System.IO.Compression;
using System.Security.Cryptography;
using LabbyTwo.Services.Offsite;

namespace LabbyTwo.Tests;

/// <summary>
/// The encrypted bundle is the only copy of somebody's credentials that leaves the house,
/// and the thing they will be opening on the worst day of the year. So: it must come back
/// byte for byte, a wrong passphrase must say so, and a damaged or shortened file must fail
/// loudly rather than hand back most of a database.
/// </summary>
public class OffsiteBundleTests
{
    // Few rounds so the suite stays quick; the count is in the header, so decryption
    // follows whatever the file says.
    private const int Rounds = 1000;

    private static byte[] Seal(byte[] plain, string passphrase, int chunk = 64)
    {
        using var output = new MemoryStream();
        BackupBundle.Encrypt(new MemoryStream(plain), output, passphrase, Rounds, chunk);
        return output.ToArray();
    }

    private static async Task<byte[]> OpenAsync(byte[] bundle, string passphrase)
    {
        using var output = new MemoryStream();
        await BackupBundle.DecryptAsync(new MemoryStream(bundle), output, passphrase);
        return output.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]     // exactly one chunk: the last one is full
    [InlineData(128)]    // exactly two
    [InlineData(1000)]
    public async Task ItComesBackExactly(int length)
    {
        var plain = RandomNumberGenerator.GetBytes(length);
        var bundle = Seal(plain, "correct horse battery staple");

        Assert.Equal(plain, await OpenAsync(bundle, "correct horse battery staple"));
    }

    [Fact]
    public async Task AWrongPassphraseIsNamedAsOne()
    {
        var bundle = Seal(RandomNumberGenerator.GetBytes(500), "correct horse battery staple");

        var ex = await Assert.ThrowsAsync<BundleException>(() => OpenAsync(bundle, "correct horse battery stapler"));
        Assert.Contains("Wrong passphrase", ex.Message);
    }

    [Fact]
    public async Task TheSameInputNeverSealsTheSameWay()
    {
        // Fresh salt and nonce prefix every time, or two nights' backups would share a key
        // stream and leak their difference.
        var plain = RandomNumberGenerator.GetBytes(200);
        Assert.NotEqual(Seal(plain, "passphrase-one"), Seal(plain, "passphrase-one"));
        Assert.Equal(plain, await OpenAsync(Seal(plain, "passphrase-one"), "passphrase-one"));
    }

    [Fact]
    public async Task AFileCutShortAtAChunkBoundaryIsRefused()
    {
        // Two whole chunks and a partial third. Dropping the third leaves a file that ends
        // cleanly on a chunk — only the last-chunk flag can tell it was ever longer.
        var bundle = Seal(RandomNumberGenerator.GetBytes(150), "passphrase-one");
        var truncated = bundle[..(BackupBundle.HeaderLength + 2 * (64 + BackupBundle.TagLength))];

        var ex = await Assert.ThrowsAsync<BundleException>(() => OpenAsync(truncated, "passphrase-one"));
        Assert.Contains("cut short", ex.Message);
    }

    [Fact]
    public async Task AFileCutShortMidChunkIsRefused()
    {
        var bundle = Seal(RandomNumberGenerator.GetBytes(150), "passphrase-one");
        await Assert.ThrowsAsync<BundleException>(() => OpenAsync(bundle[..^5], "passphrase-one"));
    }

    [Fact]
    public async Task AFlippedBitIsRefused()
    {
        var bundle = Seal(RandomNumberGenerator.GetBytes(150), "passphrase-one");
        bundle[BackupBundle.HeaderLength + 70] ^= 1;

        var ex = await Assert.ThrowsAsync<BundleException>(() => OpenAsync(bundle, "passphrase-one"));
        Assert.Contains("damaged", ex.Message);
    }

    [Fact]
    public async Task AnEditedHeaderIsRefused()
    {
        // The chunk size is in the header and authenticated as associated data, so changing
        // how the reader splits the file cannot go unnoticed. (Salt, iterations and the
        // check value are covered by the passphrase check itself.)
        var bundle = Seal(RandomNumberGenerator.GetBytes(150), "passphrase-one");
        bundle[29] ^= 1; // the nonce prefix

        await Assert.ThrowsAsync<BundleException>(() => OpenAsync(bundle, "passphrase-one"));
    }

    [Fact]
    public async Task SomethingElseEntirelyIsNotMistakenForABundle()
    {
        var ex = await Assert.ThrowsAsync<BundleException>(() => OpenAsync("SQLite format 3\0 and so on, for a while"u8.ToArray(), "x"));
        Assert.Contains("not a LabbyTwo encrypted backup", ex.Message);
    }

    [Fact]
    public async Task TheBundleHoldsTheDatabaseAndTheKeyring()
    {
        var directory = TestHost.TempDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "keys"));
        try
        {
            var database = Path.Combine(directory, "labbytwo.db");
            var content = RandomNumberGenerator.GetBytes(300_000);
            await File.WriteAllBytesAsync(database, content);
            await File.WriteAllTextAsync(Path.Combine(directory, "keys", "key-1234.xml"), "<key/>");

            using var sealedStream = new MemoryStream();
            BackupBundle.Write(sealedStream, database, Path.Combine(directory, "keys"), "passphrase-one", Rounds, chunkSize: 4096);

            var zipBytes = await OpenAsync(sealedStream.ToArray(), "passphrase-one");
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);

            using (var db = new MemoryStream())
            {
                await using (var entry = zip.GetEntry(BackupBundle.DatabaseEntry)!.Open())
                    await entry.CopyToAsync(db);
                Assert.Equal(content, db.ToArray());
            }

            Assert.NotNull(zip.GetEntry("keys/key-1234.xml"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
