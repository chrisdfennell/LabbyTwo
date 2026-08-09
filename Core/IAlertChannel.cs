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
