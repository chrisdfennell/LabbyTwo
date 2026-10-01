using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LabbyTwo.Core;

/// <summary>
/// A connection provider that can run one shell command on the machine it connects to and
/// hand back what it printed — what <c>{{ssh: NAS / docker restart plex}}</c> in a runbook
/// runs through.
///
/// The host has no SSH library of its own and should not grow one: the Terminal plugin
/// already holds the connection's credentials, its host-key pinning and its login rules, so
/// it implements this and the runbook button borrows all of that rather than a second copy
/// of it. A connection whose provider does not implement it gets a button that is drawn
/// disabled and says why.
///
/// Additive, like <see cref="IConnectionProvider.ActionsFor"/>: a separate interface, so no
/// plugin compiled before it exists has to know about it.
/// </summary>
public interface ICommandRunner
{
    /// <summary>
    /// Runs <paramref name="command"/> once, non-interactively, and returns its exit code and
    /// output. Should stop at <paramref name="timeout"/> or when <paramref name="ct"/> is
    /// cancelled, whichever comes first. Like a probe, prefer a result describing the failure
    /// to an exception — the runner catches either, but only one explains itself.
    /// </summary>
    Task<CommandResult> RunCommandAsync(Connection connection, string command, TimeSpan timeout, CancellationToken ct);
}

/// <summary>What one command did.</summary>
/// <param name="ExitCode">Null when it never finished — a timeout, a login that failed, a dropped connection.</param>
/// <param name="Output">What it printed, standard output then standard error. Raw: the runner cleans and masks it.</param>
/// <param name="Error">Why it could not be run or did not finish, in words; null when it ran to the end.</param>
public sealed record CommandResult(int? ExitCode, string Output, string? Error = null)
{
    public bool Ok => Error is null && ExitCode == 0;

    public static CommandResult Failed(string why, string output = "") => new(null, output, why);
}

/// <summary>
/// The rules of <c>{{ssh: …}}</c>, kept apart from anything that runs a command so each is a
/// test: reading what was written, which commands look dangerous enough to be typed out
/// before they run, the timeout, and what is kept of the output.
/// </summary>
public static partial class RunbookCommands
{
    /// <summary>The connection setting that has to be ticked before any note can run anything on it.</summary>
    public const string AllowKey = "allow_runbook_commands";

    /// <summary>
    /// The opt-in, for a provider that implements <see cref="ICommandRunner"/> to put in its
    /// own <see cref="IConnectionProvider.Fields"/>. Off by default: a note anybody with a
    /// login can edit should not become a way to run anything on the NAS until whoever set the
    /// NAS up has said so for that machine.
    /// </summary>
    public static FieldSpec AllowField => new(AllowKey, "Allow runbook commands", FieldKind.Bool, Default: "false",
        Help: "Lets a note run a command written into it on this machine, with {{ssh: name / command}}. Each run " +
              "asks first, shows the exact command, stops after its timeout and is written into the change feed. " +
              "Off means the button is drawn but cannot be pressed.");

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ShortestTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan LongestTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Lines of output shown under the button: enough for a <c>docker ps</c>, not a whole log.</summary>
    public const int OutputLines = 50;

    /// <summary>The longest command a note may hold. Anything longer belongs in a script on the machine.</summary>
    public const int MaxCommandLength = 1000;

    /// <summary>What <c>{{ssh: …}}</c> asks for.</summary>
    /// <param name="Connection">The connection's name or id, as written.</param>
    /// <param name="Command">The command, exactly as written — slashes, quotes, equals signs and all.</param>
    /// <param name="Label">What the button says instead of the command, from <c>label=</c>; empty for the command itself.</param>
    public sealed record Request(string Connection, string Command, TimeSpan Timeout, string Label);

    /// <summary>
    /// Reads <c>{{ssh: "NAS" / docker restart plex}}</c>. The connection is everything before
    /// the first slash that is not inside quotes; the command is everything after it, as
    /// written, so <c>cat /var/log/syslog</c> or <c>FOO=1 ./run.sh</c> needs no quoting. A
    /// command wrapped whole in quotes has them taken off — the way to write one containing
    /// <c>}}</c>. Options (<c>timeout=120</c>, <c>label="Restart Plex"</c>) go before the slash,
    /// with the connection, since anything after it is the command.
    /// </summary>
    public static Request? Read(Shortcode code, out string? problem)
    {
        problem = null;
        var source = code.Source.Trim();
        var inner = source.Length >= 4 ? source[2..^2] : "";
        var colon = inner.IndexOf(':');
        var args = colon >= 0 ? inner[(colon + 1)..] : "";

        var slash = -1;
        var quoted = false;
        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (quoted)
            {
                if (c == '\\' && i + 1 < args.Length)
                    i++;
                else if (c == '"')
                    quoted = false;
                continue;
            }
            if (c == '"')
                quoted = true;
            else if (c == '/')
            {
                slash = i;
                break;
            }
        }
        if (slash < 0)
        {
            problem = "Say which machine and which command: {{ssh: NAS / docker restart plex}}.";
            return null;
        }

        var head = Shortcodes.Parse("{{ssh: " + args[..slash] + "}}");
        var connection = head?.Part(0) ?? "";
        var command = Unquote(args[(slash + 1)..].Trim());
        if (connection.Length == 0)
        {
            problem = "Say which machine to run it on: {{ssh: NAS / docker restart plex}}.";
            return null;
        }
        if (command.Length == 0)
        {
            problem = $"Say what to run on {connection}: {{{{ssh: {connection} / uptime}}}}.";
            return null;
        }
        if (command.Length > MaxCommandLength)
        {
            problem = $"That command is over {MaxCommandLength} characters. Put it in a script on the machine and run the script.";
            return null;
        }
        if (command.Any(c => c is '\n' or '\r' or '\0'))
        {
            problem = "A command has to be one line.";
            return null;
        }

