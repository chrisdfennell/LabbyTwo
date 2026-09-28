using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace LabbyTwo.Services.Offsite;

/// <summary>
/// The encrypted file that goes off-site when a passphrase is set: a zip of the database
/// and the keyring, sealed with a key derived from the passphrase.
///
/// Why both, and why sealed. Every password and API key in the database is encrypted with
/// the keyring in <c>data/keys</c>, so a copy of the database alone restores the dashboard
/// but not one credential in it. Sending the keyring too fixes that — and also means that
/// whoever reads the bucket can read every credential. A passphrase that only the owner
/// knows is what makes carrying the keyring safe, so the keyring only ever leaves inside
/// this. With no passphrase, the database goes alone and the Settings page says what that
/// costs.
///
/// The format is small enough to decrypt with a dozen lines of anything that has
/// PBKDF2 and AES-GCM (scripts/decrypt-backup.py is one), because a backup you can only
/// open with the program that died is not much of a backup. All integers are big-endian.
///
/// <code>
///   header (56 bytes)
///     8   magic "LABBYBK1"
///     1   KDF: 1 = PBKDF2-HMAC-SHA256
///     4   iterations
///     16  salt
///     7   nonce prefix
///     4   chunk size (plaintext bytes per chunk)
///     16  key check: HMAC-SHA256(key, "LabbyTwo backup key check"), first 16 bytes
///   chunks, until the end of the file
///     ciphertext (chunk size bytes, or fewer for the last) + 16-byte GCM tag
/// </code>
///
/// Each chunk is AES-256-GCM with the whole header as associated data and a nonce of
/// prefix ‖ chunk index (4 bytes) ‖ 1 for the last chunk, 0 otherwise. That is the STREAM
/// construction: the index stops chunks being reordered, and the last-chunk flag stops a
/// file cut short at a chunk boundary from decrypting as though it were whole. Chunked
/// rather than one GCM call because a database can be hundreds of megabytes, and neither
/// end of this should have to hold it in memory.
/// </summary>
public static class BackupBundle
{
    public const string Extension = ".l2backup";

    public const int HeaderLength = 56;
    public const int TagLength = 16;
    public const int DefaultChunkSize = 1 << 20;

    /// <summary>
    /// OWASP's 2023 figure for PBKDF2-SHA256. PBKDF2 rather than Argon2 because it is in
    /// .NET, Python's standard library and OpenSSL alike, so the restore instructions need
    /// nothing installed but the AES library — and a nightly job derives this once.
    /// </summary>
    public const int DefaultIterations = 600_000;

    /// <summary>A file claiming more than this is refused, not ground through for an hour.</summary>
    internal const int MaxIterations = 50_000_000;

    private const byte KdfPbkdf2Sha256 = 1;
    private static readonly byte[] Magic = "LABBYBK1"u8.ToArray();
    private static readonly byte[] CheckLabel = "LabbyTwo backup key check"u8.ToArray();

    /// <summary>Where the database sits inside the zip. The restore steps rename it to whatever DatabasePath says.</summary>
    public const string DatabaseEntry = "labbytwo.db";

    public const string KeysFolder = "keys/";

