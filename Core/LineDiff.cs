namespace LabbyTwo.Core;

/// <summary>One line of a diff: the same in both, only in the old text, or only in the new.</summary>
public enum DiffKind
{
    Same,
    Removed,
    Added,
}

/// <param name="OldLine">1-based line number in the old text; null for an added line.</param>
/// <param name="NewLine">1-based line number in the new text; null for a removed line.</param>
public sealed record DiffLine(DiffKind Kind, string Text, int? OldLine, int? NewLine);

/// <summary>
/// The difference between two versions of a note, line by line — what "Compare with
/// current" shows. A longest-common-subsequence diff: the lines both texts share, in order,
/// are kept, and everything else is a removal from the old or an addition in the new.
///
/// Written here rather than taken from a package because it is forty lines and a note is
/// small. The shared head and tail are cut off first, which is almost all of any real edit,
/// so the table is only ever as big as the part that changed. Past
/// <see cref="MaxCells"/> — two very different texts of thousands of lines each — it
/// stops looking for common lines in the middle and shows it all as removed and added,
/// which is still a true diff, only not the smallest one.
/// </summary>
public static class LineDiff
{
    /// <summary>The largest table worked out: four million cells, a few megabytes and a few milliseconds.</summary>
    public const long MaxCells = 4_000_000;

    public static IReadOnlyList<string> Lines(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.ReplaceLineEndings("\n").Split('\n');

    public static IReadOnlyList<DiffLine> Compare(string? oldText, string? newText)
    {
        var a = Lines(oldText);
        var b = Lines(newText);

        var head = 0;
        while (head < a.Count && head < b.Count && a[head] == b[head])
            head++;
        var tail = 0;
        while (tail < a.Count - head && tail < b.Count - head && a[a.Count - 1 - tail] == b[b.Count - 1 - tail])
            tail++;

        var result = new List<DiffLine>(a.Count + b.Count);
        for (var i = 0; i < head; i++)
            result.Add(new DiffLine(DiffKind.Same, a[i], i + 1, i + 1));

        var n = a.Count - head - tail;
        var m = b.Count - head - tail;
        if ((long)(n + 1) * (m + 1) > MaxCells)
        {
            for (var i = 0; i < n; i++)
                result.Add(new DiffLine(DiffKind.Removed, a[head + i], head + i + 1, null));
            for (var j = 0; j < m; j++)
                result.Add(new DiffLine(DiffKind.Added, b[head + j], null, head + j + 1));
        }
        else
        {
            // lcs[i, j]: how many lines the old middle from i on and the new from j on share.
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
            {
                for (var j = m - 1; j >= 0; j--)
                {
                    lcs[i, j] = a[head + i] == b[head + j]
                        ? lcs[i + 1, j + 1] + 1
                        : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[head + x] == b[head + y])
                {
                    result.Add(new DiffLine(DiffKind.Same, a[head + x], head + x + 1, head + y + 1));
                    x++;
                    y++;
                }
                // Removals before additions where either would do, as diff tools show them.
                else if (x < n && (y == m || lcs[x + 1, y] >= lcs[x, y + 1]))
                {
                    result.Add(new DiffLine(DiffKind.Removed, a[head + x], head + x + 1, null));
                    x++;
                }
                else
                {
                    result.Add(new DiffLine(DiffKind.Added, b[head + y], null, head + y + 1));
                    y++;
                }
            }
        }

        for (var t = tail; t > 0; t--)
        {
            var i = a.Count - t;
            var j = b.Count - t;
            result.Add(new DiffLine(DiffKind.Same, a[i], i + 1, j + 1));
        }
        return result;
    }
}
