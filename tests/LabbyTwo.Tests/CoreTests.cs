using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Storage;

namespace LabbyTwo.Tests;

public class SearchEngineTests
{
    [Fact]
    public void AKnownEngineResolvesToItsUrl()
    {
        var engine = SearchEngine.Resolve("duckduckgo", null);
        Assert.Equal("https://duckduckgo.com/", engine.Url);
        Assert.Equal("q", engine.QueryParameter);
    }

    [Fact]
    public void AnUnknownKeyFallsBackRatherThanBreakingTheWidget()
    {
        Assert.Equal(SearchEngine.All[0].Key, SearchEngine.Resolve("who-knows", null).Key);
    }

    [Theory]
    [InlineData("http://searx.lan/search?q=", "http://searx.lan/search", "q")]
    [InlineData("https://wiki.lan/index.php?search=", "https://wiki.lan/index.php", "search")]
    [InlineData("https://example.invalid/find?type=all&query=", "https://example.invalid/find", "query")]
    public void ACustomUrlHasItsQueryParameterReadOffTheEnd(string input, string url, string parameter)
    {
        var engine = SearchEngine.Resolve(SearchEngine.CustomKey, input);
        Assert.Equal(url, engine.Url);
        Assert.Equal(parameter, engine.QueryParameter);
    }

    [Fact]
    public void ACustomUrlWithNoQueryStringStillWorks()
    {
        var engine = SearchEngine.Resolve(SearchEngine.CustomKey, "http://searx.lan/search");
        Assert.Equal("http://searx.lan/search", engine.Url);
        Assert.Equal("q", engine.QueryParameter);
    }

    [Fact]
    public void AnEmptyCustomUrlDoesNotProduceABrokenForm()
    {
        var engine = SearchEngine.Resolve(SearchEngine.CustomKey, "   ");
        Assert.Equal("https://duckduckgo.com/", engine.Url);
    }

    [Fact]
    public void EngineKeysAreUniqueAndAllOfferedAsOptions()
    {
        Assert.Equal(SearchEngine.All.Count, SearchEngine.All.Select(e => e.Key).Distinct().Count());
        Assert.Equal(SearchEngine.All.Count + 1, SearchEngine.Options.Count);
    }
}

public class AppearanceTests
{
    [Theory]
    [InlineData("#4da3ff", true)]
    [InlineData("#fff", true)]
    [InlineData("#GGGGGG", false)]
    [InlineData("red", false)]
    [InlineData("#4da3ff; background: url(x)", false)]
    [InlineData(null, false)]
    public void OnlyAHexColourIsAccepted(string? value, bool expected)
    {
        // The value is written into a style attribute, so anything else is an injection.
        Assert.Equal(expected, Appearance.IsValidColor(value));
    }

    [Fact]
    public void AnInvalidStoredAccentFallsBackRatherThanReachingTheStyleAttribute()
    {
        var look = Appearance.From(new SettingsBag { [Appearance.AccentKey] = "javascript:alert(1)" });
        Assert.Contains(Appearance.Default.Accent, look.StyleAttribute);
        Assert.DoesNotContain("javascript", look.StyleAttribute);
    }

    [Fact]
    public void SystemStampsNoThemeAttributeSoTheStylesheetDecides()
    {
        Assert.Null(Appearance.From(new SettingsBag { [Appearance.ThemeKey] = "system" }).ThemeAttribute);
        Assert.Equal("dark", Appearance.From(new SettingsBag { [Appearance.ThemeKey] = "dark" }).ThemeAttribute);
    }

    [Theory]
    [InlineData("compact", "0.86")]
    [InlineData("comfortable", "1")]
    [InlineData("roomy", "1.12")]
    public void DensityBecomesAScaleFactor(string density, string scale)
    {
        var look = Appearance.From(new SettingsBag { [Appearance.DensityKey] = density });
        Assert.Contains($"--density: {scale}", look.StyleAttribute);
    }
}

public class LinkRowTests
{
    [Fact]
    public void RowsSurviveARoundTrip()
    {
        List<LinkRow> rows = [new("🏠", "Home", "http://home.lan"), new("", "Router", "http://192.168.1.1")];
        Assert.Equal(rows, LinkRow.Parse(LinkRow.Serialize(rows)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"not\": \"an array\"}")]
    public void BadStoredValuesEmptyTheCardRatherThanBreakingThePage(string? stored)
    {
        Assert.Empty(LinkRow.Parse(stored));
    }
}

public class JsonApiParsingTests
{
    [Fact]
    public void MetricLinesAreParsedAndCommentsIgnored()
    {
        var map = JsonApiProvider.ParseMetricMap("""
            cpu = sensors.cpu.temp
            # this line is a note
            ssd_used = disks[0].used

            malformed line with no equals
            fan = fan_running
            """).ToList();

        Assert.Equal(
            [("cpu", "sensors.cpu.temp"), ("ssd_used", "disks[0].used"), ("fan", "fan_running")],
            map);
    }

