using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Xml.Linq;
using LabbyTwo.Core;

namespace LabbyTwo.Services.Offsite;

/// <summary>
/// The three S3 calls an off-site backup needs — PutObject, ListObjectsV2, DeleteObject —
/// signed with <see cref="SigV4"/>. Nothing else of S3 is here, deliberately.
/// </summary>
public sealed class S3Client(HttpClient http, OffsiteDestination destination, Func<DateTimeOffset>? clock = null)
{
    public sealed record S3Object(string Key, long Size, DateTimeOffset? LastModified);

    /// <summary>A refusal from the store, already turned into advice.</summary>
    public sealed class S3Exception(string message, HttpStatusCode status, string? code) : Exception(message)
    {
        public HttpStatusCode Status { get; } = status;
        public string? Code { get; } = code;

        /// <summary>Worth trying again: the store was busy or broken, not the request wrong.</summary>
        public bool Transient => (int)Status >= 500 || Status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;
    }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// For a whole backup. Generous, because a home upstream can be a few megabits and a
    /// database a few hundred megabytes; a stall still ends here rather than never.
    /// </summary>
    public TimeSpan UploadTimeout { get; init; } = TimeSpan.FromHours(2);

    private string Region => destination.Region.Trim() is { Length: > 0 } r ? r : "us-east-1";

    private Uri BaseUri => destination.Endpoint.Trim() is { Length: > 0 } e
        ? new Uri(e.TrimEnd('/'))
        : new Uri($"https://s3.{Region}.amazonaws.com");

    /// <summary>
    /// Streams a file up. The hash is taken in a first pass over the file rather than
    /// sending UNSIGNED-PAYLOAD, because every store accepts a signed payload over plain
    /// http as well as https, and a MinIO on the LAN is often plain http. Two reads of a
    /// local file are cheap next to one upload of it.
    /// </summary>
    public async Task PutFileAsync(string key, string path, CancellationToken ct)
    {
        string hash;
        await using (var read = OpenRead(path))
            hash = SigV4.Hex(await SHA256.HashDataAsync(read, ct));

        await using var body = OpenRead(path);
        var content = new StreamContent(body, 256 * 1024);
        content.Headers.ContentLength = body.Length;
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await SendAsync(HttpMethod.Put, key, [], hash, content, UploadTimeout, ct);
    }

