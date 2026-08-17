using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.MinecraftPlugin;

/// <summary>
/// A Minecraft server, asked the same question the game's own server list asks: who is on,
/// what version is it, and what does the message of the day say.
///
/// No HTTP, no API key and no dependency — the Server List Ping is a handful of
/// length-prefixed packets on the game port, which is the whole appeal. It is also the
/// cheapest useful thing in this set: <c>players_online</c> charted over a week tells you
/// more about whether the server is worth keeping running than any amount of CPU graphing.
/// </summary>
public sealed class MinecraftProvider : IConnectionProvider
{
    public const string ProviderType = "minecraft";

    public string Type => ProviderType;
    public string DisplayName => "Minecraft server";
    public string Icon => "🟩";
    public string Category => "General";

    public string Description =>
        "Players online, the version and the MOTD, from a Java Edition server. Uses the same query "
        + "the game's server list does, so nothing needs installing on the server.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("host", "Address", FieldKind.Text, "192.168.86.57", Required: true),
        new("port", "Port", FieldKind.Number, Default: "25565"),
        new("timeout", "Timeout (seconds)", FieldKind.Number, Default: "5") { Advanced = true },
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("players_online", "Players online"),
        new("players_max", "Player slots"),
    ];

    /// <summary>
    /// Nothing. "Nobody is playing" is not a fault, and "the server is full" is a
    /// celebration on most home servers rather than an alert. The provider going Down
    /// already covers the case that matters, which is the server having fallen over.
    /// </summary>
    public IReadOnlyList<SuggestedRule> SuggestedRules => [];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var host = connection.Settings.Get("host");
        if (host.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No address configured.");

        var port = connection.Settings.GetInt("port", 25565);
        if (port is <= 0 or > 65535)
            port = 25565;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(connection.Settings.GetInt("timeout", 5), 1, 60)));

            var json = await StatusAsync(host, port, cts.Token);
            stopwatch.Stop();

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var online = 0d;
            var max = 0d;
            if (root.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Object)
            {
                online = players.TryGetProperty("online", out var o) && o.TryGetDouble(out var on) ? on : 0;
                max = players.TryGetProperty("max", out var m) && m.TryGetDouble(out var mx) ? mx : 0;
            }

            var version = root.TryGetProperty("version", out var v)
                          && v.ValueKind == JsonValueKind.Object
                          && v.TryGetProperty("name", out var vn)
                ? vn.GetString() ?? ""
                : "";

            var details = new Dictionary<string, string>();
            if (version.Length > 0)
                details["Version"] = version;

            if (Motd(root) is { Length: > 0 } motd)
                details["MOTD"] = motd;

            // Who is on, when the server sends the sample. Vanilla sends up to twelve names
            // and many servers turn it off, so this is a bonus rather than something to
            // rely on.
            if (players.ValueKind == JsonValueKind.Object
                && players.TryGetProperty("sample", out var sample)
                && sample.ValueKind == JsonValueKind.Array
                && sample.GetArrayLength() > 0)
            {
                var names = sample.EnumerateArray()
                    .Select(p => p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                    .Where(n => n.Length > 0)
                    .ToList();

                if (names.Count > 0)
                    details["Playing"] = string.Join(", ", names);
            }

            return ProbeResult.Up(stopwatch.Elapsed,
                online == 0 ? "Up, nobody playing" : $"{online:0} of {max:0} playing",
                new Dictionary<string, double>
                {
                    ["players_online"] = online,
                    ["players_max"] = max,
                    ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
                },
                details);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, "The server did not answer in time.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// The Server List Ping, as of protocol 47 and every version since. Two packets out —
    /// a handshake saying "I want status", then an empty status request — and one JSON
    /// document back.
    ///
    /// Everything is length-prefixed with a variable-length integer, which is the only
    /// awkward part of the protocol and the reason this is written out rather than taken
    /// from a library.
    /// </summary>
    private static async Task<string> StatusAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        await using var stream = client.GetStream();

        var handshake = new List<byte> { 0x00 };            // packet id: handshake
        WriteVarInt(handshake, -1);                          // protocol version; -1 means "just asking"
        WriteString(handshake, host);
        handshake.Add((byte)(port >> 8));
        handshake.Add((byte)(port & 0xFF));
        WriteVarInt(handshake, 1);                           // next state: status

        await SendAsync(stream, handshake, ct);
        await SendAsync(stream, [0x00], ct);                 // status request, no payload

        var length = await ReadVarIntAsync(stream, ct);
        if (length is <= 0 or > 2_000_000)
            throw new InvalidOperationException("The server answered, but not with a status packet.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);

        // Inside the packet: a varint packet id, then the JSON as a length-prefixed string.
        var offset = 0;
        _ = ReadVarInt(payload, ref offset);                 // packet id, always 0x00 here
        var jsonLength = ReadVarInt(payload, ref offset);

        if (jsonLength < 0 || offset + jsonLength > payload.Length)
            throw new InvalidOperationException("The status packet was shorter than it claimed to be.");

        return Encoding.UTF8.GetString(payload, offset, jsonLength);
    }

    private static async Task SendAsync(NetworkStream stream, IReadOnlyList<byte> body, CancellationToken ct)
    {
        var framed = new List<byte>();
        WriteVarInt(framed, body.Count);
        framed.AddRange(body);
        await stream.WriteAsync(framed.ToArray(), ct);
    }

    private static void WriteString(List<byte> buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(buffer, bytes.Length);
        buffer.AddRange(bytes);
    }

    /// <summary>Seven bits per byte, low group first, top bit set while more follow.</summary>
    private static void WriteVarInt(List<byte> buffer, int value)
    {
        var unsigned = unchecked((uint)value);
        while (true)
        {
            if ((unsigned & ~0x7Fu) == 0)
            {
                buffer.Add((byte)unsigned);
                return;
            }

            buffer.Add((byte)((unsigned & 0x7F) | 0x80));
            unsigned >>= 7;
        }
    }

    private static async Task<int> ReadVarIntAsync(NetworkStream stream, CancellationToken ct)
    {
        var result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var single = new byte[1];
            await stream.ReadExactlyAsync(single, ct);

            result |= (single[0] & 0x7F) << shift;
            if ((single[0] & 0x80) == 0)
                return result;
        }

        // Five bytes is the most a 32-bit varint can be. Anything longer is a stream out of
        // step, and reading on would hang rather than fail.
        throw new InvalidOperationException("That is not a Minecraft server — the reply was not valid framing.");
    }

    private static int ReadVarInt(byte[] buffer, ref int offset)
    {
        var result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (offset >= buffer.Length)
                throw new InvalidOperationException("The status packet ended mid-number.");

            var current = buffer[offset++];
            result |= (current & 0x7F) << shift;
            if ((current & 0x80) == 0)
                return result;
        }

        throw new InvalidOperationException("The status packet contained a malformed number.");
    }

    /// <summary>
    /// The MOTD, which is either a plain string or a chat component tree depending on the
    /// server, with the text scattered across nested "extra" arrays. Flattened to something
    /// readable, and the colour codes taken out.
    /// </summary>
    internal static string Motd(JsonElement root)
    {
        if (!root.TryGetProperty("description", out var description))
            return "";

        var text = Flatten(description).Trim();

        // §a and friends are the section-sign colour codes. They are markup, and a tile is
        // not going to render them.
        var clean = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '§' && index + 1 < text.Length)
            {
                index++;
                continue;
            }

            clean.Append(text[index] == '\n' ? ' ' : text[index]);
        }

        return clean.ToString().Trim();
    }

    private static string Flatten(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                return node.GetString() ?? "";

            case JsonValueKind.Array:
                return string.Concat(node.EnumerateArray().Select(Flatten));

            case JsonValueKind.Object:
                var text = node.TryGetProperty("text", out var own) ? Flatten(own) : "";
                if (node.TryGetProperty("extra", out var extra))
                    text += Flatten(extra);
                return text;

            default:
                return "";
        }
    }
}