    [Theory]
    [InlineData("a.b.c", 3)]
    [InlineData("list[1].value", 7)]
    [InlineData("list[0].value", 5)]
    public void DottedPathsWithIndexersResolve(string path, double expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            { "a": { "b": { "c": 3 } }, "list": [ { "value": 5 }, { "value": 7 } ] }
            """);

        var element = JsonApiProvider.Resolve(document.RootElement, path);
        Assert.NotNull(element);
        Assert.Equal(expected, element!.Value.GetDouble());
    }

    [Theory]
    [InlineData("a.nope")]
    [InlineData("list[9].value")]
    [InlineData("a[0]")]
    public void APathThatDoesNotMatchReturnsNullRatherThanThrowing(string path)
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            { "a": { "b": 1 }, "list": [ { "value": 5 } ] }
            """);

        Assert.Null(JsonApiProvider.Resolve(document.RootElement, path));
    }
}

public class SettingsBagTests
{
    [Fact]
    public void KeysAreCaseInsensitiveThroughJson()
    {
        var bag = new SettingsBag { ["Url"] = "http://example.invalid" };
        var round = SettingsBag.FromJson(bag.ToJson());
        Assert.Equal("http://example.invalid", round.Get("url"));
    }

    [Fact]
    public void AccessorsFallBackOnMissingOrUnparseableValues()
    {
        var bag = new SettingsBag { ["n"] = "not a number", ["blank"] = "  " };

        Assert.Equal(7, bag.GetInt("n", 7));
        Assert.Equal(7, bag.GetInt("absent", 7));
        Assert.Equal("fallback", bag.Get("blank", "fallback"));
        Assert.True(bag.GetBool("absent", true));
    }

    [Fact]
    public void CloningDoesNotShareState()
    {
        var original = new SettingsBag { ["a"] = "1" };
        var clone = original.Clone();
        clone["a"] = "2";
        Assert.Equal("1", original.Get("a"));
    }
}

public class HomeAssistantParsingTests
{
    [Theory]
    [InlineData("21.5", 21.5)]
    [InlineData("0", 0)]
    [InlineData("-3.25", -3.25)]
    public void ANumericStateParses(string state, double expected)
        => Assert.Equal(expected, HomeAssistantProvider.AsNumber(state));

    [Theory]
    [InlineData("on", 1)]
    [InlineData("home", 1)]
    [InlineData("detected", 1)]
    [InlineData("off", 0)]
    [InlineData("not_home", 0)]
    [InlineData("closed", 0)]
    public void TheOnOffVocabularyBecomesOneAndZero(string state, double expected)
        => Assert.Equal(expected, HomeAssistantProvider.AsNumber(state));

    [Theory]
    [InlineData("unavailable")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void NoReadingIsNullRatherThanZero(string? state)
    {
        // Charting "unavailable" as 0 would draw a sensor dropping to zero rather than
        // a gap, and an alert rule would fire on it.
        Assert.Null(HomeAssistantProvider.AsNumber(state));
    }

    [Fact]
    public void EntityLinesAreParsedAndCommentsIgnored()
    {
        var map = HomeAssistantProvider.ParseEntityMap("""
            office_temp = sensor.office_temperature
            # a note
            solar_watts = sensor.solar_power

            nonsense without an equals
            """).ToList();

        Assert.Equal(
            [("office_temp", "sensor.office_temperature"), ("solar_watts", "sensor.solar_power")],
            map);
    }
}

public class RadarSourceTests
{
    [Fact]
    public void CoordinatesGoIntoTheTemplate()
    {
        var url = RadarSource.Resolve("rainviewer").BuildUrl(51.5072, -0.1276, 7, "", "");

        Assert.Contains("loc=51.5072,-0.1276,7", url);
        Assert.StartsWith("https://www.rainviewer.com/", url);
    }

