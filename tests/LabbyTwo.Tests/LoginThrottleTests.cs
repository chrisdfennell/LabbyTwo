using System.Net;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Tests;

/// <summary>
/// The login throttle is the only thing between the login form and a script trying every
/// password in a list, so its schedule, what it counts against and when it forgets are
/// pinned here with a clock the test moves by hand.
/// </summary>
public sealed class LoginThrottleTests
{
    private static readonly IPAddress A = IPAddress.Parse("192.168.1.50");
    private static readonly IPAddress B = IPAddress.Parse("192.168.1.51");

    internal sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    internal sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
                Lines.Add((logLevel, formatter(state, exception)));
        }
    }

    private readonly ManualClock _clock = new();

    private LoginThrottle Throttle(LoginThrottle.Limits? limits = null, ILogger<LoginThrottle>? log = null) =>
        new(log ?? NullLogger<LoginThrottle>.Instance, _clock, limits);

    private static LoginThrottle.Result Wrong(LoginThrottle throttle, IPAddress? from) => throttle.Attempt(from, () => false);
    private static LoginThrottle.Result Right(LoginThrottle throttle, IPAddress? from) => throttle.Attempt(from, () => true);

    // ---- The schedule ------------------------------------------------------------------

    [Fact]
    public void Five_mistakes_are_free_then_each_doubles_the_wait_up_to_fifteen_minutes()
    {
        var limits = new LoginThrottle.Limits();
        for (var i = 1; i <= 5; i++)
            Assert.Equal(TimeSpan.Zero, LoginThrottle.LockoutAfter(i, limits));

        Assert.Equal(TimeSpan.FromSeconds(1), LoginThrottle.LockoutAfter(6, limits));
        Assert.Equal(TimeSpan.FromSeconds(2), LoginThrottle.LockoutAfter(7, limits));
        Assert.Equal(TimeSpan.FromSeconds(4), LoginThrottle.LockoutAfter(8, limits));
        Assert.Equal(TimeSpan.FromSeconds(512), LoginThrottle.LockoutAfter(15, limits));
        Assert.Equal(TimeSpan.FromMinutes(15), LoginThrottle.LockoutAfter(16, limits));
        // A script that has been at it all night must not overflow its way back to zero.
        Assert.Equal(TimeSpan.FromMinutes(15), LoginThrottle.LockoutAfter(10_000, limits));
    }

    [Fact]
    public void A_locked_address_is_refused_without_the_password_being_looked_at()
    {
        var throttle = Throttle();
        for (var i = 0; i < 5; i++)
            Assert.Equal(new LoginThrottle.Result(LoginThrottle.Outcome.Failed, TimeSpan.Zero), Wrong(throttle, A));

        Assert.Equal(new LoginThrottle.Result(LoginThrottle.Outcome.Failed, TimeSpan.FromSeconds(1)), Wrong(throttle, A));

        var looked = false;
        var refused = throttle.Attempt(A, () => looked = true);
        Assert.Equal(LoginThrottle.Outcome.Throttled, refused.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(1), refused.RetryAfter);
        Assert.False(looked);

        // Once the second has passed it may try again, and the next failure costs two.
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(2), Wrong(throttle, A).RetryAfter);
        _clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(TimeSpan.FromSeconds(0.5), Wrong(throttle, A).RetryAfter);
    }

    [Fact]
    public void One_address_being_locked_does_not_touch_another()
    {
        var throttle = Throttle();
        for (var i = 0; i < 8; i++)
            Wrong(throttle, A);
        Assert.Equal(LoginThrottle.Outcome.Throttled, Wrong(throttle, A).Outcome);

        Assert.Equal(new LoginThrottle.Result(LoginThrottle.Outcome.Failed, TimeSpan.Zero), Wrong(throttle, B));
        Assert.Equal(LoginThrottle.Outcome.Succeeded, Right(throttle, B).Outcome);
    }

    [Fact]
    public void Signing_in_clears_that_address_and_only_that_one()
    {
        var throttle = Throttle();
        for (var i = 0; i < 5; i++)
        {
            Wrong(throttle, A);
            Wrong(throttle, B);
        }
        Assert.Equal(LoginThrottle.Outcome.Succeeded, Right(throttle, A).Outcome);

        // A has its five free mistakes back; B does not.
        for (var i = 0; i < 5; i++)
            Assert.Equal(TimeSpan.Zero, Wrong(throttle, A).RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(1), Wrong(throttle, A).RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(1), Wrong(throttle, B).RetryAfter);
    }

    [Fact]
    public void Too_many_failures_across_all_addresses_pause_every_sign_in()
    {
        var log = new ListLogger<LoginThrottle>();
        var throttle = Throttle(log: log);

        // Fifty addresses, one guess each: nobody has used up their free attempts, and the
        // ceiling is not yet passed.
        for (var i = 1; i <= 50; i++)
            Assert.Equal(TimeSpan.Zero, Wrong(throttle, IPAddress.Parse($"203.0.113.{i}")).RetryAfter);

        // The fifty-first passes it.
        Assert.Equal(TimeSpan.FromMinutes(1), Wrong(throttle, IPAddress.Parse("203.0.113.51")).RetryAfter);

        // Now even a new address with the right password waits.
        var looked = false;
        var refused = throttle.Attempt(IPAddress.Parse("198.51.100.7"), () => looked = true);
        Assert.Equal(LoginThrottle.Outcome.Throttled, refused.Outcome);
        Assert.False(looked);
        Assert.NotNull(throttle.Snapshot().GlobalLockedUntil);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("203.0.113.51")
                                        && l.Message.Contains("every sign-in"));

        // A minute later it is let through; another failure inside the window pauses again.
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(LoginThrottle.Outcome.Succeeded, Right(throttle, IPAddress.Parse("198.51.100.7")).Outcome);
        Assert.Equal(TimeSpan.FromMinutes(1), Wrong(throttle, IPAddress.Parse("198.51.100.8")).RetryAfter);

        // Once the window has emptied, one failure is just one failure.
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(TimeSpan.Zero, Wrong(throttle, IPAddress.Parse("198.51.100.9")).RetryAfter);
    }

    [Fact]
    public void A_lockout_is_logged_as_a_warning_naming_the_address()
    {
        var log = new ListLogger<LoginThrottle>();
        var throttle = Throttle(log: log);
        for (var i = 0; i < 5; i++)
            Wrong(throttle, A);
        Assert.DoesNotContain(log.Lines, l => l.Level >= LogLevel.Warning);

        Wrong(throttle, A);
        var line = Assert.Single(log.Lines, l => l.Level == LogLevel.Warning);
        Assert.Contains("192.168.1.50", line.Message);

        var status = throttle.Snapshot();
        Assert.Equal(6, status.Failures);
        Assert.Equal(1, status.LockedAddresses);
        Assert.Equal("192.168.1.50", status.LastLockAddress);
    }

    // ---- Forgetting --------------------------------------------------------------------

    [Fact]
    public void A_quiet_address_is_forgotten_and_starts_again()
    {
        var throttle = Throttle();
        for (var i = 0; i < 5; i++)
            Wrong(throttle, A);
        Assert.Equal(1, throttle.Tracked);

        _clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Wrong(throttle, B);
        Assert.Equal(1, throttle.Tracked);

        // Back to five free mistakes.
        for (var i = 0; i < 5; i++)
            Assert.Equal(TimeSpan.Zero, Wrong(throttle, A).RetryAfter);
    }

    [Fact]
    public void A_flood_of_addresses_cannot_grow_the_table_past_its_cap()
    {
        var limits = new LoginThrottle.Limits { MaxTracked = 100, GlobalCeiling = int.MaxValue };
        var throttle = Throttle(limits);

        for (var i = 0; i < 1_000; i++)
        {
            Wrong(throttle, new IPAddress([10, 0, (byte)(i / 256), (byte)(i % 256)]));
            _clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.True(throttle.Tracked <= 100, $"{throttle.Tracked} addresses tracked");
        // The newest is the one kept: the quietest are the ones dropped.
        for (var i = 0; i < 5; i++)
            Wrong(throttle, IPAddress.Parse("10.0.3.231"));
        Assert.Equal(LoginThrottle.Outcome.Throttled, Wrong(throttle, IPAddress.Parse("10.0.3.231")).Outcome);
    }

    // ---- What counts as one address ----------------------------------------------------

    [Fact]
    public void An_ipv4_client_on_a_dual_stack_socket_is_the_same_client()
    {
        Assert.Equal("192.168.1.50", LoginThrottle.KeyFor(A.MapToIPv6()));
        Assert.Equal("unknown", LoginThrottle.KeyFor(null));
        Assert.Equal("::1", LoginThrottle.KeyFor(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void Ipv6_addresses_in_one_slash_64_share_their_attempts()
    {
        var one = IPAddress.Parse("2001:db8:1:2::1");
        var other = IPAddress.Parse("2001:db8:1:2:ffff:ffff:ffff:ffff");
        var elsewhere = IPAddress.Parse("2001:db8:1:3::1");

        Assert.Equal(LoginThrottle.KeyFor(one), LoginThrottle.KeyFor(other));
        Assert.NotEqual(LoginThrottle.KeyFor(one), LoginThrottle.KeyFor(elsewhere));

        var throttle = Throttle();
        for (var i = 0; i < 6; i++)
            Wrong(throttle, IPAddress.Parse($"2001:db8:1:2::{i + 10}"));
        Assert.Equal(LoginThrottle.Outcome.Throttled, Wrong(throttle, other).Outcome);
    }

    [Theory]
    [InlineData(0.2, "1 second")]
    [InlineData(1, "1 second")]
    [InlineData(1.5, "2 seconds")]
    [InlineData(90, "90 seconds")]
    [InlineData(91, "2 minutes")]
    [InlineData(900, "15 minutes")]
    public void Waits_are_described_rounded_up(double seconds, string expected) =>
        Assert.Equal(expected, LoginThrottle.Describe(TimeSpan.FromSeconds(seconds)));

    // ---- Comparing the password --------------------------------------------------------

    [Fact]
    public void Credentials_match_only_when_both_are_right()
    {
        var auth = new LabbyOptions.AuthSettings { Username = "labby", Password = "correct horse" };

        Assert.True(LoginCredentials.Matches(auth, "labby", "correct horse"));
        Assert.True(LoginCredentials.Matches(auth, "LABBY", "correct horse"));
        Assert.False(LoginCredentials.Matches(auth, "labby", "Correct horse"));
        Assert.False(LoginCredentials.Matches(auth, "labby", "correct horse "));
        Assert.False(LoginCredentials.Matches(auth, "labby", "c"));
        Assert.False(LoginCredentials.Matches(auth, "admin", "correct horse"));
        Assert.False(LoginCredentials.Matches(auth, null, null));
    }

    [Fact]
    public void With_no_password_configured_nothing_matches()
    {
        var auth = new LabbyOptions.AuthSettings { Username = "labby", Password = "" };
        Assert.False(LoginCredentials.Matches(auth, "labby", ""));
    }

    [Fact]
    public void The_login_page_compares_through_the_constant_time_helper()
    {
        // The page's own string comparison is what this replaced; a later edit putting a
        // string.Equals back on the password would pass every other test here.
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "Components", "Pages", "Login.razor"));
        Assert.Contains("Throttle.Attempt(", page);
        Assert.Contains("LoginCredentials.Matches(", page);
        Assert.DoesNotContain("auth.Password", page);
    }

    internal static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LabbyTwo.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    // ---- Who the client is -------------------------------------------------------------

    private static async Task<IPAddress?> ClientSeenAs(
        LabbyOptions.ProxySettings settings, string remote, params (string Name, string Value)[] headers)
    {
        var options = new ForwardedHeadersOptions();
        Assert.Empty(ForwardedHeadersSetup.Apply(options, settings));

        IPAddress? seen = null;
        var middleware = new ForwardedHeadersMiddleware(
            context => { seen = context.Connection.RemoteIpAddress; return Task.CompletedTask; },
            NullLoggerFactory.Instance, Options.Create(options));

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        foreach (var (name, value) in headers)
            context.Request.Headers[name] = value;
        await middleware.Invoke(context);
        return seen;
    }

    [Fact]
    public async Task With_no_trusted_proxies_a_forwarded_header_is_ignored()
    {
        var seen = await ClientSeenAs(new LabbyOptions.ProxySettings(), "172.18.0.5",
            ("X-Forwarded-For", "203.0.113.9"));
        Assert.Equal(IPAddress.Parse("172.18.0.5"), seen);
    }

    [Fact]
    public async Task A_trusted_proxy_is_believed_about_the_client()
    {
        var seen = await ClientSeenAs(new LabbyOptions.ProxySettings { TrustedProxies = "172.16.0.0/12" }, "172.18.0.5",
            ("X-Forwarded-For", "203.0.113.9"));
        Assert.Equal(IPAddress.Parse("203.0.113.9"), seen);
    }

    [Fact]
    public async Task Somebody_on_the_lan_cannot_name_their_own_address()
    {
        // The proxy is trusted, but this request did not come through it.
        var seen = await ClientSeenAs(new LabbyOptions.ProxySettings { TrustedProxies = "172.18.0.5" }, "192.168.1.66",
            ("X-Forwarded-For", "203.0.113.9"));
        Assert.Equal(IPAddress.Parse("192.168.1.66"), seen);
    }

    [Fact]
    public async Task A_client_supplied_prefix_to_the_header_is_not_believed()
    {
        // Cloudflare appends the real address to whatever X-Forwarded-For the client sent.
        // Walking back from the right stops at the first address that is not a proxy of ours.
        var seen = await ClientSeenAs(new LabbyOptions.ProxySettings { TrustedProxies = "172.16.0.0/12, 10.0.0.2" },
            "172.18.0.5", ("X-Forwarded-For", "1.2.3.4, 203.0.113.9, 10.0.0.2"));
        Assert.Equal(IPAddress.Parse("203.0.113.9"), seen);
    }

    [Fact]
    public async Task Behind_cloudflare_the_connecting_ip_header_can_be_used_instead()
    {
        var settings = new LabbyOptions.ProxySettings { TrustedProxies = "172.18.0.5", ClientIpHeader = "CF-Connecting-IP" };
        var seen = await ClientSeenAs(settings, "172.18.0.5",
            ("CF-Connecting-IP", "203.0.113.9"), ("X-Forwarded-For", "1.2.3.4"));
        Assert.Equal(IPAddress.Parse("203.0.113.9"), seen);
    }

    [Fact]
    public void An_entry_that_is_not_an_address_is_reported_rather_than_trusted()
    {
        var options = new ForwardedHeadersOptions();
        var invalid = ForwardedHeadersSetup.Apply(options,
            new LabbyOptions.ProxySettings { TrustedProxies = "10.0.0.0/8 cloudflared 172.18.0.5" });
        Assert.Equal(["cloudflared"], invalid);
        Assert.Contains(IPAddress.Parse("172.18.0.5"), options.KnownProxies);
        Assert.Null(options.ForwardLimit);
    }
}
