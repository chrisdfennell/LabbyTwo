using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>
/// The two kinds of number a Bitcoin miner reports that no other integration here does, and
/// which read badly as a plain decimal: hashrate and share difficulty.
///
/// Both span absurd ranges. An ESP32 lottery miner does well under a megahash a second and a
/// Bitaxe a terahash; written in the gigahashes they are stored in, one is "0.00" and the
/// other "1200.00" — and neither tells you anything at a glance. Difficulty is worse: the
/// network's is in the hundreds of trillions, a lottery miner's pool hands out shares at
/// 0.002, and the same card shows both. So the stored value stays in one unit (what alert
/// rules and charts compare against) and only the words adapt, the way the miners' own
/// screens do it — "840 KH/s", "132.8T".
///
/// A metric opts in by declaring <see cref="GigahashesPerSecond"/> or <see cref="Difficulty"/>
/// as its unit; <see cref="Units.Format(double, string, int, Units.Preferences)"/> — which every
/// tile, chart, table and alert line goes through — asks here first.
/// </summary>
public static class MinerUnits
{
    /// <summary>
    /// The stored unit of every hashrate: gigahashes per second, because that is what AxeOS
    /// (the Bitaxe firmware) reports and what NMMiner's API mirrors.
    /// </summary>
    public const string GigahashesPerSecond = " GH/s";

    /// <summary>
    /// A marker unit for a share or network difficulty. It is never printed — a difficulty is
    /// a bare number — but it is what tells the formatter to say "132.8T" rather than
    /// "132800000000000".
    /// </summary>
    public const string Difficulty = " diff";

    private static readonly string[] HashUnits = ["H/s", "KH/s", "MH/s", "GH/s", "TH/s", "PH/s", "EH/s"];

    private static readonly (char Suffix, double Scale)[] Suffixes =
    [
        ('K', 1e3), ('M', 1e6), ('G', 1e9), ('T', 1e12), ('P', 1e15), ('E', 1e18),
    ];

    /// <summary>
    /// The formatting for a miner's unit, or false for any other unit so the caller carries
    /// on as before.
    /// </summary>
    public static bool TryFormat(double value, string unit, out string text)
    {
        switch (unit)
        {
            case GigahashesPerSecond:
                text = Hashrate(value);
                return true;
            case Difficulty:
                text = FormatDifficulty(value);
                return true;
            default:
                text = "";
                return false;
        }
    }

    /// <summary>
    /// A hashrate given in GH/s, in whichever unit puts it between 1 and 1000 — "840 KH/s",
    /// "1.21 TH/s". Two decimals under ten, one under a hundred, none above: three
    /// significant figures, which is more than any miner's own figure deserves.
    /// </summary>
    public static string Hashrate(double gigahashes)
    {
        if (double.IsNaN(gigahashes) || double.IsInfinity(gigahashes))
            return "—";

        var hashes = gigahashes * 1e9;
        if (hashes <= 0)
            return "0 H/s";

        var index = 0;
        while (index < HashUnits.Length - 1 && hashes >= 1000)
        {
            hashes /= 1000;
            index++;
        }

        return Trim(hashes) + " " + HashUnits[index];
    }

    /// <summary>
    /// A difficulty as miners write it: "827.47", "0.002", "132.8T". Under a thousand it is
    /// left alone (with up to four decimals for the tiny share difficulties a lottery pool
    /// sets); above, it takes the K/M/G/T/P/E suffix that keeps it under a thousand.
    /// </summary>
    public static string FormatDifficulty(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return "—";

        var magnitude = Math.Abs(value);
        if (magnitude < 1)
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        if (magnitude < 1000)
            return value.ToString("0.##", CultureInfo.InvariantCulture);

        var (suffix, scale) = Suffixes[0];
        foreach (var candidate in Suffixes)
        {
            if (magnitude >= candidate.Scale)
                (suffix, scale) = candidate;
        }

        return (value / scale).ToString("0.##", CultureInfo.InvariantCulture) + suffix;
    }

    /// <summary>
    /// Reads a difficulty however a miner wrote it: a JSON number, or a string such as
    /// "306.59 " (NMMiner pads them), "132.8T", "1.2M" or "4.29G". The suffix is SI — K, M,
    /// G, T, P, E — and forgiven in lower case, since "k" never means anything else; null for
    /// anything that is not a number at all.
    /// </summary>
    public static double? ParseDifficulty(string? written)
    {
        if (string.IsNullOrWhiteSpace(written))
            return null;

        var text = written.Trim();
        var scale = 1.0;
        var last = char.ToUpperInvariant(text[^1]);
        foreach (var (suffix, factor) in Suffixes)
        {
            if (last != suffix)
                continue;
            scale = factor;
            text = text[..^1].TrimEnd();
            break;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
               && !double.IsNaN(number) && !double.IsInfinity(number)
            ? number * scale
            : null;
    }

    private static string Trim(double scaled) => scaled switch
    {
        < 10 => scaled.ToString("0.##", CultureInfo.InvariantCulture),
        < 100 => scaled.ToString("0.#", CultureInfo.InvariantCulture),
        _ => scaled.ToString("0", CultureInfo.InvariantCulture),
    };
}
