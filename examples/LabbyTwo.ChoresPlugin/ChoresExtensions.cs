using LabbyTwo.Core;

namespace LabbyTwo.ChoresPlugin;

public sealed class ChoresTabKind : ITabKind
{
    public string Kind => "chores";
    public string DisplayName => "Chores";
    public string Icon => "🧹";
    public string Description =>
        "A list of recurring jobs with due dates, each assignable to somebody — filters, gutters, " +
        "backups you do by hand.";

    // No fields: the tab holds nothing configurable, because the data is the point.
    public Type Component => typeof(ChoresTab);
}

public sealed class ChoresDueWidget : IWidgetType
{
    public string Type => "chores-due";
    public string DisplayName => "Chores due";
    public string Icon => "🧹";
    public string Description => "Only the chores that are due or overdue, with a tick to clear them.";

    // No ProviderTypes: this widget needs no connection at all, which is what tells the
    // picker to offer it on any dashboard rather than only where something is bound.
    public int DefaultWidth => 3;

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("person", "Only this person's", FieldKind.Text,
            Help: "Blank shows everyone's. Chores nobody is assigned always show, because an " +
                  "unclaimed one is the one that gets forgotten."),
        new("within_days", "Also show ones due within (days)", FieldKind.Number, Default: "0",
            Help: "0 shows only what is due today or overdue."),
        new("limit", "Most to list", FieldKind.Number, Default: "5"),
    ];

    public Type Component => typeof(ChoresDue);
}
