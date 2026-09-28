using System.Text;

namespace LabbyTwo.Services;

public enum LogStream
{
    Stdout,
    Stderr,
}

/// <param name="Time">From Docker's timestamps=1 prefix; null when a line had none.</param>
public sealed record LogLine(DateTimeOffset? Time, string Text, LogStream Stream);

/// <summary>
/// Turns the bytes of <c>GET /containers/{id}/logs</c> into lines.
///
/// Two formats come back from the same endpoint, and which one depends on how the container
/// was created, not on anything in the request. A container with a TTY sends the raw
/// terminal output. Every other container multiplexes stdout and stderr onto one stream,
/// each chunk behind an eight-byte header: one byte for the stream, three of zero, and a
/// big-endian length. Reading the second format as the first puts binary garbage in front
/// of every line; reading the first as the second takes the first letters of a line as a
/// length and swallows the rest. So the caller says which it is, from the container's
/// inspect.
///
/// Neither headers nor lines line up with network reads — a header can arrive in two
/// pieces, a frame can end in the middle of a line, and a line in the middle of a UTF-8
/// character — so everything partial is carried over to the next <see cref="Feed"/>.
/// Bytes are kept until a newline and only then decoded, which is what keeps a split
/// multi-byte character whole.
/// </summary>
public sealed class DockerLogReader(bool tty, bool timestamps = true)
{
    /// <summary>
    /// A line longer than this is cut. A container that prints a megabyte of JSON without a
    /// newline should cost the page a long line, not a megabyte per line held in memory.
    /// </summary>
    public const int MaxLineBytes = 16 * 1024;

    private readonly byte[] _header = new byte[8];
    private int _headerFilled;
    private int _frameRemaining;
    private LogStream _frameStream = LogStream.Stdout;

    private readonly MemoryStream _stdout = new();
    private readonly MemoryStream _stderr = new();

    /// <summary>Everything completed by these bytes, in the order it arrived.</summary>
    public IReadOnlyList<LogLine> Feed(ReadOnlySpan<byte> data)
    {
        var lines = new List<LogLine>();

        if (tty)
        {
            Append(LogStream.Stdout, data, lines);
            return lines;
        }

        while (data.Length > 0)
        {
            if (_frameRemaining == 0)
            {
                var take = Math.Min(8 - _headerFilled, data.Length);
                data[..take].CopyTo(_header.AsSpan(_headerFilled));
                _headerFilled += take;
                data = data[take..];

                if (_headerFilled < 8)
                    break;

                _headerFilled = 0;
                // 0 is stdin, which logs never carry; anything unexpected is treated as
                // stdout rather than thrown, so one odd frame does not end the stream.
                _frameStream = _header[0] == 2 ? LogStream.Stderr : LogStream.Stdout;
                _frameRemaining = (_header[4] << 24) | (_header[5] << 16) | (_header[6] << 8) | _header[7];
                if (_frameRemaining < 0)
                    _frameRemaining = 0;
                continue;
            }

            var chunk = Math.Min(_frameRemaining, data.Length);
            Append(_frameStream, data[..chunk], lines);
            _frameRemaining -= chunk;
            data = data[chunk..];
        }

        return lines;
    }

    /// <summary>
    /// Whatever is left once the stream has ended — a last line with no newline, which a
    /// container that died mid-sentence leaves behind and which is usually the interesting one.
    /// </summary>
    public IReadOnlyList<LogLine> Flush()
    {
        var lines = new List<LogLine>();
        foreach (var (buffer, stream) in new[] { (_stdout, LogStream.Stdout), (_stderr, LogStream.Stderr) })
        {
            if (buffer.Length > 0)
            {
                lines.Add(Line(buffer.ToArray(), stream));
                buffer.SetLength(0);
            }
        }
        return lines;
    }