        var timeout = Timeout(head?.Option("timeout"), out var badTimeout);
        if (timeout is null)
        {
            problem = badTimeout;
            return null;
        }
        return new Request(connection, command, timeout.Value, head?.Option("label") ?? "");
    }

    /// <summary>
    /// <c>timeout=</c> in seconds, or written like <c>90s</c> or <c>2m</c>: between five seconds
    /// and ten minutes. Blank is a minute.
    /// </summary>
    public static TimeSpan? Timeout(string? written, out string? problem)
    {
        problem = null;
        var text = (written ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0)
            return DefaultTimeout;

        var match = TimeoutWord().Match(text);
        if (!match.Success)
        {
            problem = $"“{written}” is not a timeout. Write it in seconds, like timeout=90, or timeout=2m.";
            return null;
        }
        var count = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var span = match.Groups[2].Value is "m" or "min" ? TimeSpan.FromMinutes(count) : TimeSpan.FromSeconds(count);
        if (span < ShortestTimeout || span > LongestTimeout)
        {
            problem = $"A command's timeout is between 5 seconds and 10 minutes; “{written}” is outside that.";
            return null;
        }
        return span;
    }

    [GeneratedRegex(@"^(\d{1,4})\s*(s|sec|m|min)?$")]
    private static partial Regex TimeoutWord();

    /// <summary>
    /// Shapes of command that destroy something or take a machine away, each with the words
    /// the confirmation says. Deliberately a short list of the obvious ones, read loosely:
    /// it is a speed bump in front of a slip of the thumb, not a sandbox — a command that
    /// does not match still runs only after it has been shown and confirmed.
    /// </summary>
    private static readonly (Regex Pattern, string Why)[] Dangers =
    [
        (new(@"\brm\s+(?:-\S+\s+)*?(?:-[a-zA-Z]*[rR][a-zA-Z]*|--recursive)(?:\s|$)", RegexOptions.CultureInvariant), "deletes files recursively"),
        (new(@"\bmkfs(?:\.\w+)?\b|\bwipefs\b|\b(?:s?fdisk|parted|sgdisk)\b", RegexOptions.CultureInvariant), "changes disks or their partitions"),
        (new(@"(?:^|[\s;&|(])dd\s", RegexOptions.CultureInvariant), "writes raw bytes with dd"),
        (new(@">\s*/dev/(?:sd|hd|nvme|vd|xvd|mmcblk|disk)", RegexOptions.CultureInvariant), "writes straight onto a disk"),
        (new(@"\b(?:shutdown|reboot|halt|poweroff)\b|\binit\s+[06]\b|\bsystemctl\s+(?:reboot|poweroff|halt|kexec)\b", RegexOptions.CultureInvariant), "restarts or switches off the machine"),
        (new(@":\s*\(\s*\)\s*\{", RegexOptions.CultureInvariant), "is a fork bomb"),
        (new(@"\b(?:zpool|zfs)\s+destroy\b|\blvremove\b|\bvgremove\b", RegexOptions.CultureInvariant), "destroys a pool or volume"),
        (new(@"\bdocker\s+(?:system|volume|image|container|network|builder)\s+prune\b|\bdocker\s+volume\s+rm\b", RegexOptions.CultureInvariant), "deletes Docker data"),
        (new(@"\bch(?:mod|own)\s+(?:-\w+\s+)*-R\b.*\s/(?:\s|$)", RegexOptions.CultureInvariant), "changes permissions on everything from the root down"),
    ];

    /// <summary>Why the command looks dangerous enough to be typed out before it runs, or null.</summary>
    public static string? Danger(string command)
    {
        foreach (var (pattern, why) in Dangers)
        {
            if (pattern.IsMatch(command))
                return why;
        }
        return null;
    }

    /// <summary>
    /// The last <paramref name="lines"/> lines of what a command printed, cleaned for showing
    /// as text: terminal colour codes and other control characters taken out, trailing blank
    /// lines dropped, and each line passed through <paramref name="mask"/> — the log search's
    /// secret masking, so a command that prints an environment shows it the way a log would.
    /// </summary>
    public static IReadOnlyList<string> Tail(string output, Func<string, string> mask, int lines = OutputLines)
    {
        if (string.IsNullOrEmpty(output))
            return [];
        var all = AnsiEscape().Replace(output, "").Replace("\r\n", "\n").Split('\n');
        var end = all.Length;
        while (end > 0 && all[end - 1].Trim().Length == 0)
            end--;
        var start = Math.Max(0, end - Math.Max(1, lines));
        var kept = new List<string>(end - start);
        for (var i = start; i < end; i++)
            kept.Add(mask(Printable(all[i])));
        return kept;
    }

    /// <summary>A line with everything but tabs and printable characters taken out — a stray carriage return or bell is not text.</summary>
    private static string Printable(string line)
    {
        // A carriage return mid-line is a progress bar redrawing itself; what is left after
        // the last one is what was on screen.
        var cr = line.LastIndexOf('\r');
        if (cr >= 0)
            line = line[(cr + 1)..];
        if (!line.Any(char.IsControl))
            return line;
        var text = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (c == '\t' || !char.IsControl(c))
                text.Append(c);
        }
        return text.ToString();
    }

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex AnsiEscape();

    private static string Unquote(string text)
    {
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
            return text;
        var value = new StringBuilder(text.Length);
        for (var i = 1; i < text.Length - 1; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length - 1)
            {
                value.Append(text[++i]);
                continue;
            }
            if (c == '"')
                return text; // Two quoted strings, not one: leave it as written.
            value.Append(c);
        }
        return value.ToString();
    }
}
