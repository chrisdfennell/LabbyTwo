using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>
/// How a wall screen rotates through tabs: which ones, how long each stays up, and what
/// happens when somebody walks up and touches it.
///
/// The defaults are app settings, so they can be changed from a phone without climbing up
/// to the wall. Every one of them can be overridden in the query string, because a wall
/// device is set up once by bookmarking a URL, and two screens in two rooms often want two
/// different rotations from the same installation.
/// </summary>
/// <param name="Tabs">Slugs, in the order to show them. Empty means every enabled tab, including ones added later.</param>
/// <param name="Seconds">How long each tab stays on screen.</param>
/// <param name="ResumeSeconds">How long the screen has to be left alone after a touch before rotation carries on. Zero means a touch never pauses it.</param>
/// <param name="Overlay">Whether a small clock and service summary sits in a corner.</param>
/// <param name="Nav">Whether the sidebar stays. Off by default: the point of a wall is the cards.</param>
/// <param name="At">The tab on screen, kept in the URL so a reload comes back to the same place.</param>
public sealed record WallOptions(
    IReadOnlyList<string> Tabs,
    int Seconds,
    int ResumeSeconds,
    bool Overlay,
    bool Nav,
    string? At = null)
{
    public const string TabsKey = "wall_tabs";
    public const string SecondsKey = "wall_seconds";
    public const string ResumeKey = "wall_resume_seconds";
    public const string OverlayKey = "wall_overlay";
    public const string NavKey = "wall_nav";

    /// <summary>
    /// Three is the floor because anything shorter is not a rotation anybody can read, and
    /// a typo of "6" for "60" should give a fast wall rather than a strobe.
    /// </summary>
    public const int MinSeconds = 3;
    public const int MaxSeconds = 24 * 60 * 60;
    public const int MaxResumeSeconds = 60 * 60;

    public static WallOptions Default => new([], 60, 60, true, false);

    /// <summary>The saved defaults, with anything unreadable left at the built-in value.</summary>
    public static WallOptions From(SettingsBag settings) => new(
        SplitSlugs(settings.Get(TabsKey)),
        ClampSeconds(settings.GetInt(SecondsKey, Default.Seconds)),
        ClampResume(settings.GetInt(ResumeKey, Default.ResumeSeconds)),
        ParseBool(settings.Get(OverlayKey)) ?? Default.Overlay,
        ParseBool(settings.Get(NavKey)) ?? Default.Nav);

    /// <summary>
    /// These options with the query string laid over them. Anything the query does not
    /// mention, or says in a way that cannot be read, keeps its saved value rather than
    /// falling to a built-in one, so a bookmark with a typo in it still behaves like the
    /// wall somebody configured.
    /// </summary>
    public WallOptions WithQuery(IReadOnlyDictionary<string, string> query)
    {
        var result = this;

        if (Read(query, "tabs") is { } tabs)
            result = result with { Tabs = SplitSlugs(tabs) };

        if (Read(query, "seconds") is { } seconds && int.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            result = result with { Seconds = ClampSeconds(s) };

        if (Read(query, "resume") is { } resume && int.TryParse(resume, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
            result = result with { ResumeSeconds = ClampResume(r) };

        // "clock" as well as "overlay": the clock is the half of it people ask for by name.
        if (ParseBool(Read(query, "overlay") ?? Read(query, "clock")) is { } overlay)
            result = result with { Overlay = overlay };

        if (ParseBool(Read(query, "nav")) is { } nav)
            result = result with { Nav = nav };

        if (Read(query, "at") is { Length: > 0 } at)
            result = result with { At = at.Trim() };

        return result;
    }

    /// <summary>
    /// A URL that reproduces these options on any device, whatever that installation's saved
    /// defaults are by then. Used for the "bookmark this" line on the settings page.
    /// </summary>
    public string ToUrl()
    {
        var parts = new List<string>();
        if (Tabs.Count > 0)
            parts.Add("tabs=" + string.Join(',', Tabs.Select(Uri.EscapeDataString)));
        parts.Add($"seconds={Seconds}");
        parts.Add($"resume={ResumeSeconds}");
        parts.Add($"overlay={(Overlay ? 1 : 0)}");
        if (Nav)
            parts.Add("nav=1");
        return "wall?" + string.Join('&', parts);
    }

    /// <summary>
    /// Where /t/{slug}?wall=1 goes: that one tab, with no chrome. Whatever else was in the
    /// query comes along, so ?wall=1&amp;overlay=0 means what it says, and a tabs= in it wins
    /// over the tab in the path, because somebody wrote it on purpose.
    /// </summary>
    public static string UrlForTab(string slug, IReadOnlyDictionary<string, string> query)
    {
        var parts = new List<string>();
        if (Read(query, "tabs") is null)
            parts.Add("tabs=" + Uri.EscapeDataString(slug));

        foreach (var (key, value) in query)
        {
            if (string.Equals(key, "wall", StringComparison.OrdinalIgnoreCase))
                continue;
            parts.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value));
        }

        return "wall?" + string.Join('&', parts);
    }

    /// <summary>Whether a ?wall= value asks for wall mode.</summary>
    public static bool IsOn(string? value) => ParseBool(value) == true;

    public static IReadOnlyList<string> SplitSlugs(string? value) =>
    [
        .. (value ?? "")
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
    ];

    public static int ClampSeconds(int value) => Math.Clamp(value, MinSeconds, MaxSeconds);

    /// <summary>Zero is allowed and means "never pause"; anything else gets the same floor as a tab.</summary>
    public static int ClampResume(int value) => value <= 0 ? 0 : Math.Clamp(value, MinSeconds, MaxResumeSeconds);

    /// <summary>Null for anything that is not clearly one or the other, so the caller keeps its own value.</summary>
    public static bool? ParseBool(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "on" => true,
        "0" or "false" or "no" or "off" => false,
        _ => null,
    };

    private static string? Read(IReadOnlyDictionary<string, string> query, string key)
    {
        foreach (var (k, v) in query)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return v;
        }
        return null;
    }
}

