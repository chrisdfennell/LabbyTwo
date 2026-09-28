using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace LabbyTwo.Services.WebPush;

/// <summary>
/// Message encryption for Web Push (RFC 8291), in the aes128gcm content coding (RFC 8188).
///
/// Written here rather than taken from a package. Everything it needs — P-256 ECDH, HKDF
/// and AES-GCM — has been in the base library for several releases, so the whole scheme is
/// a page of key derivation. The .NET packages that do this predate those APIs and carry
/// BouncyCastle to fill the gap, which is a large dependency to take on for a page of code
/// whose correctness a published test vector pins exactly (see WebPushTests).
/// </summary>
public static class WebPushCrypto
{
    /// <summary>
    /// The record size written into the header. One record is all a push message ever is:
    /// push services accept 4096 bytes of body, and this is the value the RFC's example uses.
    /// </summary>
    public const int RecordSize = 4096;

    /// <summary>Salt, record size, key-id length and a 65-byte key id.</summary>
    public const int HeaderLength = 16 + 4 + 1 + 65;

    /// <summary>
    /// The most plaintext that fits in one 4096-byte message: the header, the AES-GCM tag and
    /// the one-byte padding delimiter come out of it.
    /// </summary>
    public const int MaxPlaintext = RecordSize - HeaderLength - 16 - 1;

    /// <summary>
    /// Encrypts one push message for one subscription.
    /// </summary>
    /// <param name="plaintext">What the service worker will read from <c>event.data</c>.</param>
    /// <param name="userAgentPublicKey">The subscription's <c>p256dh</c>, an uncompressed P-256 point.</param>
    /// <param name="authSecret">The subscription's <c>auth</c>, 16 bytes.</param>
    /// <param name="serverKey">
    /// A throwaway key pair for this message. Fresh every time in real use — the RFC requires
    /// it — and passed in only so the test vector can be reproduced byte for byte.
    /// </param>
    /// <param name="salt">16 random bytes, likewise fresh per message.</param>
    public static byte[] Encrypt(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> userAgentPublicKey,
        ReadOnlySpan<byte> authSecret,
        ECDiffieHellman serverKey,
        ReadOnlySpan<byte> salt)
    {
        if (plaintext.Length > MaxPlaintext)
            throw new ArgumentException($"A push message holds at most {MaxPlaintext} bytes.", nameof(plaintext));
        if (salt.Length != 16)
            throw new ArgumentException("The salt must be 16 bytes.", nameof(salt));

        var serverPublic = PublicKeyBytes(serverKey);
        using var userAgent = ImportPublicKey(userAgentPublicKey);
        var sharedSecret = serverKey.DeriveRawSecretAgreement(userAgent.PublicKey);

        var (cek, nonce) = DeriveKeys(sharedSecret, authSecret, userAgentPublicKey, serverPublic, salt);

        // One record, so it is also the last: a single 0x02 delimiter and no further padding.
        var padded = new byte[plaintext.Length + 1];
        plaintext.CopyTo(padded);
        padded[^1] = 0x02;

        var body = new byte[HeaderLength + padded.Length + 16];
        salt.CopyTo(body);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16, 4), RecordSize);
        body[20] = (byte)serverPublic.Length;
        serverPublic.CopyTo(body.AsSpan(21));

        using var aes = new AesGcm(cek, 16);
        aes.Encrypt(nonce, padded,
            body.AsSpan(HeaderLength, padded.Length),
            body.AsSpan(HeaderLength + padded.Length, 16));

        return body;
    }

    /// <summary>
    /// The content-encryption key and nonce. Shared by encryption and by the tests' decryption,
    /// which is the other half of the round trip and must derive exactly the same two values.
    /// </summary>
    public static (byte[] Cek, byte[] Nonce) DeriveKeys(
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> authSecret,
        ReadOnlySpan<byte> userAgentPublicKey,
        ReadOnlySpan<byte> serverPublicKey,
        ReadOnlySpan<byte> salt)
    {
        // The auth secret is mixed in first, so a message cannot be read by somebody who
        // only has the push service's view of the subscription (the endpoint and p256dh).
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), userAgentPublicKey, serverPublicKey);
        var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, sharedSecret.ToArray(), authSecret.ToArray());
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);

        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt.ToArray());
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        return (cek, nonce);
    }

    /// <summary>The uncompressed point, 0x04 ‖ X ‖ Y — the form browsers hand out and expect.</summary>
    public static byte[] PublicKeyBytes(ECAlgorithm key)
    {
        var point = key.ExportParameters(false).Q;
        return [0x04, .. point.X!, .. point.Y!];
    }

    public static ECDiffieHellman ImportPublicKey(ReadOnlySpan<byte> uncompressed)
    {
        if (uncompressed.Length != 65 || uncompressed[0] != 0x04)
            throw new ArgumentException("Not an uncompressed P-256 public key.");

        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = uncompressed[1..33].ToArray(), Y = uncompressed[33..].ToArray() },
        });
    }

    public static string ToBase64Url(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    /// <summary>
    /// Tolerates padding and the standard alphabet as well as base64url: a subscription can
    /// arrive from a browser, from a copy-paste or from an older export, and they disagree.
    /// </summary>
    public static byte[] FromBase64Url(string text)
    {
        var trimmed = text.Trim().TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Base64Url.DecodeFromChars(trimmed);
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c) =>
        [.. a, .. b, .. c];
}