    /// <summary>
    /// Writes the zip of <paramref name="databasePath"/> and every file in
    /// <paramref name="keysDirectory"/>, encrypted, to <paramref name="output"/>.
    /// Synchronous underneath (ZipArchive is), so call it off any thread that matters.
    /// </summary>
    public static void Write(
        Stream output, string databasePath, string? keysDirectory, string passphrase,
        int iterations = DefaultIterations, int chunkSize = DefaultChunkSize)
    {
        using var sealer = new EncryptingStream(output, passphrase, iterations, chunkSize);
        using (var zip = new ZipArchive(sealer, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Fastest rather than Optimal: SQLite pages compress well either way, and this
            // runs nightly on a NAS CPU.
            zip.CreateEntryFromFile(databasePath, DatabaseEntry, CompressionLevel.Fastest);

            if (keysDirectory is not null && Directory.Exists(keysDirectory))
            {
                foreach (var key in Directory.EnumerateFiles(keysDirectory))
                    zip.CreateEntryFromFile(key, KeysFolder + Path.GetFileName(key), CompressionLevel.Fastest);
            }
        }
        sealer.Finish();
    }

    /// <summary>Encrypts an arbitrary stream. What <see cref="Write"/> uses, exposed for tests.</summary>
    public static void Encrypt(Stream input, Stream output, string passphrase,
        int iterations = DefaultIterations, int chunkSize = DefaultChunkSize)
    {
        using var sealer = new EncryptingStream(output, passphrase, iterations, chunkSize);
        input.CopyTo(sealer);
        sealer.Finish();
    }

    /// <summary>
    /// Decrypts a whole bundle into <paramref name="output"/>. Throws
    /// <see cref="BundleException"/> with a sentence a person can act on.
    /// </summary>
    public static async Task DecryptAsync(Stream input, Stream output, string passphrase, CancellationToken ct = default)
    {
        var reader = await BundleReader.OpenAsync(input, passphrase, ct);
        await reader.CopyToAsync(output, ct);
    }

    internal static byte[] DeriveKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);

    internal static byte[] KeyCheck(byte[] key) => HMACSHA256.HashData(key, CheckLabel)[..16];

