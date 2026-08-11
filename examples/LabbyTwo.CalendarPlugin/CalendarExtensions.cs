using LabbyTwo.Core;

namespace LabbyTwo.CalendarPlugin;

/// <summary>The widget descriptor. The component is <see cref="UpcomingEvents"/>.</summary>
public sealed class UpcomingEventsWidget : IWidgetType
{
    public string Type => "calendar-upcoming";
    public string DisplayName => "Calendar — what's on";
    public string Icon => "📅";
    public string Description => "Upcoming events from a calendar feed, grouped by day.";
    public IReadOnlyList<string> ProviderTypes => ["ics-calendar"];
    public int DefaultWidth => 4;

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("days", "Days to show", FieldKind.Number, Default: "7"),
        new("limit", "Most events to list", FieldKind.Number, Default: "8"),
    ];

    public Type Component => typeof(UpcomingEvents);
}

/// <summary>
/// The third extension point, and the least code of any of them: a tab kind is a
/// descriptor and a component. Adding one changes nothing else — not the router, not the
/// nav, not the tab editor's form, which builds itself from <see cref="Fields"/>.
/// </summary>
public sealed class AgendaTabKind : ITabKind
{
    public string Kind => "calendar-agenda";
    public string DisplayName => "Agenda";
    public string Icon => "📅";
    public string Description => "A whole page for one calendar — a card per day for as far ahead as you like.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("connection", "Calendar", FieldKind.Connection, ProviderFilter: "ics-calendar",
            Help: "Leave blank to use the only calendar connection you have."),
        new("days", "Days to show", FieldKind.Number, Default: "14"),
    ];

    public Type Component => typeof(AgendaTab);
}
