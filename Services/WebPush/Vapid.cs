using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace LabbyTwo.Services.WebPush;

/// <summary>
/// The VAPID signature (RFC 8292) that tells a push service this message comes from the
/// same server the browser subscribed to. It is an ES256 JWT naming the push service as its
/// audience, with a short expiry, sent beside the public key in the Authorization header.
/// </summary>
public static class Vapid
{
    /// <summary>
    /// Twelve hours. The RFC allows up to a day; less leaves room for a clock on the NAS that
    /// has drifted ahead, which push services answer with a 403 that looks like a bad key.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    /// <summary>
    /// Who to contact about this sender. Apple refuses a token without one, and wants a
    /// mailto: or an https: URL. The project's page is the honest answer for an install that
    /// has not set anything else: it is whose code is sending.
    /// </summary>
    public const string DefaultSubject = "https://github.com/chrisdfennell/LabbyTwo";

    public static string Token(ECDsa key, string audience, string subject, DateTimeOffset expires)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg = "ES256" });
        var claims = JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = audience,
            exp = expires.ToUnixTimeSeconds(),
            sub = subject,
        });

        var signingInput = $"{WebPushCrypto.ToBase64Url(header)}.{WebPushCrypto.ToBase64Url(claims)}";

        // JWS wants r ‖ s, 64 bytes, which is the IEEE P1363 form .NET produces by default —
        // not the DER sequence OpenSSL would hand back.
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"{signingInput}.{WebPushCrypto.ToBase64Url(signature)}";
    }

    /// <summary>The audience is the push service's origin — scheme and host, no path.</summary>
    public static string Audience(Uri endpoint) => endpoint.GetLeftPart(UriPartial.Authority);

    public static string AuthorizationHeader(string token, string publicKey) => $"vapid t={token}, k={publicKey}";
}

/// <summary>
/// This install's VAPID key pair, made the first time anything asks for it and kept in the
/// database from then on. It has to be stable: every subscription a browser holds is bound
/// to the public key it was made with, so a new pair would silently orphan every device.
///
/// The private half is encrypted with the same data-protection keyring as connection
/// passwords, so it travels with the database and its keys the same way they do.
/// </summary>
public sealed class VapidKeys(
    AppSettingsStore settings,
    PushSubscriptionStore subscriptions,
    IDataProtectionProvider protection,
    ILogger<VapidKeys> log)
{
    public const string PublicKeySetting = "webpush_public";
    public const string PrivateKeySetting = "webpush_private";

    private readonly IDataProtector _protector = protection.CreateProtector("LabbyTwo.VapidKey");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Pair? _pair;

    /// <summary>The public key as browsers want it (base64url, uncompressed point) and the signing key.</summary>
    public sealed record Pair(string PublicKey, ECDsa Key);

    public async Task<string> PublicKeyAsync(CancellationToken ct = default) => (await GetAsync(ct)).PublicKey;

    public async Task<Pair> GetAsync(CancellationToken ct = default)
    {
        if (_pair is { } ready)
            return ready;

        await _lock.WaitAsync(ct);
        try
        {
            if (_pair is { } loaded)
                return loaded;

            var stored = await settings.AllAsync(ct);
            var publicKey = stored.Get(PublicKeySetting);
            var privateKey = stored.Get(PrivateKeySetting);

            if (publicKey.Length > 0 && privateKey.Length > 0)
            {
                try
                {
                    var d = WebPushCrypto.FromBase64Url(_protector.Unprotect(privateKey));
                    var q = WebPushCrypto.FromBase64Url(publicKey);
                    var key = ECDsa.Create(new ECParameters
                    {
                        Curve = ECCurve.NamedCurves.nistP256,
                        D = d,
                        Q = new ECPoint { X = q[1..33], Y = q[33..] },
                    });
                    return _pair = new Pair(publicKey, key);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "The stored push key could not be read, so a new one is being made. " +
                        "Every device will need to press Notify this device again.");
                }
            }

            var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var parameters = fresh.ExportParameters(true);
            var freshPublic = WebPushCrypto.ToBase64Url([0x04, .. parameters.Q.X!, .. parameters.Q.Y!]);

            await settings.SaveAsync(new Dictionary<string, string>
            {
                [PublicKeySetting] = freshPublic,
                [PrivateKeySetting] = _protector.Protect(WebPushCrypto.ToBase64Url(parameters.D!)),
            }, ct);

            // A pair existed and could not be read, so every stored subscription was made
            // against a key nobody has any more. Push services refuse those outright; keeping
            // them would only turn every alert into a list of failures. The devices page is
            // left empty, which is the true state: each one has to subscribe again.
            if (publicKey.Length > 0)
                await subscriptions.DeleteAllAsync(ct);

            return _pair = new Pair(freshPublic, fresh);
        }
        finally
        {
            _lock.Release();
        }
    }
}
