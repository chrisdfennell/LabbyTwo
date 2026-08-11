using LabbyTwo.Core;

namespace LabbyTwo.GoogleCalendarPlugin;

/// <summary>
/// A whole page for one calendar: month, week or list, and events you can add, edit and
/// delete without leaving the dashboard.
/// </summary>
public sealed class GoogleCalendarTabKind : ITabKind
{
    public const string KindKey = "google-calendar";

    public string Kind => KindKey;
    public string DisplayName => "Calendar";
    public string Icon => "📆";
    public string Description =>
        "A real calendar — month, week and list views of a Google calendar, with adding and editing.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("connection", "Calendar", FieldKind.Connection, ProviderFilter: "google-calendar",
            Help: "Leave blank to use the only Google calendar you have connected."),

        new("view", "Opens in", FieldKind.Select, Default: "month", Options:
        [
            new SelectOption("month", "Month"),
            new SelectOption("week", "Week"),
            new SelectOption("list", "List"),
        ], Help: "Whichever view you switch to is remembered for the session; this is where it starts."),

        new("week_starts", "Week starts on", FieldKind.Select, Default: "sunday", Options:
        [
            new SelectOption("sunday", "Sunday"),
            new SelectOption("monday", "Monday"),
        ]),

        new("read_only", "Read only", FieldKind.Bool, Default: "false",
            Help: "Hides adding, editing and deleting. Worth turning on for a wall display."),
    ];

    public Type Component => typeof(CalendarTab);
}

/// <summary>The same events on a dashboard, for people who want a card rather than a page.</summary>
public sealed class GoogleCalendarWidget : IWidgetType
{
    public string Type => "google-calendar-upcoming";
    public string DisplayName => "Calendar — coming up";
    public string Icon => "📆";
    public string Description => "The next few events from a Google calendar, grouped by day.";

    public IReadOnlyList<string> ProviderTypes => ["google-calendar"];
    public int DefaultWidth => 4;

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("days", "Days to show", FieldKind.Number, Default: "7"),
        new("limit", "Most events to list", FieldKind.Number, Default: "8"),
    ];

    public Type Component => typeof(UpcomingEvents);
}
