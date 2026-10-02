#pragma warning disable BL0006 // The interactive test renderer is a Renderer, which is the whole point of it.
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// Test values for token formats, built at run time. Written out whole in the source, a
/// fake GitHub or Slack token is exactly what a secret scanner on a push would stop — and
/// rightly — so the shapes are assembled from parts instead.
/// </summary>
internal static class FakeSecrets
{
    public static readonly string GitHub = "gh" + "p_" + new string('A', 18) + new string('b', 18);
    public static readonly string GitHubFine = "github" + "_pat_" + new string('C', 22) + "_" + new string('d', 30);
    public static readonly string Slack = "xo" + "xb-" + "1234567890-" + new string('e', 24);
    public static readonly string SlackHook = "https://hooks.slack" + ".com/services/T" + "0000000" + "/B" + "1111111/" + new string('f', 24);
    public static readonly string Aws = "AK" + "IA" + "ABCDEFGHIJKLMNOP";
    public static readonly string Discord = "https://discord" + ".com/api/webhooks/" + "123456789012345678/" + new string('g', 40);
    public static readonly string Telegram = "123456789:" + "AA" + new string('h', 33);
    public static readonly string Tailscale = "tskey" + "-auth-" + "k1234567CNTRL-" + new string('i', 20);
    public static readonly string Jwt = "ey" + "JhbGciOiJIUzI1NiJ9" + ".ey" + "JzdWIiOiIxMjM0NTY3ODkwIn0" + "." + "dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
    public static readonly string PrivateKey = "-----BEGIN " + "OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjE\n-----END OPENSSH PRIVATE KEY-----";

    /// <summary>Random-looking, the way a generated API key is.</summary>
    public const string RandomKey = "f3K9qLm2Xv8RtY1pZc7WbN4sHj6DgA0e";

    /// <summary>The VPN password from the compose file that started this, more or less. Distinctive, so a leak is found.</summary>
    public const string VpnPassword = "Zq7-Plaintext-VPN-Pa55phrase";
    public const string VpnUser = "p9876543vpnuser";
}

