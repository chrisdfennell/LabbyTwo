using System.Net;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// The family status page's pure parts: what it shows and in which words, how its link is
/// checked, how text typed by an anonymous visitor is cleaned and escaped, the rate limits,
/// and the report store. The same things through the real app are in FamilyEndpointTests.
/// </summary>
public sealed class FamilyStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static Connection Plex => new() { Id = "c-plex", Provider = "http", Name = "plex-docker-01" };
    private static Connection Router => new() { Id = "c-router", Provider = "http", Name = "opnsense.lan" };
    private static Connection Nas => new() { Id = "c-nas", Provider = "http", Name = "Secret NAS" };

    private static FamilyStatusSettings Settings(params FamilyItem[] items) =>
        new(true, FamilyStatusSettings.NewToken(), false, "", items);

    private static Dictionary<string, Connection> All(params Connection[] connections) =>
        connections.ToDictionary(c => c.Id);

    [Fact]
    public void Only_the_chosen_connections_appear_and_only_by_the_names_the_owner_gave()
    {
        var settings = Settings(
            new FamilyItem("i1", Plex.Id, "Plex", "📺", "TV & Movies"),
            new FamilyItem("i2", Router.Id, "The internet", "🌐", "Internet"));

        var view = FamilyView.Build(settings, All(Plex, Router, Nas),
            _ => new FamilyView.Probe(true, false, Now.AddHours(-3)), Maintenance.Off, Now);

        Assert.Equal(["TV & Movies", "Internet"], view.Groups.Select(g => g.Name));
        Assert.Equal(["Plex", "The internet"], view.Rows.Select(r => r.Name));
        // The row carries the item's own id, never the connection's.
        Assert.Equal(["i1", "i2"], view.Rows.Select(r => r.Id));
        Assert.DoesNotContain(view.Rows, r => r.Name.Contains("plex-docker") || r.Name.Contains("Secret NAS"));
    }

    [Fact]
    public void An_item_whose_connection_was_deleted_is_left_off()
    {
        var settings = Settings(new FamilyItem("i1", "gone", "Old thing", "", ""), new FamilyItem("i2", Plex.Id, "Plex", "", ""));
        var view = FamilyView.Build(settings, All(Plex), _ => null, Maintenance.Off, Now);
        Assert.Equal(["Plex"], view.Rows.Select(r => r.Name));
    }

    [Fact]
    public void The_row_record_has_no_room_for_anything_but_the_whitelisted_fields()
    {
        // If somebody adds a field to what the page is built from, this is where they
        // should have to stop and think about whether a visitor may see it.
        var properties = typeof(FamilyRow).GetProperties().Select(p => p.Name).Order().ToList();
        Assert.Equal(["Icon", "Id", "Name", "Since", "State", "Word"], properties);
    }

    [Theory]
    [InlineData(true, false, false, false, FamilyState.Working)]
    [InlineData(true, true, false, false, FamilyState.Trouble)]
    [InlineData(false, false, false, false, FamilyState.Down)]
    [InlineData(null, false, false, false, FamilyState.Unknown)]
    // Maintenance covers only what is not working: something that stayed up is just working.
    [InlineData(false, false, true, false, FamilyState.Maintenance)]
    [InlineData(true, true, true, false, FamilyState.Maintenance)]
    [InlineData(true, false, true, false, FamilyState.Working)]
    // A connection the owner silenced is "I know, I'm on it" too.
    [InlineData(false, false, false, true, FamilyState.Maintenance)]
    public void States_come_out_in_four_plain_words(bool? up, bool failing, bool maintenance, bool silenced, FamilyState expected)
    {
        var connection = Plex with { SilencedUntil = silenced ? Now.AddHours(1) : null };
        var (state, _) = FamilyView.StateOf(connection, new FamilyView.Probe(up, failing, Now.AddMinutes(-20)),
            maintenance ? new Maintenance(true, Now.AddHours(1)) : Maintenance.Off, Now);
        Assert.Equal(expected, state);
    }

    [Fact]
    public void Down_says_since_when_in_words()
    {
        var (state, since) = FamilyView.StateOf(Plex, new FamilyView.Probe(false, false, Now.AddMinutes(-20)), Maintenance.Off, Now);
        Assert.Equal(FamilyState.Down, state);
        Assert.Equal("since 20 min ago", FamilyWords.Since(since!.Value, Now));
        Assert.Equal("Down", FamilyWords.State(state));
        Assert.Equal("since 3 hours ago", FamilyWords.Since(Now.AddHours(-3), Now));
        Assert.Equal("since yesterday", FamilyWords.Since(Now.AddDays(-1), Now));
        Assert.Equal("since just now", FamilyWords.Since(Now, Now));
    }

    [Fact]
    public void The_overall_line_is_the_worst_thing_on_the_page()
    {
        var settings = Settings(new FamilyItem("i1", Plex.Id, "Plex", "", ""), new FamilyItem("i2", Router.Id, "Internet", "", ""));
        var view = FamilyView.Build(settings, All(Plex, Router),
            id => new FamilyView.Probe(id != Router.Id, false, null), Maintenance.Off, Now);
        Assert.Equal(FamilyState.Down, view.Overall);
        Assert.Equal("Something isn't working", FamilyWords.Overall(view.Overall));
    }

    [Fact]
    public void Settings_survive_a_round_trip_and_bad_json_reads_as_never_set_up()
    {
        var settings = Settings(new FamilyItem("i1", Plex.Id, "Plex", "📺", "TV")) with { LanAccess = true, Title = "Home" };
        var back = FamilyStatusSettings.Parse(settings.ToJson());
        Assert.Equal(settings.Token, back.Token);
        Assert.True(back.Enabled);
        Assert.True(back.LanAccess);
        Assert.Equal("Home", back.Heading);
        Assert.Equal(settings.Items, back.Items);

        Assert.Equal(FamilyStatusSettings.Default, FamilyStatusSettings.Parse("{not json"));
        Assert.False(FamilyStatusSettings.Parse("").Enabled);
    }

    [Fact]
    public void A_token_is_128_random_bits_and_only_the_exact_one_matches()
    {
        var token = FamilyStatusSettings.NewToken();
        Assert.Equal(22, token.Length);
        Assert.Equal(16, Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "==").Length);
        Assert.NotEqual(token, FamilyStatusSettings.NewToken());
        Assert.All(token, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));

        const string stored = "AbCdEfGhIjKlMnOpQrStUv";
        var settings = FamilyStatusSettings.Default with { Enabled = true, Token = stored };
        Assert.True(settings.Matches(stored));
        Assert.False(settings.Matches(stored.ToLowerInvariant()));
        Assert.False(settings.Matches(stored[..^1]));
        Assert.False(settings.Matches(stored + "x"));
        Assert.False(settings.Matches(""));
        Assert.False(settings.Matches(null));

        // No token stored never matches anything, the empty string least of all.
        var none = FamilyStatusSettings.Default with { Enabled = true };
        Assert.False(none.Matches(""));
        Assert.False(none.Matches("anything"));
    }

    [Theory]
    [InlineData("  hello\r\n\tworld  ", "hello world")]
    [InlineData("a\u0000b\u0007c", "a b c")]
    // A right-to-left override is how "exe.txt" is made to read as "txt.exe".
    [InlineData("abc‮def", "abcdef")]
    [InlineData("zero​width", "zerowidth")]
    [InlineData("<script>alert(1)</script>", "<script>alert(1)</script>")]
    public void Typed_text_is_cleaned_to_one_line_of_visible_characters(string typed, string expected) =>
        Assert.Equal(expected, FamilyText.Clean(typed, 100));

    [Fact]
    public void Cleaning_cuts_to_length_without_splitting_an_emoji()
    {
        Assert.Equal(FamilyText.MaxMessage, FamilyText.Clean(new string('x', 5000), FamilyText.MaxMessage).Length);

        // The family emoji is five code points held together by zero-width joiners; the cut
        // either keeps all of it or none of it.
        var family = "👨‍👩‍👧";
        var cut = FamilyText.Clean("ab" + family, 4);
        Assert.Equal("ab", cut);
        Assert.Equal("ab" + family, FamilyText.Clean("ab" + family, 20));
    }

    [Fact]
    public void Notification_text_cannot_ping_a_server_or_become_markdown()
    {
        var plain = FamilyText.ForPlainNotification("@everyone <!channel> hi");
        Assert.DoesNotContain("@everyone", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("<!channel>", plain, StringComparison.Ordinal);

        // Ordinal: a culture-aware comparison skips the zero-width space and would find the
        // mention that is not there.
        var markdown = FamilyText.ForMarkdownNotification("**bold** [link](http://evil) `code` @here");
        Assert.Equal(@"\*\*bold\*\* \[link\]\(http://evil\) \`code\` @" + "​" + "here", markdown);

        var alert = FamilyStatus.Notification("Plex", "**bold** @everyone", "Sam");
        Assert.Equal(AlertLevel.Info, alert.Level);
        Assert.Contains("Sam says Plex isn't working", alert.Title);
        Assert.DoesNotContain("@everyone", alert.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("**bold**", alert.Markdown, StringComparison.Ordinal);
        Assert.False(alert.Urgent);
    }

    [Fact]
    public void Page_loads_are_limited_per_address()
    {
        var clock = new LoginThrottleTests.ManualClock();
        var throttle = new FamilyThrottle(clock, new FamilyThrottle.Limits { PageLoads = 3, PageWindow = TimeSpan.FromMinutes(1) });
        var one = IPAddress.Parse("203.0.113.5");

        for (var i = 0; i < 3; i++)
            Assert.Null(throttle.Page(one));
        Assert.NotNull(throttle.Page(one));
        Assert.Null(throttle.Page(IPAddress.Parse("203.0.113.6")));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(throttle.Page(one));
    }

    [Fact]
    public void Reports_are_limited_per_address_and_for_everybody()
    {
        var clock = new LoginThrottleTests.ManualClock();
        var throttle = new FamilyThrottle(clock, new FamilyThrottle.Limits
        {
            ReportsShort = 2, ReportsShortWindow = TimeSpan.FromMinutes(15),
            ReportsDaily = 3, ReportsGlobal = 5, ReportsGlobalWindow = TimeSpan.FromHours(1),
        });
        var one = IPAddress.Parse("203.0.113.5");

        Assert.Null(throttle.Report(one));
        Assert.Null(throttle.Report(one));
        var wait = throttle.Report(one);
        Assert.Equal(TimeSpan.FromMinutes(15), wait);

        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Null(throttle.Report(one));
        // Three in the day is the daily cap, however they are spaced.
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.NotNull(throttle.Report(one));

        // Everybody together: two more addresses fill the global budget of five.
        Assert.Null(throttle.Report(IPAddress.Parse("198.51.100.1")));
        Assert.Null(throttle.Report(IPAddress.Parse("198.51.100.2")));
        Assert.NotNull(throttle.Report(IPAddress.Parse("198.51.100.3")));
    }

    [Fact]
    public void An_ipv6_visitor_is_counted_by_their_whole_64()
    {
        var throttle = new FamilyThrottle(new LoginThrottleTests.ManualClock(), new FamilyThrottle.Limits { PageLoads = 1 });
        Assert.Null(throttle.Page(IPAddress.Parse("2001:db8:1:2::1")));
        Assert.NotNull(throttle.Page(IPAddress.Parse("2001:db8:1:2::ffff")));
    }

    [Fact]
    public void Rotating_addresses_cannot_grow_the_throttle_forever()
    {
        var throttle = new FamilyThrottle(new LoginThrottleTests.ManualClock(), new FamilyThrottle.Limits { MaxTracked = 10 });
        for (var i = 0; i < 100; i++)
            throttle.Page(IPAddress.Parse($"203.0.113.{i}"));
        Assert.True(throttle.Tracked <= 10);
    }

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::5", true)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("203.0.113.5", false)]
    [InlineData("2001:db8::1", false)]
    public void Private_addresses_are_the_home_network(string address, bool expected) =>
        Assert.Equal(expected, FamilyStatus.IsPrivate(IPAddress.Parse(address)));

    [Theory]
    [InlineData(null, true)]
    // A tunnel or an untrusted proxy makes the internet look local; any trace of one refuses.
    [InlineData("X-Forwarded-For", false)]
    [InlineData("CF-Connecting-IP", false)]
    [InlineData("CF-Ray", false)]
    [InlineData("Forwarded", false)]
    public void A_request_that_came_through_a_proxy_is_never_local(string? header, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("172.18.0.5");
        if (header is not null)
            context.Request.Headers[header] = "203.0.113.9";
        Assert.Equal(expected, FamilyStatus.IsLocal(context));
    }

    [Fact]
    public async Task Reports_are_stored_listed_counted_and_dismissed()
    {
        var directory = TestHost.TempDirectory();
        var services = TestHost.ReadyHost(directory);
        try
        {
            var store = new FamilyReportStore(services.GetRequiredService<Db>());
            var changed = 0;
            store.Changed += () => changed++;

            var first = await store.AddAsync(new FamilyReport(0, Now.AddMinutes(-5), "i1", "c-plex", "Plex", "<b>no</b>", "Sam"));
            await store.AddAsync(new FamilyReport(0, Now, "other", null, "Something else", "", ""));

            Assert.Equal(2, await store.CountAsync());
            var list = await store.ListAsync();
            Assert.Equal(["Something else", "Plex"], list.Select(r => r.ItemName));
            // Stored exactly as given: it is text, and escaping is the renderer's job.
            Assert.Equal("<b>no</b>", list[1].Message);

            await store.DismissAsync(first.Id);
            Assert.Equal(1, await store.CountAsync());
            await store.DismissAllAsync();
            Assert.Equal(0, await store.CountAsync());
            Assert.Equal(4, changed);
        }
        finally
        {
            TestHost.Teardown(services, directory);
        }
    }

    [Fact]
    public async Task The_store_keeps_at_most_its_ceiling()
    {
        var directory = TestHost.TempDirectory();
        var services = TestHost.ReadyHost(directory);
        try
        {
            var store = new FamilyReportStore(services.GetRequiredService<Db>());
            for (var i = 0; i < FamilyReportStore.MaxKept + 5; i++)
                await store.AddAsync(new FamilyReport(0, Now.AddSeconds(i), "other", null, "Something else", $"#{i}", ""));
            Assert.Equal(FamilyReportStore.MaxKept, await store.CountAsync());
            Assert.Equal($"#{FamilyReportStore.MaxKept + 4}", (await store.ListAsync(1)).Single().Message);
        }
        finally
        {
            TestHost.Teardown(services, directory);
        }
    }

    [Fact]
    public void Report_is_a_change_kind_the_feed_offers()
    {
        Assert.Contains(ChangeKinds.All, k => k.Key == ChangeKinds.Report);
        Assert.Equal(ChangeKinds.Report, ChangeKinds.Parse("reports"));
    }
}
