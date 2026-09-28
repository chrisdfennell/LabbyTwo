using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LabbyTwo.Services.Offsite;

/// <summary>
/// AWS Signature Version 4, the request signing every S3-compatible store accepts — AWS,
/// Backblaze B2, Cloudflare R2, MinIO, Wasabi.
///
/// Written here rather than taken from the AWS SDK: this needs three calls (put, list,
/// delete), the SDK is several megabytes and a steady stream of updates for a NAS image,
/// and SigV4 is a page of well-specified hashing. It is checked against the examples AWS
/// publishes in its own documentation, which is the only oracle that counts.
/// </summary>
public static class SigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>SHA-256 of nothing, for requests without a body.</summary>
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>
    /// The value of the Authorization header.
    /// </summary>
    /// <param name="canonicalUri">The path exactly as sent, already percent-encoded.</param>
    /// <param name="query">Unencoded query parameters; encoded and sorted here.</param>
    /// <param name="headers">
    /// The headers to sign, including host and x-amz-date. Names are lower-cased and values
    /// trimmed here; the caller must send the same values.
    /// </param>
    public static string Authorization(
        string method, string canonicalUri, IEnumerable<KeyValuePair<string, string>> query,
        IEnumerable<KeyValuePair<string, string>> headers, string payloadHash,
        string accessKey, string secretKey, string region, string service, DateTimeOffset now)
    {
        var (signedHeaders, signature) = Sign(method, canonicalUri, query, headers, payloadHash, secretKey, region, service, now);
        return $"{Algorithm} Credential={accessKey}/{Scope(now, region, service)}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    public static (string SignedHeaders, string Signature) Sign(
        string method, string canonicalUri, IEnumerable<KeyValuePair<string, string>> query,
        IEnumerable<KeyValuePair<string, string>> headers, string payloadHash,
        string secretKey, string region, string service, DateTimeOffset now)
    {
        var canonical = CanonicalRequest(method, canonicalUri, query, headers, payloadHash, out var signedHeaders);

        var stringToSign = string.Join('\n',
            Algorithm,
            AmzDate(now),
            Scope(now, region, service),
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));

        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), Date(now));
        key = Hmac(key, region);
        key = Hmac(key, service);
        key = Hmac(key, "aws4_request");

        return (signedHeaders, Hex(Hmac(key, stringToSign)));
    }

    public static string CanonicalRequest(
        string method, string canonicalUri, IEnumerable<KeyValuePair<string, string>> query,
        IEnumerable<KeyValuePair<string, string>> headers, string payloadHash, out string signedHeaders)
    {
        var sortedHeaders = headers
            .Select(h => (Name: h.Key.Trim().ToLowerInvariant(), Value: CollapseSpaces(h.Value.Trim())))
            .OrderBy(h => h.Name, StringComparer.Ordinal)
            .ToList();

        signedHeaders = string.Join(';', sortedHeaders.Select(h => h.Name));

        return string.Join('\n',
            method,
            canonicalUri.Length == 0 ? "/" : canonicalUri,
            CanonicalQuery(query),
            string.Concat(sortedHeaders.Select(h => $"{h.Name}:{h.Value}\n")),
            signedHeaders,
            payloadHash);
    }

    /// <summary>Query string in the order and encoding the signature covers — also what goes on the wire.</summary>
    public static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> query) =>
        string.Join('&', query
            .Select(p => (Key: Encode(p.Key), Value: Encode(p.Value)))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));

    /// <summary>
    /// RFC 3986 encoding as SigV4 defines it: everything but A–Z a–z 0–9 - _ . ~ is
    /// percent-encoded from its UTF-8 bytes, in upper-case hex. Uri.EscapeDataString is
    /// close, but not something to trust with a signature.
    /// </summary>
    public static string Encode(string value, bool keepSlash = false)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~'
                || (keepSlash && c == '/'))
                builder.Append(c);
            else
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    public static string AmzDate(DateTimeOffset now) => now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    private static string Date(DateTimeOffset now) => now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string Scope(DateTimeOffset now, string region, string service) => $"{Date(now)}/{region}/{service}/aws4_request";

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string CollapseSpaces(string value)
    {
        if (!value.Contains("  "))
            return value;
        var builder = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value)
        {
            if (c == ' ')
            {
                if (!space)
                    builder.Append(c);
                space = true;
            }
            else
            {
                builder.Append(c);
                space = false;
            }
        }
        return builder.ToString();
    }
}
