using LabbyTwo.Core;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace LabbyTwo.TerminalPlugin;

/// <summary>
/// A live terminal: bytes in, bytes out, and a window size. Both backends reduce to this,
/// which is why the WebSocket pump in <see cref="TerminalEndpoints"/> knows nothing about
/// SSH or Docker.
/// </summary>
public interface ITerminalSession : IAsyncDisposable
{
    /// <summary>What to write across the top of the terminal once it opens.</summary>
    string Describe { get; }

    /// <summary>Zero means the far end hung up — the shell exited, or the container stopped.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    ValueTask ResizeAsync(int columns, int rows, CancellationToken ct);
}

/// <summary>
/// An interactive shell on an SSH host, with a real pty — so the far end knows the window
/// size, and full-screen programs work.
/// </summary>
public sealed class SshSession : ITerminalSession
{
    private readonly SshClient _client;
    private readonly ShellStream _shell;

    private SshSession(SshClient client, ShellStream shell, string describe)
    {
        _client = client;
        _shell = shell;
        Describe = describe;
    }

    public string Describe { get; }

    public static async Task<SshSession> OpenAsync(
        Connection connection, int columns, int rows, CancellationToken ct)
    {
        var watch = new SshHost.HostKeyWatch();
        var client = SshHost.Client(connection, watch);
        try
        {
            await client.ConnectAsync(ct);

            // xterm-256color rather than xterm: it is what the browser side actually is,
            // and a shell told otherwise draws prompts in eight colours on a terminal
            // that has 256. The pixel dimensions are zero because xterm.js resizes in
            // cells, and sending a lie about pixels is worse than sending nothing.
            var shell = client.CreateShellStream("xterm-256color",
                (uint)columns, (uint)rows, 0, 0, 16 * 1024);

            var host = connection.Settings.Get("host");
            var user = connection.Settings.Get("username");
            return new SshSession(client, shell, $"{user}@{host}");
        }
        catch
        {
            client.Dispose();

            // SSH.NET says "Host key could not be verified", which is true and tells
            // nobody what to do. The terminal is where most people will meet this, so it
            // gets the same sentence the connection's own probe would have given it.
            //
            // Deliberately without the original as an inner exception: callers reach for
            // GetBaseException to dig past wrappers like HttpRequestException, and that
            // would walk straight back to the sentence being replaced. Nothing is lost —
            // this message names the fingerprint, which the other one does not.
            if (watch.Rejected)
                throw new InvalidOperationException(SshHost.KeyChanged(watch));

            throw;
        }
    }