    internal static void Nonce(Span<byte> nonce, ReadOnlySpan<byte> prefix, uint index, bool last)
    {
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[7..11], index);
        nonce[11] = last ? (byte)1 : (byte)0;
    }

    /// <summary>
    /// A write-only stream that seals whatever is written through it. Not seekable, which
    /// ZipArchive copes with by writing data descriptors instead of going back.
    /// </summary>
    private sealed class EncryptingStream : Stream
    {
        private readonly Stream _output;
        private readonly AesGcm _aes;
        private readonly byte[] _header;
        private readonly byte[] _prefix;
        private readonly byte[] _buffer;
        private readonly byte[] _sealed;
        private int _filled;
        private uint _index;
        private bool _finished;

        public EncryptingStream(Stream output, string passphrase, int iterations, int chunkSize)
        {
            if (string.IsNullOrEmpty(passphrase))
                throw new ArgumentException("A passphrase is required.", nameof(passphrase));

            _output = output;
            var salt = RandomNumberGenerator.GetBytes(16);
            _prefix = RandomNumberGenerator.GetBytes(7);
            var key = DeriveKey(passphrase, salt, iterations);

            _header = new byte[HeaderLength];
            Magic.CopyTo(_header, 0);
            _header[8] = KdfPbkdf2Sha256;
            BinaryPrimitives.WriteInt32BigEndian(_header.AsSpan(9, 4), iterations);
            salt.CopyTo(_header, 13);
            _prefix.CopyTo(_header, 29);
            BinaryPrimitives.WriteInt32BigEndian(_header.AsSpan(36, 4), chunkSize);
            KeyCheck(key).CopyTo(_header, 40);

            _aes = new AesGcm(key, TagLength);
            CryptographicOperations.ZeroMemory(key);

            _buffer = new byte[chunkSize];
            _sealed = new byte[chunkSize + TagLength];
            _output.Write(_header);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_finished;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => _output.Flush();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(_finished, this);
            while (data.Length > 0)
            {
                // A full buffer is only sealed once more data turns up, because until then
                // it might be the last chunk and has to be flagged as such.
                if (_filled == _buffer.Length)
                {
                    Seal(last: false);
                    _filled = 0;
                }

                var take = Math.Min(data.Length, _buffer.Length - _filled);
                data[..take].CopyTo(_buffer.AsSpan(_filled));
                _filled += take;
                data = data[take..];
            }
        }

        public void Finish()
        {
            if (_finished)
                return;
            Seal(last: true);
            _finished = true;
            _output.Flush();
        }

        private void Seal(bool last)
        {
            if (_index == uint.MaxValue)
                throw new InvalidOperationException("The backup is too large for this format.");

            Span<byte> nonce = stackalloc byte[12];
            Nonce(nonce, _prefix, _index, last);
            var cipher = _sealed.AsSpan(0, _filled);
            var tag = _sealed.AsSpan(_filled, TagLength);
            _aes.Encrypt(nonce, _buffer.AsSpan(0, _filled), cipher, tag, _header);
            _output.Write(_sealed, 0, _filled + TagLength);
            _index++;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _aes.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// An opened bundle whose passphrase has been checked. Split from the copy so a caller —
/// the decrypt endpoint — can refuse a wrong passphrase before it starts sending anything.
/// </summary>
public sealed class BundleReader
{
    private readonly Stream _input;
    private readonly byte[] _header;
    private readonly byte[] _key;
    private readonly int _chunkSize;

    private BundleReader(Stream input, byte[] header, byte[] key, int chunkSize)
    {
        _input = input;
        _header = header;
        _key = key;
        _chunkSize = chunkSize;
    }

    public static async Task<BundleReader> OpenAsync(Stream input, string passphrase, CancellationToken ct = default)
    {
        var header = new byte[BackupBundle.HeaderLength];
        if (await input.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct) < header.Length
            || !header.AsSpan(0, 8).SequenceEqual("LABBYBK1"u8))
            throw new BundleException("That is not a LabbyTwo encrypted backup (.l2backup) file.");

        if (header[8] != 1)
            throw new BundleException("This backup was written by a newer LabbyTwo, which uses a key derivation this one does not know.");

        var iterations = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(9, 4));
        var chunkSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(36, 4));
        if (iterations is < 1 or > BackupBundle.MaxIterations || chunkSize is < 1 or > 64 << 20)
            throw new BundleException("The backup's header is damaged.");

        var key = BackupBundle.DeriveKey(passphrase, header[13..29], iterations);
        if (!CryptographicOperations.FixedTimeEquals(BackupBundle.KeyCheck(key), header.AsSpan(40, 16)))
            throw new BundleException("Wrong passphrase. It is the one that was set on the Settings page when this backup was taken.");

        return new BundleReader(input, header, key, chunkSize);
    }

    public async Task CopyToAsync(Stream output, CancellationToken ct = default)
    {
        var length = _chunkSize + BackupBundle.TagLength;
        var current = new byte[length];
        var next = new byte[length];
        var plain = new byte[_chunkSize];
        var prefix = _header.AsMemory(29, 7);

        using var aes = new AesGcm(_key, BackupBundle.TagLength);
        var read = await _input.ReadAtLeastAsync(current, length, throwOnEndOfStream: false, ct);
        uint index = 0;

        while (true)
        {
            // Read one chunk ahead: only the end of the file says which chunk was the last,
            // and the last one was sealed with a different nonce.
            var ahead = read == length
                ? await _input.ReadAtLeastAsync(next, length, throwOnEndOfStream: false, ct)
                : 0;
            var last = ahead == 0;

            if (read < BackupBundle.TagLength)
                throw new BundleException("The backup is cut short — it ends part-way through. The upload or copy did not finish.");

            var cipherLength = read - BackupBundle.TagLength;
            var nonce = new byte[12];
            BackupBundle.Nonce(nonce, prefix.Span, index, last);
            try
            {
                aes.Decrypt(nonce, current.AsSpan(0, cipherLength), current.AsSpan(cipherLength, BackupBundle.TagLength),
                    plain.AsSpan(0, cipherLength), _header);
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new BundleException(last && index > 0
                    ? "The backup is cut short or damaged near its end — it does not decrypt as a whole file."
                    : $"The backup is damaged: part {index + 1} does not decrypt, although the passphrase is right.");
            }

            await output.WriteAsync(plain.AsMemory(0, cipherLength), ct);
            if (last)
                return;

            (current, next) = (next, current);
            read = ahead;
            index++;
        }
    }
}

/// <summary>A bundle that cannot be opened, with the reason in words.</summary>
public sealed class BundleException(string message) : Exception(message);
