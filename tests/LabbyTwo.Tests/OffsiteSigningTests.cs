using System.Net;
using System.Text;
using LabbyTwo.Services.Offsite;

namespace LabbyTwo.Tests;

/// <summary>
/// SigV4 is hand-written, so it is checked against answers nobody here computed: the
/// examples in AWS's own documentation, and one worked by botocore — the signer the AWS CLI
/// uses — for the request shape a MinIO on the LAN actually gets. A signature is right or
/// it is useless, and "the store said 403" is a bad way to find out which.
/// </summary>
public class OffsiteSigningTests
{
    private static readonly KeyValuePair<string, string>[] NoQuery = [];

    [Fact]
    public void TheSigV4TestSuitesVanillaGetSignsAsAwsSays()
    {
        // get-vanilla, from the AWS Signature Version 4 test suite.
        var now = new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);
        var authorization = SigV4.Authorization("GET", "/", NoQuery,
            [new("Host", "example.amazonaws.com"), new("X-Amz-Date", SigV4.AmzDate(now))],
            SigV4.EmptyPayloadHash, "AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY",
            "us-east-1", "service", now);

        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, " +
            "SignedHeaders=host;x-amz-date, " +
            "Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
            authorization);
    }

    [Fact]
    public void TheS3DocumentationsGetObjectExampleSignsAsAwsSays()
    {
        // "Example: GET Object" in the S3 API reference's SigV4 header-authentication page.
        var now = new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);
        var (signed, signature) = SigV4.Sign("GET", "/test.txt", NoQuery,
            [
                new("Host", "examplebucket.s3.amazonaws.com"),
                new("Range", "bytes=0-9"),
                new("x-amz-content-sha256", SigV4.EmptyPayloadHash),
                new("x-amz-date", "20130524T000000Z"),
            ],
            SigV4.EmptyPayloadHash, "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", "s3", now);

        Assert.Equal("host;range;x-amz-content-sha256;x-amz-date", signed);
        Assert.Equal("f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", signature);
    }

    [Fact]
    public void TheS3DocumentationsListObjectsExampleSignsAsAwsSays()
    {
        // "Example: GET Bucket (List Objects)": the query is sorted and encoded by the signer.
        var now = new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);
        var (_, signature) = SigV4.Sign("GET", "/", [new("prefix", "J"), new("max-keys", "2")],
            [
                new("Host", "examplebucket.s3.amazonaws.com"),
                new("x-amz-content-sha256", SigV4.EmptyPayloadHash),
                new("x-amz-date", "20130524T000000Z"),
            ],
            SigV4.EmptyPayloadHash, "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", "s3", now);

        Assert.Equal("34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7", signature);
    }

    [Fact]
    public async Task APathStylePutToAPortSignsAsBotocoreDoes()
    {
        // Worked with botocore's S3SigV4Auth for exactly this request. Covers what the AWS
        // examples do not: path-style addressing, a non-default port in the Host header, a
        // nested key, and a secret with / and + in it.
        var handler = new CapturingHandler();
        var destination = new OffsiteDestination
        {
            Kind = OffsiteDestination.S3Kind, Name = "MinIO", Endpoint = "http://minio.lan:9000", Region = "us-east-1",
            Bucket = "backups", Prefix = "labbytwo/nas", AccessKey = "minioadmin", SecretKey = "minio/secret+key",
            PathStyle = true,
        };
        var client = new S3Client(new HttpClient(handler), destination,
            () => new DateTimeOffset(2026, 9, 28, 3, 0, 5, TimeSpan.Zero));

        await client.PutBytesAsync(destination.NormalisedPrefix + "labbytwo-2026-09-28.l2backup", "hello"u8.ToArray(), default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://minio.lan:9000/backups/labbytwo/nas/labbytwo-2026-09-28.l2backup", request.Uri.ToString());
        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=minioadmin/20260928/us-east-1/s3/aws4_request, " +
            "SignedHeaders=host;x-amz-content-sha256;x-amz-date, " +
            "Signature=c5cf28ac465dac17ec884a4de977dc11faadb6364a0f36fbfad0b52804c5ece3",
            request.Authorization);
    }

    [Fact]
    public void VirtualHostedAddressingPutsTheBucketInTheHostName()
    {
        var client = new S3Client(new HttpClient(), new OffsiteDestination
        {
            Kind = OffsiteDestination.S3Kind, Bucket = "my-backups", Region = "eu-west-2", Prefix = "/nas/",
        });

        var (uri, path) = client.Address("nas/labbytwo-2026-09-28.db", []);

        Assert.Equal("https://my-backups.s3.eu-west-2.amazonaws.com/nas/labbytwo-2026-09-28.db", uri.ToString());
        Assert.Equal("/nas/labbytwo-2026-09-28.db", path);
    }

    [Fact]
    public void EncodingIsRfc3986NotWhateverTheFrameworkPrefers()
    {
        Assert.Equal("a%20b%2Bc%2Fd~e", SigV4.Encode("a b+c/d~e"));
        Assert.Equal("a%20b/c", SigV4.Encode("a b/c", keepSlash: true));
        Assert.Equal("%C3%A9", SigV4.Encode("é"));
    }

    [Fact]
    public void ErrorCodesBecomeSomethingToCheck()
    {
        var client = new S3Client(new HttpClient(), new OffsiteDestination { Kind = OffsiteDestination.S3Kind, Bucket = "b" });
        var uri = new Uri("https://b.s3.amazonaws.com/x");

        var denied = client.Explain(HttpStatusCode.Forbidden,
            "<Error><Code>AccessDenied</Code><Message>Access Denied</Message></Error>", uri);
        Assert.Contains("permissions", denied.Message);
        Assert.False(denied.Transient);

        var signature = client.Explain(HttpStatusCode.Forbidden, "<Error><Code>SignatureDoesNotMatch</Code></Error>", uri);
        Assert.Contains("secret key", signature.Message);

        var plain403 = client.Explain(HttpStatusCode.Forbidden, "<html>nope</html>", uri);
        Assert.Contains("access key", plain403.Message);

        Assert.True(client.Explain(HttpStatusCode.ServiceUnavailable, "", uri).Transient);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(Uri Uri, string Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!, string.Join(",", request.Headers.GetValues("Authorization"))));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("", Encoding.UTF8) });
        }
    }
}