/// <summary>
/// Which tabs a wall shows and which comes next. Pure, because "the tab you were on was
/// deleted while the screen was showing it" is exactly the case nobody will be standing in
/// front of the wall to notice.
/// </summary>
public static class WallRotation
{
    /// <summary>
    /// The tabs to rotate through. Disabled tabs are left out even when named, because
    /// disabling a tab is how somebody takes it off every screen at once. Named slugs that do
    /// not exist are skipped, and if none of them do, the wall shows every enabled tab rather
    /// than nothing: a blank wall after a rename looks broken, and a full one does not.
    /// </summary>
    public static IReadOnlyList<Tab> Resolve(IEnumerable<Tab> tabs, IReadOnlyList<string> wanted)
    {
        var enabled = tabs.Where(t => t.Enabled).ToList();
        if (wanted.Count == 0)
            return enabled;

        var chosen = wanted
            .Select(slug => enabled.FirstOrDefault(t => string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase)))
            .OfType<Tab>()
            .DistinctBy(t => t.Id)
            .ToList();

        return chosen.Count > 0 ? chosen : enabled;
    }

    /// <summary>Where a wall starts: the tab named in the URL if it is still in the rotation, else the first.</summary>
    public static int Start(IReadOnlyList<Tab> rotation, string? at)
    {
        var index = IndexOf(rotation, at);
        return index >= 0 ? index : 0;
    }

    /// <summary>
    /// The index <paramref name="delta"/> steps from <paramref name="index"/>, wrapping at
    /// both ends. -1 for an empty rotation.
    /// </summary>
    public static int Step(int count, int index, int delta)
    {
        if (count <= 0)
            return -1;
        return (((index + delta) % count) + count) % count;
    }

    /// <summary>
    /// Where to be after the rotation itself changed under a running wall. The same tab if it
    /// is still there, wherever it moved to; otherwise the same position, pulled back inside
    /// the list — so deleting the tab on screen shows the one that took its place, rather
    /// than jumping back to the start.
    /// </summary>
    public static int Reposition(IReadOnlyList<Tab> rotation, string? currentSlug, int previousIndex)
    {
        if (rotation.Count == 0)
            return -1;
        var index = IndexOf(rotation, currentSlug);
        return index >= 0 ? index : Math.Clamp(previousIndex, 0, rotation.Count - 1);
    }

    public static int IndexOf(IReadOnlyList<Tab> rotation, string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return -1;
        for (var i = 0; i < rotation.Count; i++)
        {
            if (string.Equals(rotation[i].Slug, slug.Trim(), StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}

/// <summary>
/// When a wall moves on. Kept apart from the page, and handed the time rather than reading
/// a clock, so "touched, left alone, carried on" can be tested in microseconds rather than
/// by standing in front of a tablet.
///
/// A touch pauses rotation for the idle period, and when that runs out the tab on screen
/// gets a full turn of its own before the next one — the person may have walked away
/// mid-sentence, but the next person to glance up should not see it vanish immediately.
/// </summary>
public sealed class WallTimer(TimeSpan dwell, TimeSpan idle, DateTimeOffset now)
{
    public TimeSpan Dwell { get; } = dwell;
    public TimeSpan Idle { get; } = idle;

    /// <summary>When the next tab is due, if nothing else happens first.</summary>
    public DateTimeOffset NextAt { get; private set; } = now + dwell;

    /// <summary>The end of the current pause, or null when there has not been one.</summary>
    public DateTimeOffset? PausedUntil { get; private set; }

    public bool IsPaused(DateTimeOffset now) => PausedUntil is { } until && now < until;

    /// <summary>Somebody is using the screen. Does nothing when pausing is switched off.</summary>
    public void Interacted(DateTimeOffset now)
    {
        if (Idle <= TimeSpan.Zero)
            return;
        PausedUntil = now + Idle;
        NextAt = now + Idle + Dwell;
    }

    /// <summary>A new tab is on screen, by the timer or by hand, and gets its full turn.</summary>
    public void Stepped(DateTimeOffset now)
    {
        var start = IsPaused(now) ? PausedUntil!.Value : now;
        NextAt = start + Dwell;
    }

    public bool Due(DateTimeOffset now) => !IsPaused(now) && now >= NextAt;
}