    // ShellStream reads synchronously, so this is sync-over-async on a pool thread. That
    // is the right trade here: there is one blocked thread per open terminal, and a
    // person watching a terminal is not a scale problem. Cancellation arrives as the
    // disposal below rather than through the token, which is why the pump disposes the
    // session to stop it rather than only cancelling.
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) =>
        _shell.ReadAsync(buffer, ct);

    /// <summary>
    /// The flush is not optional. ShellStream buffers writes and only puts them on the
    /// channel when the buffer fills or something flushes it — which for a terminal means
    /// a keypress goes nowhere until roughly a thousand more follow it. It looks exactly
    /// like a shell that has hung.
    /// </summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _shell.WriteAsync(data, ct);
        await _shell.FlushAsync(ct);
    }

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken ct)
    {
        _shell.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _shell.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Building the SSH client from a connection's settings. Shared by the session above and
/// by <see cref="SshProvider"/>'s probe, so what gets tested is what gets connected to —
/// the same bargain the host makes between its Test button and its monitor.
/// </summary>
public static class SshHost
{
    /// <summary>
    /// What the host key turned out to be. The probe needs it for two reasons a boolean
    /// cannot carry: to print a fingerprint worth pasting into the settings, and to tell
    /// "the key is wrong" apart from the dozen other ways a connection fails.
    /// </summary>
    public sealed class HostKeyWatch
    {
        public string Seen { get; internal set; } = "";
        public bool Rejected { get; internal set; }
    }

    public static SshClient Client(Connection connection, HostKeyWatch? watch = null)
    {
        var settings = connection.Settings;
        var host = settings.Get("host");
        var port = Math.Clamp(settings.GetInt("port", 22), 1, 65535);
        var user = settings.Get("username");
        var password = settings.Get("password");
        var keyPath = settings.Get("key_path");

        if (host.Length == 0)
            throw new InvalidOperationException("No host is set on this connection.");
        if (user.Length == 0)
            throw new InvalidOperationException("No username is set on this connection.");

        var methods = new List<AuthenticationMethod>();

        if (keyPath.Length > 0)
        {
            if (!File.Exists(keyPath))
            {
                throw new FileNotFoundException(
                    $"No key file at {keyPath} inside LabbyTwo's container. A path on the host is not " +
                    "one in here — mount it, read-only, in docker-compose.override.yml:\n" +
                    "  services:\n    labbytwo:\n      volumes:\n" +
                    "        - ~/.ssh/id_ed25519:/app/data/keys/id_ed25519:ro");
            }

            var passphrase = settings.Get("key_passphrase");
            var key = passphrase.Length > 0
                ? new PrivateKeyFile(keyPath, passphrase)
                : new PrivateKeyFile(keyPath);
            methods.Add(new PrivateKeyAuthenticationMethod(user, key));
        }

        if (password.Length > 0)
        {
            methods.Add(new PasswordAuthenticationMethod(user, password));

            // The same password, offered the other way. Plenty of NAS firmware advertises
            // keyboard-interactive and not password, and the difference is invisible from
            // here — without this, a correct password fails with "no supported
            // authentication methods", which sends people looking for the wrong fault.
            var interactive = new KeyboardInteractiveAuthenticationMethod(user);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                    prompt.Response = password;
            };
            methods.Add(interactive);
        }

        if (methods.Count == 0)
            throw new InvalidOperationException("This connection has neither a password nor a key file.");

        var info = new ConnectionInfo(host, port, user, [.. methods])
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.GetInt("timeout", 15), 3, 120)),
        };

        var client = new SshClient(info);
        var expected = Fingerprint.Normalise(settings.Get("host_fingerprint"));

        // Trust on first use, done by hand and in the open. With no fingerprint recorded
        // the key is accepted and the probe message says what it was, so it can be pasted
        // into the field; once it is there, a key that changes stops the connection dead.
        // Silently accepting a changed host key is the one thing an SSH client must not
        // do, and a dashboard that holds the password is exactly what a man in the middle
        // is after.
        client.HostKeyReceived += (_, e) =>
        {
            var seen = Fingerprint.Normalise(e.FingerPrintSHA256);
            e.CanTrust = expected.Length == 0 || string.Equals(expected, seen, StringComparison.Ordinal);

            if (watch is not null)
            {
                watch.Seen = seen;
                watch.Rejected = !e.CanTrust;
            }
        };

        return client;
    }

    /// <summary>
    /// The one failure worth spelling out, shared by the probe and the terminal so both
    /// say the same thing about the same event.
    /// </summary>
    public static string KeyChanged(HostKeyWatch watch) =>
        "The host key is not the one pinned on this connection. It is now " +
        $"{Fingerprint.Display(watch.Seen)}. That is what a rebuilt machine looks like — and also what " +
        "an interception looks like, so check which before you change the field.";

    public static class Fingerprint
    {
        /// <summary>
        /// OpenSSH prints <c>SHA256:abc…</c> with no base64 padding; SSH.NET hands back the
        /// bare base64. Accept either, and either casing of the prefix, so pasting what
        /// <c>ssh-keyscan</c> printed works.
        /// </summary>
        public static string Normalise(string? raw)
        {
            var value = (raw ?? "").Trim();
            if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
                value = value["SHA256:".Length..];
            return value.TrimEnd('=');
        }

        public static string Display(string? raw) =>
            Normalise(raw) is { Length: > 0 } value ? $"SHA256:{value}" : "";
    }
}
