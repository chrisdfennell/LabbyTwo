using System.Text;

namespace LabbyTwo.Core;

/// <summary>
/// One volume's share of a capacity metric: <c>disk_percent:vol2</c> alongside the
/// <c>disk_percent</c> that has always been recorded for the fullest volume.
///
/// Why this exists: a NAS reported one number, the fullest volume, and so could only ever
/// forecast that one. Volume 1 sitting at 90% and not moving while Volume 2 climbs from 40%
/// by a few points a week gets the wrong answer — the fullest is fine, and the one that
/// will actually run out has no history at all. Recording each volume as its own series
/// gives each its own forecast. The aggregate stays exactly as it was, because dashboards,
/// rules, charts, exports and plugins already read it.
///
/// Why a suffix on the existing name rather than a new name per volume: everything that
/// understands <c>disk_percent</c> — its unit, that it fills towards 100, how to label a
/// forecast of it — should understand <c>disk_percent:vol2</c> without being told again.
/// The colon is already how <see cref="CapacityMetric"/> derives one key from another, it
/// is legal in a Prometheus name, and no provider uses it in an ordinary key.
///
/// Why the provider's id rather than the name somebody typed: renaming "Media" to
/// "Films" in the NAS's own UI should not start a new series and orphan a year of history.
/// A provider passes its stable id where it has one (QTS's volume number, DSM's
/// <c>volume_1</c>); only when it has none does the name get slugged into the key.
/// </summary>
public static class VolumeMetric
{
    /// <summary>Between the measured metric and the volume: <c>disk_percent:vol2</c>.</summary>
    public const char Separator = ':';

    /// <summary>
    /// The most volumes one connection records. Each is a series in the samples table and
    /// an hourly summary a year long, so a storage server with two hundred iSCSI LUNs must
    /// not quietly become two hundred series. Sixteen is more volumes than any home NAS
    /// has, and the aggregate still covers anything past it.
    /// </summary>
    public const int MaxPerConnection = 16;

    /// <summary>Long enough for any id or name a NAS uses, short enough to read in a rule.</summary>
    public const int MaxSlugLength = 40;

    /// <summary>What a provider knows about one volume, before any of it is trusted.</summary>
    /// <param name="Id">The provider's own identifier, if it has one that survives a rename.</param>
    /// <param name="Name">What the NAS calls it, for the label.</param>
    /// <param name="Total">Size, in any unit, as long as <paramref name="Used"/> is in the same one.</param>
    /// <param name="Used">How much of it is used.</param>
    public sealed record Reading(string? Id, string? Name, double Total, double Used);

    /// <summary>One volume worth recording: its metric key, its display name and how full it is.</summary>
    public sealed record Series(string Key, string Name, double Percent);

    /// <summary>The per-volume key for a measured metric and a volume id or name.</summary>
    public static string KeyFor(string metric, string volume) =>
        metric + Separator + (Slug(volume) ?? "volume");

    /// <summary>
    /// Whether a key is one volume of a measured metric, and which. A forecast key
    /// (<c>days_until_full:disk_percent</c>) also has a colon in it and is not one; that
    /// is <see cref="CapacityMetric"/>'s to parse.
    /// </summary>
    public static bool TryParse(string? key, out string metric, out string volume)
    {
        metric = "";
        volume = "";
        if (key is null || CapacityMetric.TryParse(key, out _))
            return false;

        var at = key.IndexOf(Separator);
        if (at <= 0 || at == key.Length - 1 || key.IndexOf(Separator, at + 1) >= 0)
            return false;

        metric = key[..at];
        volume = key[(at + 1)..];
        return true;
    }