    [Fact]
    public void CoordinatesAreFormattedInvariantly()
    {
        // A comma decimal separator would produce a URL pointing somewhere else entirely,
        // and it would only happen on machines with a European locale.
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var url = RadarSource.Resolve("windy").BuildUrl(48.1372, 11.5756, 8, "", "");
            Assert.Contains("lat=48.1372", url);
            Assert.Contains("lon=11.5756", url);
            Assert.DoesNotContain("48,1372", url);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void AStationSourceUsesTheSiteCodeAndUppercasesIt()
    {
        var url = RadarSource.Resolve("nws").BuildUrl(0, 0, 7, "ktlx", "");
        Assert.Equal("https://radar.weather.gov/ridge/standard/KTLX_loop.gif", url);
    }

    [Fact]
    public void AWholeCountrySourceIgnoresCoordinatesEntirely()
    {
        var url = RadarSource.Resolve("nws-conus").BuildUrl(51.5, -0.1, 3, "", "");
        Assert.Equal("https://radar.weather.gov/ridge/standard/CONUS_loop.gif", url);
    }

    [Fact]
    public void ACustomUrlIsUsedVerbatimAndStillSubstitutes()
    {
        var source = RadarSource.Resolve(RadarSource.CustomImageKey);

        Assert.Equal("http://radar.lan/img.gif",
            source.BuildUrl(1, 2, 3, "", "http://radar.lan/img.gif"));
        Assert.Equal("http://radar.lan/at/51.5/-0.13",
            source.BuildUrl(51.5, -0.13, 3, "", "http://radar.lan/at/{lat}/{lon}"));
    }

    [Fact]
    public void ACustomSourceWithNoUrlProducesNothingRatherThanABrokenTag()
    {
        Assert.Equal("", RadarSource.Resolve(RadarSource.CustomEmbedKey).BuildUrl(1, 2, 3, "", "   "));
    }

    [Fact]
    public void ZoomIsClampedToWhatAMapWillAccept()
    {
        var source = RadarSource.Resolve("rainviewer");
        Assert.Contains(",1", source.BuildUrl(0, 0, -5, "", ""));
        Assert.Contains(",15", source.BuildUrl(0, 0, 99, "", ""));
    }

    [Fact]
    public void AnUnknownKeyFallsBackRatherThanRenderingNothing()
    {
        Assert.Equal(RadarSource.All[0].Key, RadarSource.Resolve("no-such-source").Key);
        Assert.Equal(RadarSource.All[0].Key, RadarSource.Resolve(null).Key);
    }

    [Fact]
    public void EveryNonCustomSourceHasATemplateAndEveryKeyIsUnique()
    {
        Assert.All(RadarSource.All.Where(s => !s.IsCustom), s => Assert.NotEmpty(s.UrlTemplate));
        Assert.All(RadarSource.All, s => Assert.NotEmpty(s.Coverage));
        Assert.Equal(RadarSource.All.Count, RadarSource.All.Select(s => s.Key).Distinct().Count());
        Assert.Equal(RadarSource.All.Count, RadarSource.Options.Count);
    }
}

public class SuggestedRuleTests
{
    private static readonly SuggestedRule Frost =
        new("Frost", "temp_outdoor_c", Comparison.Below, 0, ClearThreshold: 2);

    [Fact]
    public void ASuggestionBecomesAnOrdinaryRuleBoundToOneConnection()
    {
        var rule = Frost.ForConnection("conn-1");

        Assert.Equal("conn-1", rule.ConnectionId);
        Assert.Equal("temp_outdoor_c", rule.Metric);
        Assert.Equal(Comparison.Below, rule.Comparison);
        Assert.Equal(2, rule.ClearsAt);
        Assert.True(rule.Enabled);
    }

    [Fact]
    public void ARuleOnTheSameConnectionAndMetricCoversTheSuggestion()
    {
        var existing = Frost.ForConnection("conn-1") with { Threshold = -2 };
        Assert.True(Frost.IsCoveredBy(existing, "conn-1"));
    }

    [Fact]
    public void ARuleWatchingEveryConnectionCoversItToo()
    {
        var everywhere = Frost.ForConnection("conn-1") with { ConnectionId = null };
        Assert.True(Frost.IsCoveredBy(everywhere, "conn-1"));
    }

