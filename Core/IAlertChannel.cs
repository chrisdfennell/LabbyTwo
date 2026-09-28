namespace LabbyTwo.Core;

/// <summary>
/// Somewhere notifications get sent. Deliberately an <see cref="IConnectionProvider"/>:
/// a Discord webhook is a thing you configure, so it gets the same generated form, the
/// same picker entry and the same storage — with encrypted secrets — as a NAS does,
/// rather than a parallel settings system of its own.
/// </summary>
public interface IAlertChannel : IConnectionProvider
{
    /// <summary>Alert channels have nothing worth polling; the Test button sends a real message instead.</summary>
    bool IConnectionProvider.IsMonitored => false;

    Task SendAsync(Connection channel, Alert alert, CancellationToken ct);
}

public enum AlertLevel
{
    Down,
    Up,
    Info,
}

public sealed record Alert(AlertLevel Level, string Title, string Body)
{
    /// <summary>
    /// Ignores quiet hours. For the handful of things where "do not wake me" is the wrong
    /// answer — a tornado warning is the reason this exists. It does not override a
    /// silence or a dependency: those are somebody deliberately muting a specific thing,
    /// whereas quiet hours are a blanket rule that never knew about this.
    ///
    /// An init-only property rather than a fourth positional parameter, so every existing
    /// <c>new Alert(level, title, body)</c> in a prebuilt plugin still binds.
    /// </summary>
    public bool Urgent { get; init; }

    /// <summary>
    /// The body again, formatted in Markdown, for a channel that renders it — a Discord
    /// embed does; an SMS-shaped push does not, and would show the asterisks. Null for
    /// the ordinary one-line alerts, where there is nothing to format. A channel that does
    /// not know about this sends <see cref="Body"/>, which always says the same thing in
    /// plain text, so a plugin built before this existed still sends something readable.
    /// </summary>
    public string? Markdown { get; init; }

    /// <summary>
    /// What this alert is about, as a stable key — a connection, or a rule on a connection —
    /// for a channel that can replace an earlier notification with a later one. The recovery
    /// then takes the place of the outage on a phone's lock screen instead of stacking under
    /// it. Null when there is nothing to group by; a channel falls back to the title.
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>
    /// Where in LabbyTwo to go to see the thing this is about, relative to the app — for a
    /// channel whose notification can be tapped. Null means the dashboard.
    /// </summary>
    public string? Link { get; init; }

    public string Emoji => Level switch
    {
        AlertLevel.Down => "🔴",
        AlertLevel.Up => "🟢",
        _ => "ℹ️",
    };

    /// <summary>Discord embed / Slack attachment colour.</summary>
    public int Color => Level switch
    {
        AlertLevel.Down => 0xFF5C6C,
        AlertLevel.Up => 0x35D07F,
        _ => 0x4DA3FF,
    };

    public string PlainText => $"{Emoji} {Title}\n{Body}";
}
