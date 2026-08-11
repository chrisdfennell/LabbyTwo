using LabbyTwo.Core;

namespace LabbyTwo.QnapFilesPlugin;

/// <summary>
/// A page for browsing the NAS you already added as a connection. The pairing is the
/// point of this example: the tab kind renders, <see cref="QnapFilesEndpoints"/> serves
/// the bytes, and neither needed a change to LabbyTwo to exist.
/// </summary>
public sealed class QnapFilesTabKind : ITabKind
{
    public string Kind => "qnap-files";
    public string DisplayName => "NAS files";
    public string Icon => "📂";
    public string Description =>
        "Browse, download and upload files on a QNAP you have already added as a connection.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("connection", "NAS", FieldKind.Connection,
            Help: "Leave blank to use the only QNAP you have.")
            { ProviderFilter = "qnap" },

        new("root", "Start in", FieldKind.Text, "/Public",
            Help: "Optional. Blank starts at the list of shared folders. This is where the tab " +
                  "opens, not a restriction — the account's own permissions decide what can be reached."),

        // Read-only by default. A dashboard is a thing people leave open on a tablet in
        // the kitchen, and the difference between a bad tap and a deleted share should be
        // a setting somebody turned on deliberately.
        new("read_only", "Read only", FieldKind.Bool, Default: "true",
            Help: "Browsing and downloading only. Turn this off to allow uploads, renames and deletes."),

        new("max_upload_mb", "Largest upload (MB)", FieldKind.Number, Default: "2048",
            Help: "Uploads pass through LabbyTwo, so this is a guard against a mistaken drag, not a NAS limit."),
    ];

    public Type Component => typeof(FilesTab);
}
