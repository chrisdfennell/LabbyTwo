using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Services;
using LabbyTwo.Services.WebPush;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Tests;

/// <summary>
/// Web Push, from the bytes up: the encryption against the RFC's own worked example, the
/// VAPID token against the key that signed it, and the sender and channel against a push
/// service that answers whatever the test tells it to.
/// </summary>
public sealed class WebPushTests : IDisposable
{
    // RFC 8291, Section 5 and Appendix A.
    private const string VectorPlaintext = "V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24";
    private const string VectorAsPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string VectorAsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string VectorUaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string VectorUaPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    private const string VectorSalt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string VectorAuth = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string VectorCek = "oIhVW04MRdy2XN9CiKLxTg";
    private const string VectorNonce = "4h_95klXJ5E_qnoN";
    private const string VectorMessage =
        "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly FakePushService _push = new();

    /// <summary>A push service that records each request and answers with whatever is queued.</summary>
    private sealed class FakePushService : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, byte[] Body)> Requests { get; } = [];
        public Queue<Func<HttpResponseMessage>> Answers { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            lock (Requests)
                Requests.Add((request, body));
            return Answers.Count > 0 ? Answers.Dequeue()() : new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    public WebPushTests()
    {
        Directory.CreateDirectory(_directory);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient(ProviderHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => _push);
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);

        services.AddSingleton<BrowserPushProvider>();
        services.AddSingleton<IConnectionProvider>(sp => sp.GetRequiredService<BrowserPushProvider>());
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<PushSubscriptionStore>();
        services.AddSingleton<VapidKeys>();
        services.AddSingleton<WebPushSender>();
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private static byte[] B(string base64Url) => WebPushCrypto.FromBase64Url(base64Url);

    // ---------- RFC 8291 ----------

    [Fact]
    public void EncryptionReproducesTheRfcExampleByteForByte()
    {
        var asPublic = B(VectorAsPublic);
        using var serverKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = B(VectorAsPrivate),
            Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..] },
        });

        var message = WebPushCrypto.Encrypt(B(VectorPlaintext), B(VectorUaPublic), B(VectorAuth), serverKey, B(VectorSalt));

        Assert.Equal(VectorMessage, WebPushCrypto.ToBase64Url(message));
    }

    [Fact]
    public void TheRfcExampleDecryptsWithTheUserAgentsKey()
    {
        // The other direction, as a browser would: only the receiver's private key and the
        // auth secret, with the salt and sender key read out of the message header.
        var plaintext = Decrypt(B(VectorMessage), UserAgent(VectorUaPublic, VectorUaPrivate), B(VectorAuth), out var cek, out var nonce);

        Assert.Equal("When I grow up, I want to be a watermelon", Encoding.UTF8.GetString(plaintext));
        Assert.Equal(VectorCek, WebPushCrypto.ToBase64Url(cek));
        Assert.Equal(VectorNonce, WebPushCrypto.ToBase64Url(nonce));
    }

    [Fact]
    public void AFreshMessageRoundTrips()
    {
        using var userAgent = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var serverKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var auth = RandomNumberGenerator.GetBytes(16);
        var text = Encoding.UTF8.GetBytes("🔴 NAS is down — it stopped answering.");

        var message = WebPushCrypto.Encrypt(text, WebPushCrypto.PublicKeyBytes(userAgent), auth, serverKey,
            RandomNumberGenerator.GetBytes(16));

        Assert.Equal(text, Decrypt(message, userAgent, auth, out _, out _));
    }

    // ---------- VAPID ----------

    [Fact]
    public void TheVapidTokenIsAnEs256JwtThatVerifiesWithThePublicKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var expires = DateTimeOffset.UtcNow.AddHours(12);
        var endpoint = new Uri("https://fcm.googleapis.com/fcm/send/abc123");

        var token = Vapid.Token(key, Vapid.Audience(endpoint), "mailto:someone@example.com", expires);

        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);

        using var header = JsonDocument.Parse(B(parts[0]));
        Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());

        using var claims = JsonDocument.Parse(B(parts[1]));
        Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal(expires.ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());
        Assert.Equal("mailto:someone@example.com", claims.RootElement.GetProperty("sub").GetString());

        // Verified with only the public half, as the push service does: rebuilt from the
        // same 65 bytes that go into the Authorization header's k= parameter.
        var publicBytes = WebPushCrypto.PublicKeyBytes(key);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicBytes[1..33], Y = publicBytes[33..] },
        });
        var signature = B(parts[2]);
        Assert.Equal(64, signature.Length);
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), signature,
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public async Task TheKeyPairIsMadeOnceKeptAcrossRestartsAndStoredEncrypted()
    {
        var first = await Get<VapidKeys>().GetAsync();

        // A restart: new instances over the same database and keyring.
        var restarted = new VapidKeys(new AppSettingsStore(Get<Db>()), Get<PushSubscriptionStore>(),
            Get<IDataProtectionProvider>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<VapidKeys>.Instance);
        var second = await restarted.GetAsync();

        Assert.Equal(first.PublicKey, second.PublicKey);
        Assert.Equal(65, B(first.PublicKey).Length);

        var stored = await new AppSettingsStore(Get<Db>()).GetAsync(VapidKeys.PrivateKeySetting);
        var d = WebPushCrypto.ToBase64Url(first.Key.ExportParameters(true).D!);
        Assert.NotEmpty(stored);
        Assert.DoesNotContain(d, stored);
    }

    // ---------- Sending ----------

    private async Task<(PushSubscription Device, ECDiffieHellman Key, byte[] Auth)> SubscribeAsync(string name = "Chris's phone")
    {
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var auth = RandomNumberGenerator.GetBytes(16);
        var device = await Get<PushSubscriptionStore>().SaveAsync(new PushSubscription
        {
            Name = name,
            Endpoint = $"https://push.example.com/send/{Guid.NewGuid():n}",
            P256dh = WebPushCrypto.ToBase64Url(WebPushCrypto.PublicKeyBytes(key)),
            Auth = WebPushCrypto.ToBase64Url(auth),
        });
        return (device, key, auth);
    }

    [Fact]
    public async Task ASentMessageCarriesVapidAndDecryptsOnTheDevice()
    {
        var (device, key, auth) = await SubscribeAsync();

        var outcome = await Get<WebPushSender>().SendAsync(device,
            new PushMessage("🔴 NAS is down", "It stopped answering.", Tag: "status:nas", Urgency: "high", Renotify: true),
            CancellationToken.None);

        Assert.Equal(PushResult.Delivered, outcome.Result);
        var (request, body) = Assert.Single(_push.Requests);

        Assert.Equal("aes128gcm", Assert.Single(request.Content!.Headers.ContentEncoding));
        Assert.Equal("high", request.Headers.GetValues("Urgency").Single());
        Assert.Equal(WebPushSender.Topic("status:nas"), request.Headers.GetValues("Topic").Single());
        Assert.True(int.Parse(request.Headers.GetValues("TTL").Single()) > 0);

        var authorization = request.Headers.GetValues("Authorization").Single();
        Assert.StartsWith("vapid t=", authorization);
        Assert.Contains($"k={await Get<VapidKeys>().PublicKeyAsync()}", authorization);

        using var payload = JsonDocument.Parse(Decrypt(body, key, auth, out _, out _));
        Assert.Equal("🔴 NAS is down", payload.RootElement.GetProperty("title").GetString());
        Assert.Equal("status:nas", payload.RootElement.GetProperty("tag").GetString());
        Assert.True(payload.RootElement.GetProperty("renotify").GetBoolean());

        Assert.NotNull((await Get<PushSubscriptionStore>().AllAsync()).Single().LastSentAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ADeviceThePushServiceNoLongerKnowsIsDeleted(HttpStatusCode status)
    {
        var (gone, _, _) = await SubscribeAsync("Old phone");
        var (kept, _, _) = await SubscribeAsync("New phone");
        _push.Answers.Enqueue(() => new HttpResponseMessage(status));

        var outcome = await Get<WebPushSender>().SendAsync(gone, new PushMessage("t", "b"), CancellationToken.None);

        Assert.Equal(PushResult.Removed, outcome.Result);
        Assert.Equal(kept.Id, Assert.Single(await Get<PushSubscriptionStore>().AllAsync()).Id);
    }

    [Fact]
    public async Task RateLimitingIsHonouredRatherThanRetried()
    {
        var (device, _, _) = await SubscribeAsync();
        _push.Answers.Enqueue(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return response;
        });

        var sender = Get<WebPushSender>();
        var first = await sender.SendAsync(device, new PushMessage("t", "b"), CancellationToken.None);
        var second = await sender.SendAsync(device, new PushMessage("t", "b"), CancellationToken.None);

        Assert.Equal(PushResult.Failed, first.Result);
        Assert.Equal(PushResult.Failed, second.Result);
        Assert.Single(_push.Requests);
        Assert.Single(await Get<PushSubscriptionStore>().AllAsync());
    }

    [Fact]
    public async Task ATooLargeAnswerIsRetriedOnceWithTheBodyCut()
    {
        var (device, key, auth) = await SubscribeAsync();
        _push.Answers.Enqueue(() => new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge));

        var outcome = await Get<WebPushSender>().SendAsync(device,
            new PushMessage("Weekly summary", new string('x', 900)), CancellationToken.None);

        Assert.Equal(PushResult.Delivered, outcome.Result);
        Assert.Equal(2, _push.Requests.Count);
        using var payload = JsonDocument.Parse(Decrypt(_push.Requests[1].Body, key, auth, out _, out _));
        Assert.True(payload.RootElement.GetProperty("body").GetString()!.Length <= 200);
    }

    [Fact]
    public void AnOversizedBodyIsCutToFitOneMessage()
    {
        var payload = WebPushSender.Payload(new PushMessage("t", string.Concat(Enumerable.Repeat("🌪", 4000))));
        Assert.True(payload.Length <= WebPushCrypto.MaxPlaintext);
    }

    // ---------- The channel ----------

    private async Task<Connection> ChannelAsync(string devices = "")
    {
        var channel = new Connection { Provider = BrowserPushProvider.ProviderType, Name = "Browser push" };
        channel.Settings["devices"] = devices;
        await Get<ConfigStore>().SaveConnectionAsync(channel);
        return channel;
    }

    [Fact]
    public async Task TheChannelKeepsQuietHoursButLetsUrgentAlertsThrough()
    {
        await SubscribeAsync();
        await ChannelAsync();

        // Quiet from an hour ago until an hour from now, with nothing allowed through.
        var now = TimeOnly.FromDateTime(DateTime.Now);
        await Get<AppSettingsStore>().SaveAsync(new Dictionary<string, string>
        {
            [AlertPolicy.FromKey] = now.AddHours(-1).ToString("HH:mm"),
            [AlertPolicy.ToKey] = now.AddHours(1).ToString("HH:mm"),
            [AlertPolicy.ModeKey] = AlertPolicy.Nothing,
        });

        var alerts = Get<AlertService>();
        Assert.Equal(0, await alerts.BroadcastAsync(new Alert(AlertLevel.Down, "NAS is down", "gone"), CancellationToken.None));
        Assert.Empty(_push.Requests);

        Assert.Equal(1, await alerts.BroadcastAsync(
            new Alert(AlertLevel.Info, "Tornado Warning", "Take shelter") { Urgent = true }, CancellationToken.None));
        Assert.Single(_push.Requests);
    }

    [Fact]
    public async Task TheChannelSendsOnlyToTheDevicesItNames()
    {
        var (phone, phoneKey, phoneAuth) = await SubscribeAsync("Chris's phone");
        await SubscribeAsync("Office PC");
        var channel = await ChannelAsync(" chris's PHONE ");

        await Get<BrowserPushProvider>().SendAsync(channel,
            new Alert(AlertLevel.Info, "Tornado Warning", "Take shelter") { Urgent = true, Tag = "weather" }, CancellationToken.None);

        var (request, body) = Assert.Single(_push.Requests);
        Assert.Equal(phone.Endpoint, request.RequestUri!.ToString());
        using var payload = JsonDocument.Parse(Decrypt(body, phoneKey, phoneAuth, out _, out _));
        Assert.True(payload.RootElement.GetProperty("urgent").GetBoolean());
    }

    [Fact]
    public async Task AChannelWithNoDevicesSaysSoInsteadOfSucceeding()
    {
        var channel = await ChannelAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Get<BrowserPushProvider>().SendAsync(channel, new Alert(AlertLevel.Down, "x", "y"), CancellationToken.None));
        Assert.Contains("Notify this device", ex.Message);
    }

    // ---------- Storage ----------

    [Fact]
    public async Task SubscribingTwiceFromOneBrowserUpdatesTheRow()
    {
        var (device, _, _) = await SubscribeAsync("Phone");
        await Get<PushSubscriptionStore>().SaveAsync(new PushSubscription
        {
            Name = "Chris's phone",
            Endpoint = device.Endpoint,
            P256dh = device.P256dh,
            Auth = device.Auth,
        });

        var only = Assert.Single(await Get<PushSubscriptionStore>().AllAsync());
        Assert.Equal(device.Id, only.Id);
        Assert.Equal("Chris's phone", only.Name);
    }

    [Fact]
    public async Task TheMigrationAddsTheTableToAnExistingDatabase()
    {
        var path = Path.Combine(_directory, "old.db");

        // A database as the release before this one left it: every earlier migration applied,
        // somebody's connection in it, and no push table.
        var fresh = new Db(Options.Create(new LabbyOptions { DatabasePath = path }), _services.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>());
        await fresh.EnsureSchemaAsync();
        await using (var connection = await fresh.OpenAsync())
        {
            var rewind = connection.CreateCommand();
            rewind.CommandText = """
                DROP TABLE push_subscriptions;
                INSERT INTO connections (id, provider, name) VALUES ('nas', 'ping', 'NAS');
                PRAGMA user_version = 12;
                """;
            await rewind.ExecuteNonQueryAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        }

        var upgraded = new Db(Options.Create(new LabbyOptions { DatabasePath = path }), _services.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>());
        var store = new PushSubscriptionStore(upgraded);
        await store.SaveAsync(new PushSubscription { Name = "Phone", Endpoint = "https://push.example.com/1", P256dh = "p", Auth = "a" });

        Assert.Single(await store.AllAsync());
        await using var check = await upgraded.OpenAsync();
        var read = check.CreateCommand();
        read.CommandText = "SELECT (SELECT name FROM connections WHERE id = 'nas') || ':' || (SELECT user_version FROM pragma_user_version)";
        Assert.Equal("NAS:13", (string)(await read.ExecuteScalarAsync())!);
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(check);
    }

    // ---------- Helpers ----------

    private static ECDiffieHellman UserAgent(string publicKey, string privateKey)
    {
        var q = B(publicKey);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = B(privateKey),
            Q = new ECPoint { X = q[1..33], Y = q[33..] },
        });
    }

    /// <summary>What a browser does with a push message: RFC 8291 receiver side, one record.</summary>
    private static byte[] Decrypt(byte[] message, ECDiffieHellman userAgent, byte[] auth, out byte[] cek, out byte[] nonce)
    {
        var salt = message[..16];
        var recordSize = BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(16, 4));
        Assert.Equal((uint)WebPushCrypto.RecordSize, recordSize);
        int idLength = message[20];
        var serverPublic = message[21..(21 + idLength)];
        var ciphertext = message[(21 + idLength)..];

        using var server = WebPushCrypto.ImportPublicKey(serverPublic);
        var shared = userAgent.DeriveRawSecretAgreement(server.PublicKey);
        (cek, nonce) = WebPushCrypto.DeriveKeys(shared, auth, WebPushCrypto.PublicKeyBytes(userAgent), serverPublic, salt);

        var padded = new byte[ciphertext.Length - 16];
        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, ciphertext[..^16], ciphertext[^16..], padded);

        // Strip padding back to the delimiter, which for the last record is 0x02.
        var end = Array.LastIndexOf(padded, (byte)0x02);
        Assert.True(end >= 0 && padded[(end + 1)..].All(b => b == 0));
        return padded[..end];
    }
}
