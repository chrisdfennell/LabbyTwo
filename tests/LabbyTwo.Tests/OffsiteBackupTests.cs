using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Services;
using LabbyTwo.Services.Offsite;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// The off-site chain end to end: a real database, the real backup job, real folders, and
/// an in-memory S3 that checks every request is signed and keeps what it is sent. What is
/// pinned down is what would hurt: which old copies get deleted (and that nothing else
/// ever is), that a failing destination alerts once rather than nightly, and that secrets
/// are not sitting in the database in the clear.
/// </summary>
public sealed class OffsiteBackupTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly RecordingChannel _channel = new();
    private readonly FakeS3 _s3 = new();

    public OffsiteBackupTests()
    {
        Directory.CreateDirectory(_directory);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient(ProviderHttp.TransferClientName).ConfigurePrimaryHttpMessageHandler(() => _s3);
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);

        services.AddSingleton<IConnectionProvider>(_channel);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<OffsiteSettingsStore>();
        services.AddSingleton<OffsiteBackups>();
        services.AddSingleton<BackupJob>();
        _services = services.BuildServiceProvider();

        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        Get<OffsiteBackups>().RetryDelays = [TimeSpan.Zero, TimeSpan.Zero];
        Get<OffsiteBackups>().BundleIterations = 1000;
    }

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    private string Folder(string name)
    {
        var path = Path.Combine(_directory, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private OffsiteDestination S3(int keep = 14, RetentionUnit unit = RetentionUnit.Copies) => new()
    {
        Kind = OffsiteDestination.S3Kind, Name = "Bucket", Endpoint = "http://s3.test", Region = "us-east-1",
        Bucket = "home", Prefix = "labbytwo", AccessKey = "AKID", SecretKey = "secret/key", PathStyle = true,
        Keep = keep, KeepUnit = unit,
    };

    private async Task WithChannelAsync() =>
        await Get<ConfigStore>().SaveConnectionAsync(new Connection { Provider = "recording", Name = "Phone" });

    // ---- Retention, the part that deletes things ---------------------------------------

    [Fact]
    public void RetentionKeepsTheNewestCopiesAndIgnoresEverythingElse()
    {
        var names = new[]
        {
            "labbytwo-2026-09-28.db", "labbytwo-2026-09-27.db", "labbytwo-2026-09-26.db", "labbytwo-2026-09-25.db",
            "holiday-photos.zip", "labbytwo-latest.db", "labbytwo-2026-09-01.db.partial",
        };

        var doomed = Retention.ToDelete(names, 2, RetentionUnit.Copies, new DateOnly(2026, 9, 28));

        Assert.Equal(["labbytwo-2026-09-26.db", "labbytwo-2026-09-25.db"], doomed);
    }

    [Fact]
    public void ADayWithBothKindsOfCopyCountsOnce()
    {
        // The night a passphrase is set, that day has a .db and an .l2backup. "Keep 2"
        // still means two days, not one day twice.
        var names = new[] { "labbytwo-2026-09-28.l2backup", "labbytwo-2026-09-28.db", "labbytwo-2026-09-27.db", "labbytwo-2026-09-26.db" };

        Assert.Equal(["labbytwo-2026-09-26.db"], Retention.ToDelete(names, 2, RetentionUnit.Copies, new DateOnly(2026, 9, 28)));
    }

    [Fact]
    public void RetentionByDaysNeverDeletesTheOnlyCopy()
    {
        var names = new[] { "labbytwo-2026-08-01.db", "labbytwo-2026-07-31.db" };

        // Seven days, on a NAS that has been off since August: everything is "too old",
        // and deleting all of it would leave nothing.
        Assert.Equal(["labbytwo-2026-07-31.db"], Retention.ToDelete(names, 7, RetentionUnit.Days, new DateOnly(2026, 9, 28)));
        Assert.Empty(Retention.ToDelete(["labbytwo-2026-09-22.db", "labbytwo-2026-09-28.db"], 7, RetentionUnit.Days, new DateOnly(2026, 9, 28)));
        Assert.Equal(["labbytwo-2026-09-21.db"],
            Retention.ToDelete(["labbytwo-2026-09-21.db", "labbytwo-2026-09-28.db"], 7, RetentionUnit.Days, new DateOnly(2026, 9, 28)));
    }

    [Fact]
    public void KeepZeroKeepsEverything() =>
        Assert.Empty(Retention.ToDelete(["labbytwo-2020-01-01.db", "labbytwo-2026-09-28.db"], 0, RetentionUnit.Copies, new DateOnly(2026, 9, 28)));

    // ---- Folders ------------------------------------------------------------------------

    [Fact]
    public async Task AFolderGetsTheCopyAndLosesOnlyItsOwnOldOnes()
    {
        var folder = Folder("usb");
        foreach (var day in new[] { "2020-01-01", "2020-01-02", "2020-01-03" })
            await File.WriteAllTextAsync(Path.Combine(folder, $"labbytwo-{day}.db"), "old");
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "not ours");

        await Get<OffsiteSettingsStore>().SaveDestinationsAsync(
            [new OffsiteDestination { Name = "USB", Path = folder, Keep = 2 }]);

        await Get<BackupJob>().RunAsync(default);

        var left = Directory.EnumerateFiles(folder).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["labbytwo-2020-01-03.db", $"labbytwo-{Today}.db", "notes.txt"], left);

        // A working database, not just a file of the right name.
        await using var copy = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(folder, $"labbytwo-{Today}.db"), Pooling = false,
        }.ToString());
        await copy.OpenAsync();
        var cmd = copy.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM app_settings";
        Assert.True(Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0);

        var status = (await Get<OffsiteSettingsStore>().StatusesAsync()).Values.Single();
        Assert.True(status.Ok);
        Assert.Equal($"labbytwo-{Today}.db", status.LastName);
    }

    [Fact]
    public async Task AMissingFolderIsReportedNotCreated()
    {
        var missing = Path.Combine(_directory, "not-mounted");
        await Get<OffsiteSettingsStore>().SaveDestinationsAsync([new OffsiteDestination { Name = "NAS share", Path = missing }]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Get<BackupJob>().RunAsync(default));

        Assert.Contains("NAS share", ex.Message);
        Assert.Contains("not mapped into the container", ex.Message);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task OneBrokenDestinationDoesNotStopTheOthers()
    {
        var good = Folder("good");
        await Get<OffsiteSettingsStore>().SaveDestinationsAsync(
        [
            new OffsiteDestination { Name = "Broken", Path = Path.Combine(_directory, "nope") },
            new OffsiteDestination { Name = "Good", Path = good },
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Get<BackupJob>().RunAsync(default));

        Assert.True(File.Exists(Path.Combine(good, $"labbytwo-{Today}.db")));
    }

    // ---- S3 -----------------------------------------------------------------------------

    [Fact]
    public async Task S3GetsASignedUploadAndPrunesOnlyUnderItsPrefix()
    {
        // More than a page of listing, so continuation tokens are followed.
        foreach (var day in Enumerable.Range(1, 5))
            _s3.Objects[$"labbytwo/labbytwo-2020-01-0{day}.db"] = [1];
        _s3.Objects["labbytwo/readme.txt"] = [1];
        _s3.Objects["labbytwo/other-host/labbytwo-2020-01-01.db"] = [1];
        _s3.Objects["elsewhere/labbytwo-2020-01-01.db"] = [1];

        await Get<OffsiteSettingsStore>().SaveDestinationsAsync([S3(keep: 3)]);

        var results = await Get<BackupJob>().RunNowAsync(default);

        Assert.True(Assert.Single(results).Ok, results[0].Message);
        Assert.Equal(
        [
            "elsewhere/labbytwo-2020-01-01.db",
            "labbytwo/labbytwo-2020-01-04.db",
            "labbytwo/labbytwo-2020-01-05.db",
            $"labbytwo/labbytwo-{Today}.db",
            "labbytwo/other-host/labbytwo-2020-01-01.db",
            "labbytwo/readme.txt",
        ], _s3.Objects.Keys.Order(StringComparer.Ordinal));
        Assert.True(_s3.ListPages > 1);
        Assert.Equal(new FileInfo(Directory.EnumerateFiles(Path.Combine(_directory, "backups")).Single()).Length,
            _s3.Objects[$"labbytwo/labbytwo-{Today}.db"].Length);
    }

    [Fact]
    public async Task ABusyStoreIsTriedAgain()
    {
        _s3.FailNextPuts = 2;
        await Get<OffsiteSettingsStore>().SaveDestinationsAsync([S3()]);

        var result = Assert.Single(await Get<BackupJob>().RunNowAsync(default));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(3, _s3.Puts);
    }

    [Fact]
    public async Task ARefusalIsNotRetriedAndSaysWhatToCheck()
    {
        _s3.Deny = true;
        await Get<OffsiteSettingsStore>().SaveDestinationsAsync([S3()]);

        var result = Assert.Single(await Get<BackupJob>().RunNowAsync(default));

        Assert.False(result.Ok);
        Assert.Contains("permissions", result.Message);
        Assert.Equal(1, _s3.Puts);
    }

    [Fact]
    public async Task WithAPassphraseTheBucketGetsAnEncryptedBundleWithTheKeyring()
    {
        await Get<OffsiteSettingsStore>().SavePassphraseAsync("a long enough passphrase");
        await Get<OffsiteSettingsStore>().SaveDestinationsAsync([S3()]);
        // DataProtection writes its key when something is first protected, which saving
        // the secret key above has just done.
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(_directory, "keys")));

        var result = Assert.Single(await Get<BackupJob>().RunNowAsync(default));
        Assert.True(result.Ok, result.Message);

        var bundle = _s3.Objects[$"labbytwo/labbytwo-{Today}.l2backup"];
        Assert.False(_s3.Objects.ContainsKey($"labbytwo/labbytwo-{Today}.db"));

        using var zipBytes = new MemoryStream();
        await BackupBundle.DecryptAsync(new MemoryStream(bundle), zipBytes, "a long enough passphrase");
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(zipBytes.ToArray()));
        Assert.NotNull(zip.GetEntry(BackupBundle.DatabaseEntry));
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("keys/", StringComparison.Ordinal));

        // The temporary bundle is not left lying about beside the local backups.
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(_directory, "backups")), f => f.EndsWith(".tmp"));
    }

    // ---- Alerting -----------------------------------------------------------------------

    [Fact]
    public async Task AFailingDestinationAlertsOnceAndAgainOnlyWhenItRecovers()
    {
        await WithChannelAsync();
        var folder = Path.Combine(_directory, "flaky");
        await Get<OffsiteSettingsStore>().SaveDestinationsAsync([new OffsiteDestination { Name = "USB", Path = folder }]);

        for (var night = 0; night < 3; night++)
            await Get<BackupJob>().RunNowAsync(default);

        var failure = Assert.Single(_channel.Sent);
        Assert.Equal(AlertLevel.Down, failure.Level);
        Assert.Contains("USB", failure.Title);
        Assert.Contains("no good copy there yet", failure.Body);

        Directory.CreateDirectory(folder);
        await Get<BackupJob>().RunNowAsync(default);
        await Get<BackupJob>().RunNowAsync(default);

        Assert.Equal(2, _channel.Sent.Count);
        Assert.Equal(AlertLevel.Up, _channel.Sent[1].Level);
    }

    [Fact]
    public void AFailureThatCouldNotBeSentIsTriedAgainNextTime()
    {
        // Quiet hours, or no channel yet: nothing went out, so nothing was announced, and
        // the next failure is still news.
        var destination = new OffsiteDestination { Name = "B2" };
        var failed = new OffsiteBackups.Result(destination, false, "Access denied.", 0, "x.db");
        var now = DateTimeOffset.Now;

        var (first, alert) = OffsiteBackups.Next(null, "B2", failed, now);
        Assert.NotNull(alert);
        Assert.False(first.FailureAnnounced);

        var (_, again) = OffsiteBackups.Next(first, "B2", failed, now.AddDays(1));
        Assert.NotNull(again);

        var (_, quiet) = OffsiteBackups.Next(first with { FailureAnnounced = true }, "B2", failed, now.AddDays(1));
        Assert.Null(quiet);

        // A recovery nobody was told about the failure of is not news either.
        var ok = failed with { Ok = true, Message = "Copied." };
        Assert.Null(OffsiteBackups.Next(first, "B2", ok, now.AddDays(2)).Alert);
    }

    // ---- Settings -----------------------------------------------------------------------

    [Fact]
    public async Task SettingsRoundTripWithSecretsEncryptedAtRest()
    {
        var destination = S3(keep: 30, unit: RetentionUnit.Days) with { SecretKey = "wJalrXUtnFEMI/K7MDENG" };
        await Get<OffsiteSettingsStore>().SaveDestinationsAsync([destination]);
        await Get<OffsiteSettingsStore>().SavePassphraseAsync("hunter2 hunter2 hunter2");

        // A fresh store over the same database — what a restart sees.
        var reopened = new OffsiteSettingsStore(new AppSettingsStore(Get<Db>()), Get<IDataProtectionProvider>());
        Assert.Equal(destination, Assert.Single(await reopened.DestinationsAsync()));
        Assert.Equal("hunter2 hunter2 hunter2", await reopened.PassphraseAsync());

        var raw = await Get<AppSettingsStore>().AllAsync();
        Assert.DoesNotContain("wJalrXUtnFEMI", raw.Get(OffsiteSettingsStore.DestinationsKey));
        Assert.DoesNotContain("hunter2", raw.Get(OffsiteSettingsStore.PassphraseKey));
        Assert.Contains("AKID", raw.Get(OffsiteSettingsStore.DestinationsKey));
    }

    [Fact]
    public async Task ASecretFromAnotherKeyringComesBackEmptyRatherThanWrong()
    {
        await Get<OffsiteSettingsStore>().SaveDestinationsAsync([S3()]);

        var stranger = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_directory, "other-keys")));
        var elsewhere = new OffsiteSettingsStore(new AppSettingsStore(Get<Db>()), stranger);

        var destination = Assert.Single(await elsewhere.DestinationsAsync());
        Assert.Equal("", destination.SecretKey);
        Assert.NotNull(destination.Problem());
    }

    // ---- Doubles ------------------------------------------------------------------------

    /// <summary>
    /// Enough of S3 for put, list and delete: a dictionary, with two-key pages so the
    /// continuation token is exercised, and a check that the body matches its signed hash.
    /// </summary>
    private sealed class FakeS3 : HttpMessageHandler
    {
        public SortedDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public int Puts { get; private set; }
        public int ListPages { get; private set; }
        public int FailNextPuts { get; set; }
        public bool Deny { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var authorization = request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : "";
            if (!authorization.StartsWith("AWS4-HMAC-SHA256 Credential=AKID/", StringComparison.Ordinal))
                return Error(HttpStatusCode.Forbidden, "MissingSecurityHeader");

            // Path style: /bucket/key
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            Assert.StartsWith("/home", path);
            var key = path.Length > "/home/".Length ? path["/home/".Length..] : "";

            if (request.Method == HttpMethod.Put)
            {
                Puts++;
                if (Deny)
                    return Error(HttpStatusCode.Forbidden, "AccessDenied");
                if (FailNextPuts > 0)
                {
                    FailNextPuts--;
                    return Error(HttpStatusCode.ServiceUnavailable, "SlowDown");
                }

                var body = await request.Content!.ReadAsByteArrayAsync(ct);
                var claimed = request.Headers.GetValues("x-amz-content-sha256").Single();
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(body)), claimed);
                Objects[key] = body;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Delete)
            {
                Objects.Remove(key);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            Assert.Equal("2", query["list-type"]);
            ListPages++;
            var prefix = query["prefix"] ?? "";
            var after = query["continuation-token"];
            var page = Objects.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Where(k => after is null || string.CompareOrdinal(k, after) > 0)
                .Take(3)
                .ToList();
            var more = page.Count == 3;
            if (more)
                page.RemoveAt(2);

            var xml = new StringBuilder("<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">");
            xml.Append($"<IsTruncated>{(more ? "true" : "false")}</IsTruncated>");
            if (more)
                xml.Append($"<NextContinuationToken>{page[^1]}</NextContinuationToken>");
            foreach (var k in page)
                xml.Append($"<Contents><Key>{k}</Key><Size>{Objects[k].Length}</Size><LastModified>2026-09-28T03:00:00.000Z</LastModified></Contents>");
            xml.Append("</ListBucketResult>");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml.ToString()) };
        }

        private static HttpResponseMessage Error(HttpStatusCode status, string code) => new(status)
        {
            Content = new StringContent($"<Error><Code>{code}</Code><Message>{code}</Message></Error>"),
        };
    }

    private sealed class RecordingChannel : IAlertChannel
    {
        public string Type => "recording";
        public string DisplayName => "Recording channel";
        public string Icon => "📼";
        public string Description => "Test double.";
        public IReadOnlyList<FieldSpec> Fields => [];
        public List<Alert> Sent { get; } = [];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.Zero));

        public Task SendAsync(Connection channel, Alert alert, CancellationToken ct)
        {
            lock (Sent)
                Sent.Add(alert);
            return Task.CompletedTask;
        }
    }
}