/// <summary>
/// What counts as a plain-text secret and what does not — the table that decides whether
/// the list is worth reading. Every false positive here is a reason somebody stops looking.
/// </summary>
public class SecretScanTests
{
    public static TheoryData<string, string, string, SecretSeverity> Flagged => new()
    {
        // The compose file that started it.
        { "OPENVPN_USER", FakeSecrets.VpnUser, "VPN username", SecretSeverity.Medium },
        { "OPENVPN_PASSWORD", FakeSecrets.VpnPassword, "VPN password", SecretSeverity.High },
        { "WIREGUARD_PRIVATE_KEY", "yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=", "WireGuard private key", SecretSeverity.High },
        { "CF_API_TOKEN", FakeSecrets.RandomKey, "Cloudflare API token", SecretSeverity.High },
        { "TUNNEL_TOKEN", "eyJhIjoiMTIzNDU2Nzg5MCIsInQiOiJhYmNkZWYifQ", "Cloudflare tunnel token", SecretSeverity.High },
        { "TS_AUTHKEY", FakeSecrets.Tailscale, "Tailscale auth key", SecretSeverity.High },
        { "PLEX_CLAIM", "claim-aBcDeFgHiJkLmNoPqRsT", "Plex claim token", SecretSeverity.Low },

        // Names with a secret word in them.
        { "MYSQL_ROOT_PASSWORD", "rootpw", "password", SecretSeverity.Medium },
        { "DB_PASS", "abc123", "password", SecretSeverity.Medium },
        { "dbPassword", "hunter22", "password", SecretSeverity.Medium },
        { "ADMINPASSWORD", "hunter22", "password", SecretSeverity.Medium },
        { "SMTP_PASSWD", "123456789", "password", SecretSeverity.Medium },
        { "API_KEY", FakeSecrets.RandomKey, "API key", SecretSeverity.High },
        { "SONARR_APIKEY", "0123456789abcdef0123456789abcdef", "API key", SecretSeverity.High },
        { "SECRET_KEY", FakeSecrets.RandomKey, "secret", SecretSeverity.High },
        { "SESSION_SECRET", FakeSecrets.RandomKey, "secret", SecretSeverity.High },
        { "JWT_SIGNING_KEY", FakeSecrets.RandomKey, "random-looking key or secret", SecretSeverity.High },
        { "COOKIE_SALT", FakeSecrets.RandomKey, "random-looking key or secret", SecretSeverity.High },
        { "GOTIFY_TOKEN", "Abc.d3fG", "token", SecretSeverity.Medium },
        { "AWS_SECRET_ACCESS_KEY", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYzzzzKEY1", "secret", SecretSeverity.High },
        { "GOOGLE_CREDENTIALS", "{\"type\":\"service_account\"}", "credentials", SecretSeverity.Medium },
        { "LICENSE_KEY", "ABCD-1234-EFGH", "key", SecretSeverity.Low },
        { "ALERT_WEBHOOK", "https://example.com/hook/abc", "webhook URL", SecretSeverity.Medium },
        { "NOTIFY_URL", "https://ntfy.example/topic?token=tk_0123456789abcdef", "URL with a token in it", SecretSeverity.Medium },

        // The value's shape, whatever it is called.
        { "FOO", FakeSecrets.GitHub, "GitHub token", SecretSeverity.High },
        { "GH", FakeSecrets.GitHubFine, "GitHub token", SecretSeverity.High },
        { "BOT", FakeSecrets.Slack, "Slack token", SecretSeverity.High },
        { "HOOK", FakeSecrets.SlackHook, "Slack webhook", SecretSeverity.High },
        { "AWS_ACCESS_KEY_ID", FakeSecrets.Aws, "AWS access key", SecretSeverity.High },
        { "DISCORD", FakeSecrets.Discord, "Discord webhook", SecretSeverity.High },
        { "TG", FakeSecrets.Telegram, "Telegram bot token", SecretSeverity.High },
        { "X", FakeSecrets.Jwt, "JWT", SecretSeverity.High },
        { "SSH", FakeSecrets.PrivateKey, "private key", SecretSeverity.High },
        { "DATABASE_URL", "postgres://app:pw123@db:5432/app", "URL with a password", SecretSeverity.Medium },
        { "REDIS", "redis://:s3cret@redis:6379/0", "URL with a password", SecretSeverity.Medium },
        { "ConnectionStrings__Default", "Server=db;Database=app;User Id=sa;Password=pw123;", "connection string with a password", SecretSeverity.Medium },

        // Placeholders: still a secret, and a guessable one.
        { "ADMIN_PASSWORD", "changeme", "placeholder password", SecretSeverity.Medium },
        { "POSTGRES_PASSWORD", "password", "placeholder password", SecretSeverity.Medium },
        { "OPENVPN_PASSWORD", "your_password_here", "VPN password", SecretSeverity.Medium },
    };

    [Theory]
    [MemberData(nameof(Flagged))]
    public void Secrets_are_found(string name, string value, string kind, SecretSeverity severity)
    {
        var verdict = SecretScan.Classify(name, value);

        Assert.NotNull(verdict);
        Assert.Equal(kind, verdict.Kind);
        Assert.Equal(severity, verdict.Severity);
    }

    [Theory]
    [InlineData("ADMIN_PASSWORD", "changeme")]
    [InlineData("OPENVPN_PASSWORD", "your_password_here")]
    [InlineData("API_TOKEN", "<your token>")]
    public void Placeholders_are_marked_weak(string name, string value) =>
        Assert.True(SecretScan.Classify(name, value)?.Weak);

    [Theory]
    // The usual settings.
    [InlineData("PUID", "1000")]
    [InlineData("PGID", "1000")]
    [InlineData("TZ", "Europe/London")]
    [InlineData("UMASK", "022")]
    [InlineData("PWD", "/app")]
    [InlineData("VPN_SERVICE_PROVIDER", "mullvad")]
    // Pointing at a file is the fix, so it is never a finding.
    [InlineData("DB_PASSWORD_FILE", "/run/secrets/db_password")]
    [InlineData("MYSQL_ROOT_PASSWORD_FILE", "/run/secrets/mysql_root")]
    [InlineData("FILE__PASSWORD", "/run/secrets/pw")]
    [InlineData("FILE__OPENVPN_PASSWORD", "/config/vpn-pass")]
    [InlineData("WHATEVER", "/run/secrets/anything")]
    // Empty, or a reference that was never expanded.
    [InlineData("MYSQL_PASSWORD", "")]
    [InlineData("MYSQL_PASSWORD", "   ")]
    [InlineData("DB_PASSWORD", "${DB_PASSWORD}")]
    [InlineData("DATABASE_URL", "postgres://app:${DB_PASSWORD}@db/app")]
    // Names about a secret rather than holding one.
    [InlineData("AUTH_METHOD", "oidc")]
    [InlineData("TOKEN_TTL", "3600")]
    [InlineData("SESSION_TIMEOUT", "30m")]
    [InlineData("SECRET_NAME", "db-creds")]
    [InlineData("DB_USER", "app")]
    [InlineData("AUTH_ENABLED", "true")]
    [InlineData("AUTH_HOST", "authelia")]
    [InlineData("PRIVATE_KEY_PATH", "/certs/key.pem")]
    [InlineData("TRAEFIK_AUTH_USERSFILE", "/etc/traefik/users")]
    // Public keys and checksums, which official images set themselves.
    [InlineData("GPG_KEY", "A035C8C19219BA821ECEA86B64E628F8D684696D")]
    [InlineData("PUBLIC_KEY", "f3K9qLm2Xv8RtY1pZc7WbN4sHj6DgA0e")]
    [InlineData("PYTHON_SHA256", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("NGINX_GPGKEY", "573BFD6B3D8FBC641079A6ABABF5BD827BD9BF62")]
    // Settings under a name that only might be a secret.
    [InlineData("AUTH", "basic")]
    [InlineData("SESSION_STORE", "redis")]
    [InlineData("SSL_KEY", "/certs/privkey.pem")]
    [InlineData("API_KEY_HEADER", "X-Api-Key")]
    [InlineData("AUTH_URL", "https://auth.example.com/login")]
    [InlineData("PASSTHROUGH", "yes")]
    [InlineData("COMPASS", "north")]
    public void Settings_are_not_secrets(string name, string value) =>
        Assert.Null(SecretScan.Classify(name, value));

    private static string Inspect(string name, string image, string[] env, string[]? cmd = null,
        Dictionary<string, string>? labels = null, string[]? entrypoint = null) =>
        JsonSerializer.Serialize(new
        {
            Id = name + "0123456789abcdef",
            Name = "/" + name,
            Config = new { Image = image, Env = env, Cmd = cmd, Entrypoint = entrypoint, Labels = labels ?? [] },
        });

    internal static string Gluetun(string password = FakeSecrets.VpnPassword) => Inspect("gluetun", "qmcgaw/gluetun:latest",
    [
        "VPN_SERVICE_PROVIDER=mullvad", "VPN_TYPE=openvpn", $"OPENVPN_USER={FakeSecrets.VpnUser}",
        $"OPENVPN_PASSWORD={password}", "TZ=Europe/London", "PUID=1000", "PGID=1000",
        "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
    ], labels: new() { ["com.docker.compose.project"] = "vpn", ["com.docker.compose.service"] = "gluetun" });

    [Fact]
    public void The_OpenVPN_compose_file_gives_exactly_the_login()
    {
        var findings = SecretScan.Find(SecretScan.Read(Gluetun()));

        Assert.Equal(["OPENVPN_PASSWORD", "OPENVPN_USER"], findings.Select(f => f.Name).Order());
        var password = findings.Single(f => f.Name == "OPENVPN_PASSWORD");
        Assert.Equal("gluetun", password.Container);
        Assert.Equal(SecretPlaces.Env, password.Where);
        Assert.Equal(SecretSeverity.High, password.Severity);
        Assert.Equal(FakeSecrets.VpnPassword.Length, password.Length);
        Assert.Equal($"{FakeSecrets.VpnPassword.Length} characters", password.Fingerprint);
    }

    [Fact]
    public void A_finding_never_carries_the_value()
    {
        var findings = SecretScan.Find(SecretScan.Read(Gluetun()));

        // Everything a finding can say — as JSON, as ToString, as advice — and the value is in none of it.
        var said = JsonSerializer.Serialize(findings) + string.Join("\n", findings.Select(f => f.ToString())) +
                   string.Join("\n", findings.SelectMany(f => f.Advice));
        Assert.DoesNotContain(FakeSecrets.VpnPassword, said, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecrets.VpnUser, said, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecrets.VpnPassword, SecretScan.Read(Gluetun()).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Command_line_secrets_are_found_and_ports_are_not()
    {
        var input = SecretScan.Read(Inspect("app", "example/app", [],
            cmd: ["--port", "8080", "--api-key=" + FakeSecrets.RandomKey, "--token", "Tk0123456789abcdefXYZ", "--verbose"]));

        var findings = SecretScan.Find(input);

        Assert.Equal(["--api-key", "--token"], findings.Select(f => f.Name).Order());
        Assert.All(findings, f => Assert.Equal(SecretPlaces.Command, f.Where));
    }

    [Fact]
    public void A_shell_command_is_read_word_by_word()
    {
        var input = SecretScan.Read(Inspect("job", "alpine", [],
            entrypoint: ["sh", "-c"], cmd: ["curl -H 'x' " + FakeSecrets.Discord + " && run --password=hunter22"]));

        var findings = SecretScan.Find(input);

        Assert.Contains(findings, f => f.Kind == "Discord webhook" && f.Name.StartsWith("argument", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Name == "--password" && f.Kind == "password");
    }

    [Fact]
    public void Labels_find_a_basic_auth_hash_and_leave_routing_alone()
    {
        var input = SecretScan.Read(Inspect("whoami", "traefik/whoami", [], labels: new()
        {
            ["traefik.http.middlewares.auth.basicauth.users"] = "admin:$apr1$H6uskkkW$IgXLP6ewTrSuBkTrqE8wj/",
            ["traefik.http.routers.whoami.middlewares"] = "auth",
            ["traefik.http.middlewares.auth.forwardauth.address"] = "http://authelia:9091/api/verify",
            ["traefik.http.middlewares.auth.forwardauth.authResponseHeaders"] = "Remote-User,Remote-Groups",
            ["com.docker.compose.config-hash"] = "0123456789abcdef0123456789abcdef0123456789abcdef",
            ["org.opencontainers.image.revision"] = "f3K9qLm2Xv8RtY1pZc7WbN4sHj6DgA0e",
        }));

        var finding = Assert.Single(SecretScan.Find(input));
        Assert.Equal("traefik.http.middlewares.auth.basicauth.users", finding.Name);
        Assert.Equal("password hash", finding.Kind);
        Assert.Equal(SecretSeverity.Low, finding.Severity);
    }

    [Fact]
    public void The_same_value_in_two_containers_is_said_without_saying_it()
    {
        var findings = SecretScan.FindAll(
        [
            SecretScan.Read(Inspect("app", "example/app", ["DB_PASSWORD=Shared-Pa55-0001"])),
            SecretScan.Read(Inspect("db", "postgres:16", ["POSTGRES_PASSWORD=Shared-Pa55-0001"])),
            SecretScan.Read(Inspect("other", "example/other", ["DB_PASSWORD=Different-Pa55"])),
        ]);

        Assert.Equal(["db POSTGRES_PASSWORD"], findings.Single(f => f.Container == "app").SameValueAs);
        Assert.Equal(["app DB_PASSWORD"], findings.Single(f => f.Container == "db").SameValueAs);
        Assert.Empty(findings.Single(f => f.Container == "other").SameValueAs);
    }

    [Fact]
    public void Advice_fits_the_image_and_the_severity()
    {
        var linuxserver = new SecretFinding("sonarr", SecretPlaces.Env, "SONARR_PASSWORD", "password", SecretSeverity.Medium, 8)
        {
            Image = "lscr.io/linuxserver/sonarr:latest",
        };
        var gluetun = new SecretFinding("gluetun", SecretPlaces.Env, "OPENVPN_PASSWORD", "VPN password", SecretSeverity.High, 20)
        {
            Image = "qmcgaw/gluetun",
        };
        var command = new SecretFinding("app", SecretPlaces.Command, "--token", "token", SecretSeverity.Medium, 20);

        Assert.Contains(linuxserver.Advice, a => a.Contains("FILE__SONARR_PASSWORD=/run/secrets/sonarr_password", StringComparison.Ordinal));
        Assert.DoesNotContain(linuxserver.Advice, a => a.Contains("rotate", StringComparison.OrdinalIgnoreCase) || a.Contains("revoke", StringComparison.Ordinal));
        Assert.Contains(gluetun.Advice, a => a.Contains("${OPENVPN_PASSWORD}", StringComparison.Ordinal) && a.Contains("chmod 600", StringComparison.Ordinal));
        Assert.Contains(gluetun.Advice, a => a.Contains("OPENVPN_PASSWORD_FILE", StringComparison.Ordinal));
        Assert.Contains(gluetun.Advice, a => a.Contains("ever shared", StringComparison.Ordinal));
        Assert.Contains(command.Advice, a => a.Contains("list processes", StringComparison.Ordinal));
    }

    [Fact]
    public void The_ignore_list_reads_back_and_forgives_rubbish()
    {
        var stored = SecretScan.FormatIgnored(["gluetun|OPENVPN_USER", "app|--token", "gluetun|OPENVPN_USER"]);

        Assert.Equal(["app|--token", "gluetun|OPENVPN_USER"], SecretScan.ParseIgnored(stored).Order());
        Assert.Empty(SecretScan.ParseIgnored("not json"));
        Assert.Empty(SecretScan.ParseIgnored(null));
    }

    [Fact]
    public void The_daily_check_is_due_once_a_day_and_only_when_on()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var bag = new SettingsBag { [SecretHygiene.DailyKey] = "true" };
        Assert.True(SecretHygiene.DailyDue(bag, now));

        bag[SecretHygiene.LastRunKey] = now.AddHours(-3).ToString("O");
        Assert.False(SecretHygiene.DailyDue(bag, now));

        bag[SecretHygiene.LastRunKey] = now.AddHours(-25).ToString("O");
        Assert.True(SecretHygiene.DailyDue(bag, now));

        bag[SecretHygiene.DailyKey] = "false";
        Assert.False(SecretHygiene.DailyDue(bag, now));
    }
}

/// <summary>
/// The check against a real database and a Docker host that answers over HTTP: what it
/// reads, how many at once, what it remembers, what it announces — and, above all, that
/// the values never reach the page, the log, the change feed or the database.
/// </summary>
public sealed class SecretHygieneTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly CapturingLogs _logs = new();
    private readonly ScriptedDocker _docker;
    private InteractiveRenderer? _renderer;
    private Connection _connection = new();

    /// <summary>Containers the fake host lists, by name: running or not, and their inspect payloads.</summary>
    private readonly ConcurrentDictionary<string, (bool Running, string Inspect)> _containers = new();

    private volatile bool _refuseInspect;

    // Inspects in flight, counted here rather than by ScriptedDocker: that one counts every
    // request the port receives, and on a busy runner a port just freed by another test can
    // still be reached by that test's pollers, which pushed its count past the cap now and then.
    private int _inspecting;
    private int _maxInspecting;

    public SecretHygieneTests()
    {
        _docker = new ScriptedDocker(HandleAsync);
        _services = TestHost.Build(_directory, services =>
        {
            services.AddLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(_logs);
            });
            services.AddSingleton<Offload>();
            services.AddSingleton<SecretHygiene>();
        });
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        _containers["gluetun"] = (true, SecretScanTests.Gluetun());
    }

    public async ValueTask DisposeAsync()
    {
        if (_renderer is not null)
            await _renderer.DisposeAsync();
        await Get<SecretHygiene>().StopAsync(CancellationToken.None);
        _docker.Dispose();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private async Task HandleAsync(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath;
        if (path.EndsWith("/containers/json", StringComparison.Ordinal))
        {
            await ScriptedDocker.Json(context, _containers.Select(c => new
            {
                Id = c.Key + "0123456789abcdef",
                Names = new[] { "/" + c.Key },
                Image = "example",
                ImageID = "sha256:aaa",
                State = c.Value.Running ? "running" : "exited",
                Status = c.Value.Running ? "Up 2 hours" : "Exited (0) 1 hour ago",
                Labels = new Dictionary<string, string>(),
            }).ToList());
            return;
        }

        if (path.Contains("/containers/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal))
        {
            if (_refuseInspect)
            {
                await ScriptedDocker.Forbidden(context);
                return;
            }
            var now = Interlocked.Increment(ref _inspecting);
            int seen;
            while ((seen = Volatile.Read(ref _maxInspecting)) < now &&
                   Interlocked.CompareExchange(ref _maxInspecting, now, seen) != seen)
            {
            }
            try
            {
                // Slow enough that the inspects overlap, so the cap is what limits them.
                await Task.Delay(30);
            }
            finally
            {
                Interlocked.Decrement(ref _inspecting);
            }
            var id = Uri.UnescapeDataString(path.Split('/')[^2]);
            var name = id.EndsWith("0123456789abcdef", StringComparison.Ordinal) ? id[..^16] : id;
            if (_containers.TryGetValue(name, out var container))
                await ScriptedDocker.Json(context, container.Inspect);
            else
                await ScriptedDocker.NoSuchContainer(context);
            return;
        }

        await ScriptedDocker.NoSuchContainer(context);
    }

    private async Task<Connection> DockerAsync()
    {
        _connection = new Connection
        {
            Provider = "docker",
            Name = "nas",
            Settings = new SettingsBag { ["endpoint"] = _docker.Endpoint, ["timeout"] = "5" },
        };
        await Get<ConfigStore>().SaveConnectionAsync(_connection);
        return _connection;
    }

    private static string Container(string name, string password, bool linuxserver = false) =>
        JsonSerializer.Serialize(new
        {
            Id = name + "0123456789abcdef",
            Name = "/" + name,
            Config = new
            {
                Image = linuxserver ? "lscr.io/linuxserver/" + name : "example/" + name,
                Env = new[] { "PUID=1000", "TZ=UTC", "APP_PASSWORD=" + password },
            },
        });

    [Fact]
    public async Task A_check_reads_running_containers_and_skips_stopped_ones_unless_asked()
    {
        await DockerAsync();
        _containers["old"] = (false, Container("old", "Stopped-Pa55-xyz"));

        var report = await Get<SecretHygiene>().CheckAsync();

        Assert.Equal(1, report.Containers);
        Assert.Equal(["gluetun"], report.Findings.Select(f => f.Container).Distinct());
        Assert.Equal("1 container has 2 plain-text secrets.", report.Summary);
        Assert.Equal(SecretSeverity.High, report.Findings[0].Severity);

        await Get<AppSettingsStore>().SaveAsync(SecretHygiene.StoppedKey, "true");
        report = await Get<SecretHygiene>().CheckAsync();

        Assert.Equal(2, report.Containers);
        Assert.Contains(report.Findings, f => f.Container == "old");
        Assert.Equal("2 containers have 3 plain-text secrets.", report.Summary);
    }

    [Fact]
    public async Task Inspects_are_capped_at_four_at_a_time()
    {
        await DockerAsync();
        for (var i = 0; i < 16; i++)
            _containers[$"app{i}"] = (true, Container($"app{i}", $"Pa55-{i:00}-unique"));

        var report = await Get<SecretHygiene>().CheckAsync();

        Assert.Equal(17, report.Containers);
        Assert.InRange(_maxInspecting, 1, SecretHygiene.Concurrency);
    }

    [Fact]
    public async Task A_socket_proxy_refusing_inspect_says_which_flag()
    {
        await DockerAsync();
        _refuseInspect = true;

        var report = await Get<SecretHygiene>().CheckAsync();

        var problem = Assert.Single(report.Problems);
        Assert.Contains("CONTAINERS=1", problem, StringComparison.Ordinal);
        Assert.Contains("nas", problem, StringComparison.Ordinal);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task An_ignored_finding_stays_ignored_and_out_of_the_count()
    {
        await DockerAsync();
        var hygiene = Get<SecretHygiene>();
        var report = await hygiene.CheckAsync();

        await hygiene.SetIgnoredAsync(report.Findings.Single(f => f.Name == "OPENVPN_USER"), true);

        Assert.Equal("1 container has 1 plain-text secret.", hygiene.Latest!.Summary);
        Assert.Equal(1, hygiene.Latest.IgnoredCount);
        Assert.Contains("gluetun|OPENVPN_USER", await Get<AppSettingsStore>().GetAsync(SecretHygiene.IgnoredKey), StringComparison.Ordinal);

        // A fresh check — the container recreated, LabbyTwo restarted — still knows.
        report = await hygiene.CheckAsync();
        Assert.True(report.Findings.Single(f => f.Name == "OPENVPN_USER").Ignored);
        Assert.False(report.Findings.Single(f => f.Name == "OPENVPN_PASSWORD").Ignored);

        await hygiene.SetIgnoredAsync(report.Findings.Single(f => f.Name == "OPENVPN_USER"), false);
        Assert.Equal(0, hygiene.Latest!.IgnoredCount);
    }

    [Fact]
    public async Task A_recreated_container_with_a_new_high_secret_is_announced_once()
    {
        var connection = await DockerAsync();
        var hygiene = Get<SecretHygiene>();
        _containers.Clear();
        await hygiene.CheckAsync();

        // Off by default: nothing is said.
        _containers["gluetun"] = (true, SecretScanTests.Gluetun());
        Assert.Empty(await hygiene.NoteRecreatedAsync(connection.Id, "gluetun", DateTimeOffset.Now, CancellationToken.None));

        await Get<AppSettingsStore>().SaveAsync(SecretHygiene.NotifyKey, "true");
        var announced = Assert.Single(await hygiene.NoteRecreatedAsync(connection.Id, "gluetun", DateTimeOffset.Now, CancellationToken.None));
        Assert.Equal("gluetun now has OPENVPN_PASSWORD in plain text", announced.Title);
        Assert.Equal(ChangeKinds.Container, announced.Kind);

        // Recreated again, same secret: already said.
        Assert.Empty(await hygiene.NoteRecreatedAsync(connection.Id, "gluetun", DateTimeOffset.Now, CancellationToken.None));
    }

    [Fact]
    public async Task The_feed_saying_recreated_is_what_sets_it_off()
    {
        var connection = await DockerAsync();
        await Get<AppSettingsStore>().SaveAsync(SecretHygiene.NotifyKey, "true");
        await Get<SecretHygiene>().StartAsync(CancellationToken.None);

        await Get<ChangeStore>().RecordAsync(new Change(DateTimeOffset.Now, ChangeKinds.Container, ChangeActions.Recreated,
            connection.Id, "gluetun", "gluetun was recreated"));

        var until = DateTime.UtcNow.AddSeconds(10);
        IReadOnlyList<Change> feed = [];
        while (DateTime.UtcNow < until)
        {
            feed = await Feed();
            if (feed.Any(c => c.Action == ChangeActions.Appeared))
                break;
            await Task.Delay(25);
        }
        Assert.Contains(feed, c => c.Title == "gluetun now has OPENVPN_PASSWORD in plain text");
    }

    [Fact]
    public async Task An_ignored_secret_is_not_announced()
    {
        var connection = await DockerAsync();
        await Get<AppSettingsStore>().SaveAsync(new Dictionary<string, string>
        {
            [SecretHygiene.NotifyKey] = "true",
            [SecretHygiene.IgnoredKey] = SecretScan.FormatIgnored(["gluetun|OPENVPN_PASSWORD"]),
        });

        Assert.Empty(await Get<SecretHygiene>().NoteRecreatedAsync(connection.Id, "gluetun", DateTimeOffset.Now, CancellationToken.None));
    }

    private Task<IReadOnlyList<Change>> Feed() =>
        Get<ChangeStore>().QueryAsync(new ChangeQuery(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1)));

    [Fact]
    public async Task The_values_never_reach_the_log_the_feed_or_the_database()
    {
        var connection = await DockerAsync();
        _containers["sonarr"] = (true, Container("sonarr", "Sonarr-Only-Pa55word", linuxserver: true));
        var hygiene = Get<SecretHygiene>();
        await Get<AppSettingsStore>().SaveAsync(SecretHygiene.NotifyKey, "true");

        var report = await hygiene.CheckAsync();
        await hygiene.SetIgnoredAsync(report.Findings.First(), true);
        // A new secret after a recreate, so the announcement path runs too.
        _containers["gluetun"] = (true, SecretScanTests.Gluetun("Rotated-But-Still-Plain-VPN-99"));
        await hygiene.SetIgnoredAsync(report.Findings.First(), false);
        _containers["tunnel"] = (true, JsonSerializer.Serialize(new
        {
            Name = "/tunnel",
            Config = new { Image = "cloudflare/cloudflared", Env = new[] { "TUNNEL_TOKEN=" + FakeSecrets.RandomKey } },
        }));
        Assert.NotEmpty(await hygiene.NoteRecreatedAsync(connection.Id, "tunnel", DateTimeOffset.Now, CancellationToken.None));

        string[] values = [FakeSecrets.VpnPassword, FakeSecrets.VpnUser, "Sonarr-Only-Pa55word", FakeSecrets.RandomKey];

        var logs = _logs.Text;
        Assert.Contains("Secret check", logs, StringComparison.Ordinal);
        var feed = string.Join("\n", (await Feed()).Select(c => $"{c.Title} {c.Detail} {c.Subject}"));
        Assert.Contains("TUNNEL_TOKEN", feed, StringComparison.Ordinal);
        var database = DatabaseText();

        foreach (var value in values)
        {
            Assert.DoesNotContain(value, logs, StringComparison.Ordinal);
            Assert.DoesNotContain(value, feed, StringComparison.Ordinal);
            Assert.DoesNotContain(value, database, StringComparison.Ordinal);
        }
    }

    /// <summary>Every byte of the database files, as text — the main file and its WAL.</summary>
    private string DatabaseText()
    {
        var text = new StringBuilder();
        foreach (var file in Directory.GetFiles(_directory, "test.db*"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            text.Append(Encoding.UTF8.GetString(memory.ToArray()));
        }
        return text.ToString();
    }

    // ---- the page --------------------------------------------------------------------

    private InteractiveRenderer Renderer => _renderer ??= new InteractiveRenderer(_services);

    [Fact]
    public async Task The_page_lists_findings_by_container_and_never_the_value()
    {
        await DockerAsync();
        await Renderer.RenderAsync<SecretHygieneSettings>(new Dictionary<string, object?>());

        await Renderer.ClickAsync("Check now");
        var html = await Renderer.WaitForAsync("1 container has 2 plain-text secrets.");
        var text = WebUtility.HtmlDecode(html);

        Assert.Contains("Secrets in containers", text, StringComparison.Ordinal);
        Assert.Contains("data-container=\"gluetun\"", html, StringComparison.Ordinal);
        Assert.Contains("OPENVPN_PASSWORD", text, StringComparison.Ordinal);
        Assert.Contains("VPN password", text, StringComparison.Ordinal);
        Assert.Contains($"{FakeSecrets.VpnPassword.Length} characters", text, StringComparison.Ordinal);
        Assert.Contains("CONTAINERS=1", text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecrets.VpnPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecrets.VpnUser, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_severity_filter_and_ignore_work_on_the_page()
    {
        await DockerAsync();
        await Get<SecretHygiene>().CheckAsync();
        await Renderer.RenderAsync<SecretHygieneSettings>(new Dictionary<string, object?>());
        await Renderer.WaitForAsync("OPENVPN_USER");

        await Renderer.ChangeAsync("secrets-severity", nameof(SecretSeverity.High));
        var html = WebUtility.HtmlDecode(await Renderer.HtmlAsync());
        Assert.Contains("OPENVPN_PASSWORD", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<code>OPENVPN_USER</code>", html, StringComparison.Ordinal);

        await Renderer.ClickAsync("Ignore");
        html = await Renderer.WaitForAsync("1 container has 1 plain-text secret.");
        Assert.Contains("Show 1 ignored", WebUtility.HtmlDecode(html), StringComparison.Ordinal);
    }

    /// <summary>Every log line written while the test ran, with its arguments and any exception.</summary>
    private sealed class CapturingLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public string Text => string.Join("\n", _lines);

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var line = formatter(state, exception);
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                    line += " " + string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"));
                if (exception is not null)
                    line += " " + exception;
                owner._lines.Enqueue(line);
            }
        }
    }
}
