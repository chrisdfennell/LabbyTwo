using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Services;

namespace LabbyTwo.TerminalPlugin;

/// <summary>
/// A shell inside a container, over the same socket the Docker provider already reads.
///
/// This is the one part of the plugin that cannot use an <see cref="HttpClient"/>. Docker
/// answers <c>/exec/{id}/start</c> with <c>101 UPGRADED</c> and then hands the connection
/// over as a raw duplex stream — there is no more HTTP on it, in either direction.
/// <see cref="HttpClient"/> has nowhere to give you that socket back, so the request is
/// written by hand and the response headers are read off the stream until the blank line,
/// after which the same stream is the terminal.
/// </summary>
public static class DockerExec
{
    /// <summary>
    /// Matches the version <see cref="DockerSocket"/> pins. Old enough for anything still
    /// running, and exec has been in the API since long before it.
    /// </summary>
    private const string ApiVersion = "/v1.41";

    public static async Task<DockerExecSession> OpenAsync(
        Connection docker, string container, string shell, int columns, int rows, CancellationToken ct)
    {
        var endpoint = docker.Settings.Get("endpoint", DockerSocket.DefaultEndpoint);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(docker.Settings.GetInt("timeout", 10), 1, 120));

        var command = CommandLine.Split(shell);
        if (command.Count == 0)
            throw new InvalidOperationException("The shell command is empty.");

        var create = JsonSerializer.Serialize(new
        {
            AttachStdin = true,
            AttachStdout = true,
            AttachStderr = true,
            Tty = true,
            Cmd = command,

            // Without this the shell inside believes it is on a dumb terminal, and every
            // program that draws anything falls back to its plainest output — which is
            // the opposite of why anyone opens a terminal in a dashboard.
            Env = new[] { "TERM=xterm-256color" },
        });

        var response = await DockerSocket.PostAsync(
            endpoint, timeout, $"/containers/{Uri.EscapeDataString(container)}/exec", create, ct);

        using var document = JsonDocument.Parse(response);
        if (!document.RootElement.TryGetProperty("Id", out var id) || id.GetString() is not { Length: > 0 } execId)
            throw new InvalidOperationException("Docker created the exec but did not say what its id was.");

        var stream = await ConnectAsync(endpoint, timeout, ct);
        try
        {
            await UpgradeAsync(stream, execId, ct);

            // The window size can only be set once the exec is running: before that,
            // Docker has no tty to resize and answers 409.
            await ResizeAsync(endpoint, timeout, execId, columns, rows, ct);

            return new DockerExecSession(stream, endpoint, timeout, execId, $"{container} ({docker.Name})");
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    internal static async Task ResizeAsync(
        string endpoint, TimeSpan timeout, string execId, int columns, int rows, CancellationToken ct)
    {
        // Docker answers 409 for a resize that arrives before the process has a tty, or
        // after it has exited. Neither is worth showing anybody: the terminal is either
        // about to work or already gone.
        try
        {
            await DockerSocket.PostAsync(endpoint, timeout,
                $"/exec/{execId}/resize?h={rows}&w={columns}", null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The request Docker upgrades. Written by hand for the reason at the top of the file.
    /// </summary>
    private static async Task UpgradeAsync(Stream stream, string execId, CancellationToken ct)
    {
        var body = """{"Detach":false,"Tty":true}"""u8.ToArray();
        var request =
            $"POST {ApiVersion}/exec/{execId}/start HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Content-Type: application/json\r\n" +
            "Connection: Upgrade\r\n" +
            "Upgrade: tcp\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);

        var headers = await ReadHeadersAsync(stream, ct);
        var status = headers.Split('\r')[0];

        // 101 is the upgrade. 200 is Docker streaming the body instead, which happens on
        // older daemons and is the same raw stream either way once Tty is set.
        if (!status.Contains(" 101", StringComparison.Ordinal) && !status.Contains(" 200", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Docker would not start the shell: {status.Trim()}. " +
                "The usual cause is that the container has no shell at the path being asked for — " +
                "a distroless or scratch image genuinely has none.");
        }
    }

    /// <summary>
    /// A byte at a time to the blank line, so not one byte of the terminal stream is read
    /// into a buffer this method is about to throw away.
    /// </summary>
    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken ct)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];
        var matched = 0;

        while (matched < 4)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                throw new IOException("Docker closed the connection before answering.");

            var character = (char)buffer[0];
            builder.Append(character);

            matched = (matched, character) switch
            {
                (0, '\r') or (2, '\r') => matched + 1,
                (1, '\n') or (3, '\n') => matched + 1,
                (_, '\r') => 1,
                _ => 0,
            };

            if (builder.Length > 8 * 1024)
                throw new IOException("Docker sent headers that never ended.");
        }

        return builder.ToString();
    }

    /// <summary>
    /// The three transports the Docker provider's endpoint field accepts, as a duplex
    /// stream rather than an <see cref="HttpClient"/>.
    /// </summary>
    private static async Task<Stream> ConnectAsync(string endpoint, TimeSpan timeout, CancellationToken ct)
    {
        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);

        if (endpoint.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            var authority = endpoint["tcp://".Length..].TrimEnd('/');
            var colon = authority.LastIndexOf(':');
            var host = colon > 0 ? authority[..colon] : authority;
            var port = colon > 0 && int.TryParse(authority[(colon + 1)..], out var parsed) ? parsed : 2375;

            var tcp = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await tcp.ConnectAsync(host, port, linked.Token);
            return new NetworkStream(tcp, ownsSocket: true);
        }

        if (endpoint.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase) ||
            endpoint.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            var name = endpoint
                .Replace("npipe://./pipe/", "", StringComparison.OrdinalIgnoreCase)
                .Replace(@"\\.\pipe\", "", StringComparison.OrdinalIgnoreCase);

            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(linked.Token);
            return pipe;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), linked.Token);
        return new NetworkStream(socket, ownsSocket: true);
    }
}

/// <summary>
/// The upgraded connection, once Docker has handed it over. With <c>Tty</c> set there is
/// no stream multiplexing header, so what arrives is exactly what the shell wrote.
/// </summary>
public sealed class DockerExecSession(
    Stream stream, string endpoint, TimeSpan timeout, string execId, string describe) : ITerminalSession
{
    public string Describe => describe;

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) =>
        stream.ReadAsync(buffer, ct);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }

    public async ValueTask ResizeAsync(int columns, int rows, CancellationToken ct) =>
        await DockerExec.ResizeAsync(endpoint, timeout, execId, columns, rows, ct);

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}

/// <summary>
/// Splitting a shell setting into argv. Docker takes a list rather than a line, and the
/// default shell command has quotes in it that have to survive the trip.
/// </summary>
public static class CommandLine
{
    public static IReadOnlyList<string> Split(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var started = false;

        foreach (var character in command)
        {
            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                else
                    current.Append(character);
                continue;
            }

            switch (character)
            {
                case '\'' or '"':
                    quote = character;
                    started = true;
                    break;

                case ' ' or '\t':
                    if (started)
                    {
                        parts.Add(current.ToString());
                        current.Clear();
                        started = false;
                    }
                    break;

                default:
                    current.Append(character);
                    started = true;
                    break;
            }
        }

        if (started)
            parts.Add(current.ToString());

        return parts;
    }
}
