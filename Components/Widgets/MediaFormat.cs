using System.Globalization;
using LabbyTwo.Services;

namespace LabbyTwo.Components.Widgets;

/// <summary>Formatting the media cards share, so a bar or a date reads the same on all of them.</summary>
public static class MediaFormat
{
    /// <summary>A width for a progress bar, clamped and invariant so the CSS is always valid.</summary>
    public static string Percent(double value) =>
        Math.Clamp(value, 0, 100).ToString("0.#", CultureInfo.InvariantCulture);

    public static string DayLabel(DateTime date) => date == DateTime.Today
        ? "Today"
        : date == DateTime.Today.AddDays(1) ? "Tomorrow" : date.ToString("ddd d MMM");

    /// <summary>
    /// Whichever numbers a download client actually reports. They disagree about which
    /// they publish — a torrent client has no queue in gigabytes, a Usenet one has no
    /// upload worth showing — so the line is assembled from what is there rather than
    /// printing dashes for the rest.
    /// </summary>
    public static string ClientLine(MediaStack.Client client)
    {
        var parts = new List<string>();
        if (client.DownMbps is { } down)
            parts.Add($"↓ {down:0.0}");
        if (client.UpMbps is { } up and > 0)
            parts.Add($"↑ {up:0.0}");
        if (client.RemainingGb is { } left and > 0)
            parts.Add($"{left:0.#} GB left");
        if (client.FreeDiskGb is { } free)
            parts.Add($"{free:0} GB free");

        return parts.Count > 0 ? string.Join(" · ", parts) : client.Paused ? "paused" : "—";
    }
}