    /// <summary>
    /// A key-safe form of an id or name: lower case letters, digits, underscores and dashes,
    /// with anything else run together into one dash. Null when nothing usable is left, so
    /// a volume with neither an id nor a name is left out rather than keyed as "".
    /// </summary>
    public static string? Slug(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var slug = new StringBuilder(raw.Length);
        foreach (var character in raw.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
                slug.Append(character);
            else if (slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
        }

        var text = slug.ToString().Trim('-', '_');
        if (text.Length > MaxSlugLength)
            text = text[..MaxSlugLength].TrimEnd('-', '_');
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// A name for a volume nobody has named yet — after a restart, before the provider's
    /// first probe has said what it is called. "vol2", "volume_2" and "2" all read as
    /// "Volume 2"; anything else is the slug with its dashes turned back into spaces.
    /// </summary>
    public static string FallbackName(string volume)
    {
        var words = volume.Replace('_', ' ').Replace('-', ' ').Trim();
        var digits = words.TrimStart("volume".ToCharArray()).Trim();
        if (digits.Length > 0 && digits.All(char.IsAsciiDigit)
            && (words.StartsWith("vol", StringComparison.OrdinalIgnoreCase) || words.All(char.IsAsciiDigit)))
            return $"Volume {digits}";

        return words.Length == 0 ? volume : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>
    /// How one volume is labelled and formatted: the measured metric's unit, precision and
    /// capacity, under the volume's own name. Just the name — "Volume 2", "tank" — because
    /// that is what reads right everywhere the label is used: "Volume 2 is 91%" in an
    /// alert, "Volume 2 full in about 3 weeks" in a forecast, and a row on the NAS's card.
    /// The aggregate is labelled the same way ("Fullest volume", not "Fullest volume used").
    /// </summary>
    public static MetricSpec SpecFor(MetricSpec measured, string key, string? name)
    {
        var label = name is { Length: > 0 } given
            ? given.Trim()
            : TryParse(key, out _, out var volume) ? FallbackName(volume) : key;
        return measured with { Key = key, Label = label };
    }

    /// <summary>
    /// Names that are the machine's own rather than somewhere to keep things. None of the
    /// NAS APIs used here normally list them, but a firmware that one day does should not
    /// put a boot pool at the top of "Running out".
    /// </summary>
    private static readonly HashSet<string> SystemNames = new(
        ["system", "boot", "boot-pool", "freenas-boot", "swap", "tmp", "tmpfs", "cachedev0"],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsSystem(string? name) => name is { Length: > 0 } && SystemNames.Contains(name.Trim());

    /// <summary>
    /// The volumes worth a series of their own, in the order the provider listed them.
    /// Left out: anything with no size (an unmounted, initialising or failed volume reports
    /// zero, and "0% of nothing" is not a volume filling), anything with neither id nor name
    /// to key it by, and system volumes. Capped at <see cref="MaxPerConnection"/> in listing
    /// order rather than by fullness, so the same volumes are recorded every probe — ranking
    /// by fullness would let a volume drop in and out of history as its neighbours changed.
    /// Two volumes whose ids slug to the same key keep both, the second with a number on
    /// the end, rather than one silently overwriting the other.
    /// </summary>
    public static IReadOnlyList<Series> Select(string metric, IEnumerable<Reading> volumes)
    {
        var result = new List<Series>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var volume in volumes)
        {
            if (result.Count >= MaxPerConnection)
                break;
            if (!(double.IsFinite(volume.Total) && volume.Total > 0 && double.IsFinite(volume.Used)))
                continue;
            if (IsSystem(volume.Name) || IsSystem(volume.Id))
                continue;
            if ((Slug(volume.Id) ?? Slug(volume.Name)) is not { } slug)
                continue;

            var key = metric + Separator + slug;
            for (var n = 2; !used.Add(key); n++)
                key = $"{metric}{Separator}{slug}-{n}";

            var percent = Math.Clamp(volume.Used / volume.Total * 100, 0, 100);
            var name = volume.Name is { Length: > 0 } given && !string.IsNullOrWhiteSpace(given)
                ? given.Trim()
                : FallbackName(slug);
            result.Add(new Series(key, name, percent));
        }

        return result;
    }

    /// <summary>Specs for the series <see cref="Select"/> chose, for a provider's <c>MetricsFor</c>.</summary>
    public static IEnumerable<MetricSpec> SpecsFor(MetricSpec measured, IEnumerable<Series> series) =>
        series.Select(s => SpecFor(measured, s.Key, s.Name));

    /// <summary>Whether <paramref name="key"/> is <paramref name="metric"/> itself or one of its volumes.</summary>
    public static bool BelongsTo(string key, string metric) =>
        string.Equals(key, metric, StringComparison.OrdinalIgnoreCase)
        || (TryParse(key, out var measured, out _) && string.Equals(measured, metric, StringComparison.OrdinalIgnoreCase));
}