    /// <summary>A small object, for the Test button.</summary>
    public async Task PutBytesAsync(string key, byte[] bytes, CancellationToken ct)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        using var response = await SendAsync(HttpMethod.Put, key, [], SigV4.Hex(SHA256.HashData(bytes)), content, RequestTimeout, ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, key, [], SigV4.EmptyPayloadHash, null, RequestTimeout, ct);
    }

    /// <summary>Everything under a prefix, following continuation tokens.</summary>
    public async Task<IReadOnlyList<S3Object>> ListAsync(string prefix, CancellationToken ct, int? maxKeys = null)
    {
        var objects = new List<S3Object>();
        string? token = null;
        do
        {
            var query = new List<KeyValuePair<string, string>> { new("list-type", "2"), new("prefix", prefix) };
            if (token is not null)
                query.Add(new("continuation-token", token));
            if (maxKeys is { } max)
                query.Add(new("max-keys", max.ToString(CultureInfo.InvariantCulture)));

            using var response = await SendAsync(HttpMethod.Get, null, query, SigV4.EmptyPayloadHash, null, RequestTimeout, ct);
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = xml.Root!;

            foreach (var item in root.Elements().Where(e => e.Name.LocalName == "Contents"))
            {
                var key = Child(item, "Key") ?? "";
                long.TryParse(Child(item, "Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
                DateTimeOffset? modified = DateTimeOffset.TryParse(Child(item, "LastModified"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var at) ? at : null;
                objects.Add(new S3Object(key, size, modified));
            }

            token = string.Equals(Child(root, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase)
                ? Child(root, "NextContinuationToken")
                : null;
        }
        while (token is not null && maxKeys is null);

        return objects;
    }

    /// <summary>The URL for an object (or the bucket, with no key), in whichever addressing style is set.</summary>
    public (Uri Uri, string CanonicalPath) Address(string? key, IEnumerable<KeyValuePair<string, string>> query)
    {
        var baseUri = BaseUri;
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var objectPath = key is null ? "/" : "/" + SigV4.Encode(key, keepSlash: true);

        string host;
        string path;
        if (destination.PathStyle)
        {
            host = baseUri.Authority;
            path = basePath + "/" + SigV4.Encode(destination.Bucket.Trim()) + (key is null ? "" : objectPath);
        }
        else
        {
            host = destination.Bucket.Trim() + "." + baseUri.Authority;
            path = basePath + objectPath;
        }

        if (path.Length == 0)
            path = "/";

        var queryString = SigV4.CanonicalQuery(query);
        var uri = new Uri($"{baseUri.Scheme}://{host}{path}{(queryString.Length > 0 ? "?" + queryString : "")}");
        return (uri, path);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string? key, List<KeyValuePair<string, string>> query, string payloadHash,
        HttpContent? content, TimeSpan timeout, CancellationToken ct)
    {
        var (uri, path) = Address(key, query);
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        var amzDate = SigV4.AmzDate(now);

        var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        // Host as HttpClient will send it: Authority leaves the port off when it is the
        // scheme's default, which is exactly what the header does.
        var signed = new Dictionary<string, string>
        {
            ["host"] = uri.Authority,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };
        request.Headers.TryAddWithoutValidation("Authorization", SigV4.Authorization(
            method.Method, path, query, signed, payloadHash,
            destination.AccessKey.Trim(), destination.SecretKey.Trim(), Region, "s3", now));

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(timeout);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, window.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No answer from {uri.Authority} within {SystemHealth.Seconds(timeout)}.");
        }

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw Explain(response.StatusCode, body, uri);
        }
    }

    /// <summary>
    /// Turns an S3 error document into what to go and check. The codes are the same
    /// across AWS and the compatible stores, which is most of the point of them.
    /// </summary>
    public S3Exception Explain(HttpStatusCode status, string body, Uri uri)
    {
        string? code = null, message = null;
        try
        {
            var root = XDocument.Parse(body).Root;
            code = root is null ? null : Child(root, "Code");
            message = root is null ? null : Child(root, "Message");
        }
        catch (System.Xml.XmlException)
        {
            // Not XML — a proxy's error page, say. The status is all there is.
        }

        var bucket = destination.Bucket.Trim();
        var said = message is { Length: > 0 } ? $" The store said: \"{message}\"" : "";
        var advice = code switch
        {
            "SignatureDoesNotMatch" =>
                "The signature was refused: the secret key is wrong (or has a stray space), or the region does not match the bucket's.",
            "InvalidAccessKeyId" => "The store does not know that access key. Check it was copied whole and has not been deleted.",
            "RequestTimeTooSkewed" => "This machine's clock is too far from the store's. Fix the NAS's time (NTP) and it will work.",
            "NoSuchBucket" => $"There is no bucket called \"{bucket}\" at {uri.Authority}. Check its name, and the endpoint and region.",
            "PermanentRedirect" or "AuthorizationHeaderMalformed" or "IllegalLocationConstraintException" =>
                $"\"{bucket}\" is in a different region from \"{Region}\". Set the region the bucket was created in.",
            "AccessDenied" or "AllAccessDisabled" =>
                $"Access denied. The key works but is not allowed to do this in \"{bucket}\" — check the key's permissions and the bucket policy" +
                " (it needs to put and list objects, and delete them if old copies are to be tidied up).",
            _ => status switch
            {
                HttpStatusCode.Forbidden =>
                    $"Refused (403). Check the access key and secret, that the key may write to \"{bucket}\", and the region.",
                HttpStatusCode.NotFound =>
                    $"Not found (404) at {uri.Authority}. The bucket name or the endpoint is wrong — or the store wants path-style addressing.",
                HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect =>
                    $"The store redirected elsewhere, which means the region or endpoint is wrong for \"{bucket}\".",
                _ => $"The store answered HTTP {(int)status}{(code is null ? "" : $" ({code})")}.",
            },
        };

        return new S3Exception(advice + said, status, code);
    }

    private static string? Child(XElement element, string name) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
}
