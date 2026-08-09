using System.Text.Json;

namespace LabbyTwo.Core;

/// <summary>
/// A configured instance of a provider — "my NAS", "Plex", "the router's web UI".
/// The provider decides what <see cref="Settings"/> means; everything here is generic
/// so a new integration never needs a schema change.
/// </summary>
public sealed record Connection
{
    public string Id { get; init; } = Ids.New();
    public string Provider { get; init; } = "";
    public string Name { get; init; } = "";
    public string Icon { get; init; } = "";
    public bool Enabled { get; init; } = true;

    /// <summary>Off silences down/up notifications for this one connection without unmonitoring it.</summary>
    public bool AlertsEnabled { get; init; } = true;

    public int Sort { get; init; }
    public SettingsBag Settings { get; init; } = new();
}

/// <summary>
/// One entry in the nav. <see cref="Kind"/> names a registered tab kind (grid, embed,
/// notes…) which decides how the page renders and what <see cref="Settings"/> holds.
/// </summary>
public sealed record Tab
{
    public string Id { get; init; } = Ids.New();
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Kind { get; init; } = TabKinds.Grid;
    public int Sort { get; init; }
    public bool Enabled { get; init; } = true;
    public SettingsBag Settings { get; init; } = new();
}

/// <summary>A card on a grid tab. Optionally bound to a <see cref="Connection"/>.</summary>
public sealed record Widget
{
    public string Id { get; init; } = Ids.New();
    public string TabId { get; init; } = "";
    public string Type { get; init; } = "";
    public string Title { get; init; } = "";
    public string? ConnectionId { get; init; }
    public int Sort { get; init; }

    /// <summary>Grid columns out of 12, so a row can mix a wide chart with narrow tiles.</summary>
    public int Width { get; init; } = 4;

    public SettingsBag Settings { get; init; } = new();
}

public static class Ids
{
    public static string New() => Guid.NewGuid().ToString("n")[..12];
}

/// <summary>
/// The untyped settings blob every configurable thing carries. Stored as one JSON
/// column, which is what lets providers, widgets and tab kinds add fields without a
/// migration. Values are strings; the accessors below do the parsing.
/// </summary>
public sealed class SettingsBag : Dictionary<string, string>
{
    public SettingsBag() : base(StringComparer.OrdinalIgnoreCase) { }

    public SettingsBag(IDictionary<string, string> source)
        : base(source, StringComparer.OrdinalIgnoreCase) { }

    public string Get(string key, string fallback = "")
        => TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    public int GetInt(string key, int fallback = 0)
        => TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    public double GetDouble(string key, double fallback = 0)
        => TryGetValue(key, out var value) && double.TryParse(value, out var parsed) ? parsed : fallback;

    public bool GetBool(string key, bool fallback = false)
        => TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    public string ToJson() => JsonSerializer.Serialize(this);

    public static SettingsBag FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new SettingsBag();
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return parsed is null ? new SettingsBag() : new SettingsBag(parsed);
    }

    public SettingsBag Clone() => new(this);
}
