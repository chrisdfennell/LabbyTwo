using LabbyTwo.Core;

namespace LabbyTwo.Components.Widgets;

/// <summary>
/// Who is using a shared Intel GPU, and how busy it is. Bound to no connection on purpose,
/// like the Miners card: it gathers every Plex, Tautulli, Tdarr and Tunarr connection
/// itself, so it works with nothing but those configured — and reads the GPU's own load from
/// an <see cref="Providers.IntelGpuProvider"/> connection when there is one.
/// </summary>
public sealed class GpuWidget : IWidgetType
{
    public string Type => "gpu";
    public string DisplayName => "GPU — who's using Quick Sync";
    public string Icon => "🖥️";
    public string Description =>
        "Plex, Tdarr and Tunarr on a shared Intel GPU: who has what on it right now, how busy it is where that can be seen, " +
        "and a warning when they get in each other's way.";
    public int DefaultWidth => 4;
    public Type Component => typeof(GpuCard);

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("show_idle", "List apps with nothing on the GPU", FieldKind.Bool, Default: "true",
            Help: "Off shows only the apps using it right now."),
    ];
}
