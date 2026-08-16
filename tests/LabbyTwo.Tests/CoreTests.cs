using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using LabbyTwo.Components.Widgets;
using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Services;
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
    public void AMalformedAddressSaysWhatAGoodOneLooksLike()
    {
        // The raw text is "Invalid URI: Invalid port specified.", which names neither the
        // value nor the field and reads like a fault in the thing being probed.
        var broken = Record.Exception(() => new HttpRequestMessage(HttpMethod.Get, "http://192.168.86.57:8083:80"));
        var message = ProbeError.Describe(broken!, "http://192.168.86.57:8083:80");

        Assert.IsType<UriFormatException>(broken);
        Assert.DoesNotContain("Invalid URI", message);
        Assert.Contains("http://192.168.86.57:8083:80", message);
        Assert.Contains("port", message);
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

public class ContainerHintTests
{
    // These run outside a container, so the hint is suppressed — which is the behaviour
    // worth pinning: it must never appear for someone running LabbyTwo directly, where
    // the advice would be wrong.
    [Theory]
    [InlineData("http://192.168.1.50:8181")]
    [InlineData("http://10.0.0.5:8409")]
    [InlineData("http://172.20.0.3:9000")]
    public void OutsideAContainerAPrivateAddressGetsNoContainerAdvice(string target)
    {
        var message = ProbeError.Describe(new TaskCanceledException(), target);
        Assert.DoesNotContain("container name", message);
        Assert.Contains("Timed out", message);
    }

    [Fact]
    public void APublicAddressNeverGetsContainerAdviceEitherWay()
    {
        var message = ProbeError.Describe(new TaskCanceledException(), "https://8.8.8.8:443");
        Assert.DoesNotContain("container name", message);
    }

    [Fact]
    public void AHostnameStillGetsTheResolutionAdviceRatherThanTheContainerOne()
    {
        // The two hints are mutually exclusive: a name gets the resolution story, an
        // address gets the routing story.
        var message = ProbeError.Describe(new TaskCanceledException(), "http://no-such-host.invalid:8181");
        Assert.DoesNotContain("shared network", message);
        Assert.Contains("no-such-host.invalid", message);
    }
}

public class UnitsTests
{
    // The tests were written when this was one switch with two positions; the two presets
    // are now the starting points for four separate choices, so they are resolved once here.
    private static readonly Units.Preferences ImperialPrefs = Units.Preferences.Of(Units.Imperial);
    private static readonly Units.Preferences MetricPrefs = Units.Preferences.Of(Units.Metric);

    private static readonly MetricSpec Temp = new("temp_outdoor_c", "Outdoor temperature", "°C", 1);
    private static readonly MetricSpec Wind = new("gust_mph", "Wind gust", " mph", 1);
    private static readonly MetricSpec Disk = new("disk_percent", "Disk used", "%");

    [Fact]
    public void CelsiusBecomesFahrenheitForImperialAndStaysForMetric()
    {
        Assert.Equal(32, Units.Display(0, "°C", ImperialPrefs).Value, 3);
        Assert.Equal("°F", Units.Display(0, "°C", ImperialPrefs).Unit);
        Assert.Equal(0, Units.Display(0, "°C", MetricPrefs).Value, 3);
    }

    [Fact]
    public void MilesPerHourBecomesKilometresForMetric()
    {
        Assert.Equal(40, Units.Display(40, "mph", ImperialPrefs).Value, 3);
        Assert.Equal(64.37, Units.Display(40, "mph", MetricPrefs).Value, 2);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("ms")]
    [InlineData(" GB")]
    [InlineData("")]
    public void UnitsThatMeanTheSameEverywherePassStraightThrough(string unit)
    {
        Assert.Equal(42, Units.Display(42, unit, MetricPrefs).Value);
        Assert.Equal(42, Units.Display(42, unit, ImperialPrefs).Value);
        Assert.False(Units.IsConvertible(unit));
    }

    [Theory]
    [InlineData("°C", -40)]
    [InlineData("°C", 0)]
    [InlineData("°C", 21.5)]
    [InlineData("mph", 12.3)]
    [InlineData("inHg", 29.94)]
    [InlineData("in", 0.12)]
    public void DisplayAndStoreAreExactInverses(string unit, double stored)
    {
        // This is the property that matters: a threshold typed in one system and read
        // back in the other must be the same number, or every rule quietly drifts.
        foreach (var prefs in new[] { MetricPrefs, ImperialPrefs })
        {
            var shown = Units.Display(stored, unit, prefs).Value;
            Assert.Equal(stored, Units.Store(shown, unit, prefs), 6);
        }
    }

    [Fact]
    public void AThresholdTypedInFahrenheitIsStoredInCelsius()
    {
        // The bug this exists to prevent: someone shown °F types 90 and saves 90°C.
        Assert.Equal(32.22, Units.Store(90, "°C", ImperialPrefs), 2);
        Assert.Equal(90, Units.Store(90, "°C", MetricPrefs), 6);
    }

    [Fact]
    public void APassThroughUnitKeepsTheSpacingTheMetricDeclared()
    {
        // A hardcoded literal here turned every " mph" into "0mph" on the page, whatever
        // the provider asked for.
        Assert.Equal(" mph", Units.Display(40, " mph", ImperialPrefs).Unit);
        Assert.Equal(" inHg", Units.Display(30, " inHg", ImperialPrefs).Unit);
        Assert.Equal("°C", Units.Display(20, "°C", MetricPrefs).Unit);
    }

    [Fact]
    public void FormattingCarriesTheConvertedUnit()
    {
        Assert.Equal("32.0°F", Units.Format(Temp, 0, ImperialPrefs));
        Assert.Equal("0.0°C", Units.Format(Temp, 0, MetricPrefs));
        Assert.Equal("40.0 mph", Units.Format(Wind, 40, ImperialPrefs));
        Assert.Equal("90%", Units.Format(Disk, 90, ImperialPrefs));
    }

    [Fact]
    public void AnUnknownOrMissingPresetIsTreatedAsImperialRatherThanThrowing()
    {
        Assert.Equal(32, Units.Display(0, "°C", Units.Preferences.Of(null)).Value, 3);
        Assert.Equal(32, Units.Display(0, "°C", Units.Preferences.Of("nonsense")).Value, 3);
    }

    // ---- one quantity at a time ----

    /// <summary>
    /// The point of the whole change. A pilot wants knots and inHg; the old single switch
    /// could not express that, because choosing knots meant choosing hPa with it.
    /// </summary>
    [Fact]
    public void EachQuantityIsChosenIndependently()
    {
        var pilot = new Units.Preferences(Units.Celsius, Units.Knots, Units.InHg, Units.Inches);

        Assert.Equal(34.76, Units.Display(40, "mph", pilot).Value, 2);
        Assert.Equal(29.92, Units.Display(29.92, "inHg", pilot).Value, 2);
        Assert.Equal(0, Units.Display(0, "°C", pilot).Value, 3);
    }

    [Theory]
    [InlineData(Units.Celsius, 21.5, 21.5)]
    [InlineData(Units.Fahrenheit, 21.5, 70.7)]
    [InlineData(Units.Kelvin, 21.5, 294.65)]
    public void TemperatureConvertsToWhicheverWasChosen(string unit, double stored, double shown)
        => Assert.Equal(shown, Units.Display(stored, "°C", Units.Preferences.Default with { Temperature = unit }).Value, 2);

    [Theory]
    [InlineData(Units.Mph, 40, 40)]
    [InlineData(Units.Kmh, 40, 64.37)]
    [InlineData(Units.Ms, 40, 17.88)]
    [InlineData(Units.Knots, 40, 34.76)]
    public void WindConvertsToWhicheverWasChosen(string unit, double stored, double shown)
        => Assert.Equal(shown, Units.Display(stored, "mph", Units.Preferences.Default with { Wind = unit }).Value, 2);

    [Theory]
    [InlineData(Units.InHg, 29.92, 29.92)]
    [InlineData(Units.HPa, 29.92, 1013.2)]
    [InlineData(Units.Mbar, 29.92, 1013.2)]
    [InlineData(Units.MmHg, 29.92, 759.97)]
    [InlineData(Units.KPa, 29.92, 101.32)]
    public void PressureConvertsToWhicheverWasChosen(string unit, double stored, double shown)
        => Assert.Equal(shown, Units.Display(stored, "inHg", Units.Preferences.Default with { Pressure = unit }).Value, 1);

    /// <summary>
    /// The property that matters most, now across every combination rather than two: a
    /// threshold typed in one unit and read back in another has to be the same number, or
    /// every alert rule quietly drifts.
    /// </summary>
    [Theory]
    [InlineData("°C", Units.Kelvin, -40)]
    [InlineData("°C", Units.Fahrenheit, 21.5)]
    [InlineData("mph", Units.Knots, 12.3)]
    [InlineData("mph", Units.Ms, 12.3)]
    [InlineData("inHg", Units.MmHg, 29.94)]
    [InlineData("inHg", Units.KPa, 29.94)]
    [InlineData("in", Units.Mm, 0.12)]
    public void EveryUnitRoundTrips(string canonical, string chosen, double stored)
    {
        var prefs = canonical switch
        {
            "°C" => Units.Preferences.Default with { Temperature = chosen },
            "mph" => Units.Preferences.Default with { Wind = chosen },
            "inHg" => Units.Preferences.Default with { Pressure = chosen },
            _ => Units.Preferences.Default with { Rain = chosen },
        };

        var shown = Units.Display(stored, canonical, prefs).Value;
        Assert.Equal(stored, Units.Store(shown, canonical, prefs), 6);
    }

    /// <summary>
    /// An install that only ever chose "metric" has none of the per-quantity keys, and must
    /// keep reading in metric rather than falling back to the imperial defaults.
    /// </summary>
    [Fact]
    public void TheOldPresetStillDecidesWhenNothingFinerWasChosen()
    {
        var prefs = Units.Preferences.From(new SettingsBag { ["units"] = Units.Metric });

        Assert.Equal(Units.Celsius, prefs.Temperature);
        Assert.Equal(Units.Kmh, prefs.Wind);
        Assert.Equal(Units.HPa, prefs.Pressure);
        Assert.Equal(Units.Mm, prefs.Rain);
        Assert.Equal(Units.Metric, prefs.MatchingPreset);
    }

    [Fact]
    public void OneFinerChoiceOverridesThePresetAndLeavesTheRestAlone()
    {
        var prefs = Units.Preferences.From(new SettingsBag
        {
            ["units"] = Units.Metric,
            ["unit_wind"] = Units.Knots,
        });

        Assert.Equal(Units.Knots, prefs.Wind);
        Assert.Equal(Units.Celsius, prefs.Temperature);
        // Mixed, so the preset control has to say so rather than claim to be metric.
        Assert.Null(prefs.MatchingPreset);
    }

    [Fact]
    public void AStoredUnitNobodyOffersIsTreatedAsUnset()
    {
        var prefs = Units.Preferences.From(new SettingsBag { ["unit_wind"] = "furlongs per fortnight" });

        Assert.Equal(Units.Mph, prefs.Wind);
    }
}

public class EmojiCatalogTests
{
    [Fact]
    public void TheEmbeddedTableLoads()
    {
        // If the resource name or the csproj entry drifts, every picker silently empties.
        Assert.True(EmojiCatalog.All.Count > 2000,
            $"Expected a few thousand emoji, got {EmojiCatalog.All.Count}. Is Core/emoji.tsv still embedded?");
    }

    [Fact]
    public void EveryEntryIsUsableAndUniquelyKeyed()
    {
        Assert.All(EmojiCatalog.All, e =>
        {
            Assert.NotEmpty(e.Char);
            Assert.NotEmpty(e.Name);
            Assert.NotEmpty(e.Group);
        });
        Assert.Equal(EmojiCatalog.All.Count, EmojiCatalog.All.Select(e => e.Char).Distinct().Count());
    }

    [Fact]
    public void EveryGroupInTheDataIsOneThePickerShows()
    {
        // A group present in the file but missing from the tab list would be unreachable.
        foreach (var group in EmojiCatalog.All.Select(e => e.Group).Distinct())
            Assert.Contains(group, EmojiCatalog.Groups);
    }

    [Fact]
    public void EveryTabHasSomethingBehindIt()
    {
        foreach (var group in EmojiCatalog.Groups)
            Assert.NotEmpty(EmojiCatalog.InGroup(group));
    }

    [Theory]
    [InlineData("warning", "⚠")]
    [InlineData("floppy", "💾")]
    [InlineData("satellite antenna", "📡")]
    public void SearchFindsThingsByName(string query, string expected)
    {
        var found = EmojiCatalog.Search(query);
        Assert.Contains(found, e => e.Char.StartsWith(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryTermHasToMatch()
    {
        // "red circle" should not return everything red plus every circle.
        var found = EmojiCatalog.Search("red circle");
        Assert.All(found, e =>
        {
            Assert.Contains("red", e.Name, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("circle", e.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void PastingAnEmojiFindsItself()
    {
        var found = EmojiCatalog.Search("💾");
        Assert.Equal("💾", Assert.Single(found).Char);
    }

    [Fact]
    public void AnEmptySearchReturnsNothingRatherThanEverything()
    {
        Assert.Empty(EmojiCatalog.Search(""));
        Assert.Empty(EmojiCatalog.Search("   "));
        Assert.Empty(EmojiCatalog.Search(null));
    }

    [Fact]
    public void SkinToneModifiersAreNotOffered()
    {
        // They are combining marks. On their own they render as a bare colour swatch,
        // which is meaningless as an icon.
        Assert.DoesNotContain(EmojiCatalog.All, e => e.Name.Contains("Modifier", StringComparison.OrdinalIgnoreCase));
        foreach (var modifier in new[] { "\U0001F3FB", "\U0001F3FC", "\U0001F3FD", "\U0001F3FE", "\U0001F3FF" })
            Assert.DoesNotContain(EmojiCatalog.All, e => e.Char == modifier);
    }
}

public class UpdateCheckerTests
{
    [Fact]
    public void OnlyTheSubjectLineOfACommitIsShown()
    {
        // Commit bodies here run to paragraphs; the card wants one line.
        var message = "Add an emoji picker\n\nFour places let you set an icon, and each was\na bare text box.";
        Assert.Equal("Add an emoji picker", UpdateChecker.FirstLine(message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n\n")]
    public void AMissingCommitMessageIsNullRatherThanBlankText(string? message)
        => Assert.Null(UpdateChecker.FirstLine(message));

    [Fact]
    public void TheCompareLinkPointsAtWhatChangedSinceThisBuild()
    {
        var result = new UpdateChecker.Result("abc123456789", "def987654321", true, "x", null, null);
        Assert.Equal("https://github.com/chrisdfennell/LabbyTwo/compare/abc123456789...main", result.CompareUrl);
    }

    [Fact]
    public void AnUnknownResultIsNotReportedAsUpToDate()
    {
        // A failed check must never read as "you are current" — that is the one wrong
        // answer worse than saying nothing.
        var failed = new UpdateChecker.Result("abc123", null, null, null, null, "no route");
        Assert.False(failed.Known);
    }

    [Fact]
    public void BetweenTwoReleasesTheCompareLinkIsAFixedRange()
    {
        // Not "...main": the point of being on a release is that the range is stable.
        var result = new UpdateChecker.Result("v1.0.0", "v1.1.0", true, "x", null, null);
        Assert.Equal("https://github.com/chrisdfennell/LabbyTwo/compare/v1.0.0...v1.1.0", result.CompareUrl);
    }

    [Theory]
    [InlineData("v1.0.0", UpdateChecker.Channel.Release)]
    [InlineData("v1.10.2", UpdateChecker.Channel.Release)]
    [InlineData("1.0.0", UpdateChecker.Channel.Release)]
    // A describe is a commit that happens to know which release it came after. Reading it
    // as a release would report an install that is deliberately ahead as merely behind.
    [InlineData("v1.0.0-3-gabc1234", UpdateChecker.Channel.Commit)]
    [InlineData("abc123456789", UpdateChecker.Channel.Commit)]
    [InlineData("dev", UpdateChecker.Channel.Unstamped)]
    [InlineData("", UpdateChecker.Channel.Unstamped)]
    [InlineData(null, UpdateChecker.Channel.Unstamped)]
    public void TheShapeOfTheStampSaysWhatItShouldBeComparedAgainst(string? stamp, UpdateChecker.Channel expected)
        => Assert.Equal(expected, UpdateChecker.ChannelOf(stamp));

    [Theory]
    [InlineData("v1.0.0-3-gabc1234", "abc1234")]
    [InlineData("abc123456789", "abc123456789")]
    [InlineData("v1.0.0", null)]
    [InlineData("dev", null)]
    public void TheCommitIsFoundWhicheverShapeTheStampIs(string stamp, string? expected)
        => Assert.Equal(expected, UpdateChecker.CommitOf(stamp));

    [Fact]
    public void AReleaseIsComparedByNameAndTheVPrefixDoesNotCount()
    {
        var json = JsonDocument.Parse("""
            {"tag_name": "v1.0.0", "name": "v1.0.0", "published_at": "2026-08-12T10:00:00Z",
             "body": "Weather warnings, and a grid that reflows."}
            """);

        var same = UpdateChecker.ReadRelease("1.0.0", json.RootElement);
        Assert.False(same.Behind);
        Assert.Equal("v1.0.0", same.Latest);

        // The release's own name repeats the tag, so the summary falls back to the notes.
        Assert.Equal("Weather warnings, and a grid that reflows.", same.Summary);

        var older = UpdateChecker.ReadRelease("v0.9.0", json.RootElement);
        Assert.True(older.Behind);
    }

    [Fact]
    public void ARepositoryWithNoReleasesYetIsNotAFailedCheck()
    {
        // GitHub answers 404 for /releases/latest until the first one is cut, and that is
        // a fact about the project rather than something being wrong with the install.
        var json = JsonDocument.Parse("""{"message": "Not Found"}""");
        var result = UpdateChecker.ReadRelease("v1.0.0", json.RootElement);
        Assert.False(result.Known);
        Assert.NotNull(result.Error);
    }
}

public class ProbeSchedulingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Sweep = TimeSpan.FromSeconds(30);

    [Fact]
    public void MostThingsAreAskedEverySweep()
    {
        // The default. Anything on your own network reports a number that is true at the
        // moment you ask, so there is nothing to be gained by asking less often.
        Assert.True(HealthMonitor.IsDue(TimeSpan.Zero, Now.AddSeconds(-1), Now, Sweep));
    }

    [Fact]
    public void SomethingNeverProbedIsAlwaysDue()
    {
        // Or a restart would leave a forecast tile blank for a quarter of an hour.
        Assert.True(HealthMonitor.IsDue(TimeSpan.FromMinutes(15), null, Now, Sweep));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(14, false)]
    [InlineData(16, true)]
    [InlineData(60, true)]
    public void AMeteredUpstreamIsLeftAloneUntilItsIntervalHasPassed(int minutesAgo, bool due)
        => Assert.Equal(due, HealthMonitor.IsDue(
            TimeSpan.FromMinutes(15), Now.AddMinutes(-minutesAgo), Now, Sweep));

    [Fact]
    public void TheIntervalDoesNotDriftLongerEveryTimeItFires()
    {
        // The sweep runs on its own rhythm, so the tick that should fire usually lands a
        // fraction short. Without the slack it waits another whole sweep, and 15 minutes
        // becomes 15:30, then 16:00, and so on for as long as the process is up.
        var justShort = Now.AddMinutes(-15).AddSeconds(2);
        Assert.True(HealthMonitor.IsDue(TimeSpan.FromMinutes(15), justShort, Now, Sweep));
    }

    [Fact]
    public void TheProvidersThatCallAMeteredApiSayHowOftenTheyMayBeAsked()
    {
        // Open-Meteo cut us off for a day when these polled on the default sweep. Named
        // individually so removing one is a deliberate act rather than an oversight.
        var registry = TestHost.Build(TestHost.TempDirectory()).GetRequiredService<Registry>();

        foreach (var type in new[] { "forecast", "air-quality", "nws" })
        {
            var provider = registry.Provider(type);
            Assert.NotNull(provider);
            Assert.True(provider.MinimumInterval > TimeSpan.Zero,
                $"{type} calls a public API on a schedule and must declare a MinimumInterval.");
        }
    }
}

public class GreetingTests
{
    [Theory]
    [InlineData(0, "night")]
    [InlineData(4, "night")]
    [InlineData(5, "morning")]
    [InlineData(11, "morning")]
    [InlineData(12, "afternoon")]
    [InlineData(17, "afternoon")]
    [InlineData(18, "evening")]
    [InlineData(21, "evening")]
    [InlineData(22, "night")]
    [InlineData(23, "night")]
    public void EachHourPicksItsOwnWording(int hour, string expected)
        => Assert.Equal(expected, GreetingCard.PartOfDay(hour).Key);

    [Fact]
    public void TheNameGoesOnTheEndUnlessThePhrasePlacesIt()
    {
        Assert.Equal("Good morning, Chris", GreetingCard.Compose("Good morning", "Chris"));
        Assert.Equal("Morning Chris — kettle?", GreetingCard.Compose("Morning {name} — kettle?", "Chris"));
    }

    [Fact]
    public void APhraseWithNoNameToPutInItIsNotLeftWithTheGapOrThePunctuation()
    {
        // The widget's name field is optional, and "Evening, ." is worse than "Evening".
        Assert.Equal("Good evening", GreetingCard.Compose("Good evening", ""));
        Assert.Equal("Evening", GreetingCard.Compose("Evening, {name}", ""));
        Assert.Equal("Evening", GreetingCard.Compose("Evening {name}", ""));
    }
}

public class SunTimesTests
{
    // Somewhere with well-known times: New York, 40.71 N, 74.01 W.
    private const double NyLat = 40.7128, NyLon = -74.0060;

    [Fact]
    public void SunriseAndSunsetLandOnTheRightDayAndTheRightWayRound()
    {
        var day = SunTimes.For(new DateOnly(2026, 6, 21), NyLat, NyLon, TimeSpan.FromHours(-4));

        Assert.NotNull(day.Sunrise);
        Assert.NotNull(day.Sunset);
        Assert.True(day.Sunrise < day.Sunset);
        Assert.Equal(21, day.Sunrise!.Value.Day);
    }

    [Fact]
    public void MidsummerInNewYorkIsAboutFifteenHours()
    {
        // The real figure is 15h 5m. Anything in this range means the algorithm is right;
        // pinning it to the minute would just be asserting my own arithmetic back at me.
        var daylight = SunTimes.For(new DateOnly(2026, 6, 21), NyLat, NyLon, TimeSpan.FromHours(-4)).Daylight;

        Assert.NotNull(daylight);
        Assert.InRange(daylight!.Value.TotalHours, 14.8, 15.3);
    }

    [Fact]
    public void MidwinterIsAboutNineHours()
    {
        var daylight = SunTimes.For(new DateOnly(2026, 12, 21), NyLat, NyLon, TimeSpan.FromHours(-5)).Daylight;

        Assert.NotNull(daylight);
        Assert.InRange(daylight!.Value.TotalHours, 9.0, 9.5);
    }

    [Fact]
    public void AtTheEquinoxItIsAboutTwelveHoursEverywhere()
    {
        foreach (var latitude in new[] { -45.0, -20.0, 0.0, 20.0, 45.0 })
        {
            var daylight = SunTimes.For(new DateOnly(2026, 3, 20), latitude, 0, TimeSpan.Zero).Daylight;
            Assert.NotNull(daylight);
            Assert.InRange(daylight!.Value.TotalHours, 11.7, 12.5);
        }
    }

    [Fact]
    public void TheSeasonsAreTheOtherWayRoundInTheSouthernHemisphere()
    {
        var sydney = (Lat: -33.87, Lon: 151.21);
        var june = SunTimes.For(new DateOnly(2026, 6, 21), sydney.Lat, sydney.Lon, TimeSpan.FromHours(10)).Daylight;
        var december = SunTimes.For(new DateOnly(2026, 12, 21), sydney.Lat, sydney.Lon, TimeSpan.FromHours(11)).Daylight;

        Assert.True(june < december, "June should be the short day south of the equator.");
    }

    [Fact]
    public void InsideTheArcticCircleThereAreDaysWithNoSunriseAndDaysWithNoSunset()
    {
        // Tromsø. These are real answers, not failures, so they get their own flags rather
        // than a null that reads like an error.
        const double lat = 69.65, lon = 18.96;

        var midwinter = SunTimes.For(new DateOnly(2026, 12, 21), lat, lon, TimeSpan.FromHours(1));
        Assert.True(midwinter.PolarNight);
        Assert.Null(midwinter.Sunrise);
        Assert.Null(midwinter.Daylight);

        var midsummer = SunTimes.For(new DateOnly(2026, 6, 21), lat, lon, TimeSpan.FromHours(2));
        Assert.True(midsummer.PolarDay);
        Assert.Null(midsummer.Sunset);
    }

    [Fact]
    public void TheReturnedTimesCarryTheOffsetTheyWereAskedFor()
    {
        var day = SunTimes.For(new DateOnly(2026, 6, 21), NyLat, NyLon, TimeSpan.FromHours(-4));
        Assert.Equal(TimeSpan.FromHours(-4), day.Sunrise!.Value.Offset);
    }
}

public class InstalledVersionTests
{
    [Theory]
    [InlineData("a1b2c3d4e5f6", "a1b2c3d4e5f6")]
    [InlineData("1.4.2", "1.4.2")]
    [InlineData("a1b2c3+abcdef", "a1b2c3")]
    [InlineData("  a1b2c3  ", "a1b2c3")]
    public void APlausibleStampIsKept(string raw, string expected)
        => Assert.Equal(expected, UpdateChecker.Sanitise(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u0001")]              // a control character rendered as an unprintable box
    [InlineData("\u200b")]              // zero-width space
    [InlineData("${LABBYTWO_VERSION}")] // a build arg that never got expanded
    [InlineData("some version with spaces")]
    public void AnythingElseIsReportedAsUnstamped(string? raw)
    {
        // Whatever nonsense a broken build arg leaves behind, the page must not render it.
        Assert.Equal("dev", UpdateChecker.Sanitise(raw));
    }
}
