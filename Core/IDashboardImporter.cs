using System.Text;

namespace LabbyTwo.Core;

/// <summary>
/// Reads somebody else's dashboard config and describes it in LabbyTwo's terms. The
/// fourth extension point: an importer never touches the database, it just returns an
/// <see cref="ImportPlan"/>, which means a new one is a pure function of a file and can
/// be unit-tested without a running app.
/// </summary>
public interface IDashboardImporter
{
    /// <summary>Stable key, e.g. "homer".</summary>
    string Key { get; }

    string DisplayName { get; }
    string Icon { get; }

    /// <summary>One line naming the file the user should upload.</summary>
    string Description { get; }

    /// <summary>File extensions this importer will consider, lower case with the dot.</summary>
    IReadOnlyList<string> Extensions => [];

    /// <summary>
    /// Whether this file looks like the format. Used for the "detect automatically"
    /// path, so the user can drop a file in without knowing what it is called.
    /// Should be cheap and must not throw.
    /// </summary>
    bool CanHandle(ImportSource source);

    /// <summary>Parses the file. Throw <see cref="FormatException"/> with a readable message on bad input.</summary>
    ImportPlan Read(ImportSource source);
}

/// <summary>An uploaded file, decoded lazily so a binary importer can skip the text.</summary>
public sealed class ImportSource(string fileName, byte[] content)
{
    public string FileName { get; } = fileName;
    public byte[] Content { get; } = content;

    public string Extension => Path.GetExtension(FileName).ToLowerInvariant();

    private string? _text;

    /// <summary>UTF-8 text with any byte-order mark removed.</summary>
    public string Text => _text ??= new UTF8Encoding(false).GetString(Content).TrimStart('﻿');
}

/// <summary>
/// What an import would create, in LabbyTwo's own vocabulary but with no ids yet.
/// Connections are referenced by a local <see cref="ImportedConnection.Ref"/> so a widget
/// can point at one before either exists.
/// </summary>
public sealed record ImportPlan
{
    public List<ImportedConnection> Connections { get; init; } = [];
    public List<ImportedTab> Tabs { get; init; } = [];

    /// <summary>Anything the importer had to guess at or drop, shown before the user commits.</summary>
    public List<string> Notes { get; init; } = [];

    public int WidgetCount => Tabs.Sum(t => t.Widgets.Count);
}

public sealed record ImportedConnection(
    string Ref,
    string Provider,
    string Name,
    string Icon = "",
    SettingsBag? Settings = null)
{
    public SettingsBag Values => Settings ?? new SettingsBag();
}

public sealed record ImportedTab(
    string Name,
    string Icon = "",
    string Kind = TabKinds.Grid,
    SettingsBag? Settings = null)
{
    public List<ImportedWidget> Widgets { get; init; } = [];
    public SettingsBag Values => Settings ?? new SettingsBag();
}

public sealed record ImportedWidget(
    string Type,
    string Title = "",
    int Width = 4,
    SettingsBag? Settings = null,
    string? ConnectionRef = null)
{
    public SettingsBag Values => Settings ?? new SettingsBag();
}