    private void Append(LogStream stream, ReadOnlySpan<byte> data, List<LogLine> lines)
    {
        var buffer = stream == LogStream.Stderr ? _stderr : _stdout;
        while (data.Length > 0)
        {
            var newline = data.IndexOf((byte)'\n');
            var piece = newline >= 0 ? data[..newline] : data;

            var room = MaxLineBytes - (int)buffer.Length;
            if (room > 0)
                buffer.Write(piece[..Math.Min(room, piece.Length)]);

            if (newline < 0)
                break;

            lines.Add(Line(buffer.ToArray(), stream));
            buffer.SetLength(0);
            data = data[(newline + 1)..];
        }
    }

    private LogLine Line(byte[] bytes, LogStream stream)
    {
        var text = Encoding.UTF8.GetString(bytes).TrimEnd('\r');
        if (!timestamps)
            return new LogLine(null, text, stream);

        // timestamps=1 puts an RFC 3339 time and one space in front of every line.
        var space = text.IndexOf(' ');
        if (space > 0 && DockerTime.Parse(text[..space]) is { } time)
            return new LogLine(time, text[(space + 1)..], stream);
        return new LogLine(null, text, stream);
    }
}

/// <summary>
/// The lines a logs panel holds, capped. Following a chatty container for an afternoon
/// would otherwise grow without end, on the server, per open panel.
/// </summary>
public sealed class LogBuffer(int capacity = LogBuffer.DefaultCapacity)
{
    public const int DefaultCapacity = 5000;

    private readonly List<LogLine> _lines = [];
    private readonly object _sync = new();

    /// <summary>How many lines have been dropped off the front to stay under the cap.</summary>
    public long Dropped { get; private set; }

    public int Count
    {
        get
        {
            lock (_sync)
                return _lines.Count;
        }
    }

    public void Add(IEnumerable<LogLine> lines)
    {
        lock (_sync)
        {
            _lines.AddRange(lines);
            var over = _lines.Count - capacity;
            if (over > 0)
            {
                _lines.RemoveRange(0, over);
                Dropped += over;
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _lines.Clear();
            Dropped = 0;
        }
    }

    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_sync)
            return [.. _lines];
    }
}

/// <summary>Reading a container's logs, once or for as long as somebody is watching.</summary>
public static class DockerLogs
{
    public static string Path(string id, int tail, bool follow) =>
        $"/containers/{Uri.EscapeDataString(id)}/logs?stdout=1&stderr=1&timestamps=1&tail={Math.Max(0, tail)}" +
        (follow ? "&follow=1" : "");

    /// <summary>The last <paramref name="tail"/> lines.</summary>
    public static async Task<IReadOnlyList<LogLine>> TailAsync(
        string endpoint, TimeSpan timeout, string id, bool tty, int tail, CancellationToken ct)
    {
        await using var stream = await DockerSocket.OpenStreamAsync(endpoint, timeout, Path(id, tail, follow: false), ct);
        var reader = new DockerLogReader(tty);
        var lines = new List<LogLine>();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            lines.AddRange(reader.Feed(buffer.AsSpan(0, read)));
        lines.AddRange(reader.Flush());
        return lines;
    }

    /// <summary>
    /// The last <paramref name="tail"/> lines, then every new one until cancelled or the
    /// container stops. <paramref name="onLines"/> is called from the reading thread, once
    /// per network read, so it should only hand the lines off.
    /// </summary>
    public static async Task FollowAsync(
        string endpoint, TimeSpan timeout, string id, bool tty, int tail,
        Action<IReadOnlyList<LogLine>> onLines, CancellationToken ct)
    {
        await using var stream = await DockerSocket.OpenStreamAsync(endpoint, timeout, Path(id, tail, follow: true), ct);
        var reader = new DockerLogReader(tty);
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            var lines = reader.Feed(buffer.AsSpan(0, read));
            if (lines.Count > 0)
                onLines(lines);
        }

        var rest = reader.Flush();
        if (rest.Count > 0)
            onLines(rest);
    }
}