    [Fact]
    public void ARuleOnAnotherConnectionOrTheOtherDirectionDoesNotCoverIt()
    {
        Assert.False(Frost.IsCoveredBy(Frost.ForConnection("conn-2"), "conn-1"));

        var opposite = Frost.ForConnection("conn-1") with { Comparison = Comparison.Above };
        Assert.False(Frost.IsCoveredBy(opposite, "conn-1"));

        var otherMetric = Frost.ForConnection("conn-1") with { Metric = "humidity" };
        Assert.False(Frost.IsCoveredBy(otherMetric, "conn-1"));
    }
}

public class ProbeErrorTests
{
    [Fact]
    public void AnHttpTimeoutSaysSoInsteadOfTaskWasCanceled()
    {
        // HttpClient reports its own timeout as a cancellation, and every provider used
        // to surface the raw ".NET" text, which tells the user nothing.
        var message = ProbeError.Describe(new TaskCanceledException("A task was canceled."), "http://nas:8989");

        Assert.DoesNotContain("task was canceled", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Timed out", message);
        Assert.Contains("http://nas:8989", message);
        Assert.Contains("firewall", message);
    }

    [Theory]
    [InlineData("http://no-such-host.invalid:8989")]
    [InlineData("no-such-host.invalid")]
    public void ATimeoutAgainstAnUnresolvableNameSaysSo(string target)
    {
        // .invalid is reserved by RFC 2606 and never resolves, so this is deterministic
        // rather than dependent on whoever runs the tests.
        var message = ProbeError.Describe(new TaskCanceledException(), target);
        Assert.Contains("Timed out", message);
        Assert.Contains("no-such-host.invalid", message);
        Assert.Contains("IP address", message);
    }

    [Fact]
    public void ATimeoutAgainstAResolvableNameReportsWhatItResolvedTo()
    {
        // localhost resolves everywhere, often to both 127.0.0.1 and ::1 — which is the
        // multi-address case that made a real NAS look like it had a firewall problem.
        var message = ProbeError.Describe(new TaskCanceledException(), "http://localhost:8989");

        Assert.Contains("localhost", message);
        Assert.Contains("resolve", message, StringComparison.OrdinalIgnoreCase);
        // Either wording is correct; which one depends on the host's resolver.
        Assert.True(
            message.Contains("addresses in this container") || message.Contains("resolves to 127.0.0.1")
            || message.Contains("resolves to ::1"),
            $"Expected the message to name what it resolved to. Got: {message}");
    }

    [Theory]
    [InlineData("http://192.168.1.50:8989")]
    [InlineData("192.168.1.50")]
    [InlineData("http://[2001:db8::1]:8989")]
    public void ATimeoutAgainstAnAddressDoesNotBlameDns(string target)
    {
        // An IP literal cannot have a DNS problem, so the hint would be noise.
        var message = ProbeError.Describe(new TaskCanceledException(), target);
        Assert.DoesNotContain("IP address instead", message);
        Assert.Contains("Timed out", message);
    }

    [Fact]
    public void ARefusedConnectionIsDistinguishedFromATimeout()
    {
        // Different cause, different fix: refused means the port is closed, timed out
        // means the packets went nowhere.
        var refused = new HttpRequestException("boom",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));

        var message = ProbeError.Describe(refused, "http://nas:8989");
        Assert.Contains("refused", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing is listening", message);
    }

    [Fact]
    public void AnUnresolvableHostSuggestsUsingAnAddress()
    {
        var dns = new HttpRequestException("boom",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));

        Assert.Contains("resolve", ProbeError.Describe(dns, "http://sonarr.lan"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ATlsFailureMentionsTheHttpVersusHttpsMixUp()
    {
        var tls = new HttpRequestException("The SSL connection could not be established",
            new System.Security.Authentication.AuthenticationException("bad"));

        Assert.Contains("http://", ProbeError.Describe(tls, "https://nas:8080"));
    }

    [Fact]
    public void AnOrdinaryFailureKeepsItsOwnMessage()
    {
        Assert.Equal("Sonarr rejected the API key.",
            ProbeError.Describe(new InvalidOperationException("Sonarr rejected the API key.")));
    }

    [Fact]
    public void NoTargetMeansNoDanglingPreposition()
    {
        var message = ProbeError.Describe(new TaskCanceledException(), target: null);
        Assert.DoesNotContain(" at .", message);
        Assert.DoesNotContain("  ", message);
    }
}

public class ErsatzTvPlaylistTests
{
    [Fact]
    public void ChannelsAreCountedFromExtinfLines()
    {
        const string playlist = """
            #EXTM3U
            #EXTINF:-1 tvg-id="1" tvg-name="Movies",Movies
            http://ersatztv:8409/iptv/channel/1.m3u8
            #EXTINF:-1 tvg-id="2" tvg-name="Comedy",Comedy
            http://ersatztv:8409/iptv/channel/2.m3u8
            """;

        Assert.Equal(2, ErsatzTvProvider.CountChannels(playlist));
    }

    [Fact]
    public void AnEmptyPlaylistIsZeroChannelsRatherThanAFailure()
    {
        // A fresh ErsatzTV with no channels yet is working correctly, not broken.
        Assert.Equal(0, ErsatzTvProvider.CountChannels("#EXTM3U\n"));
    }
}
