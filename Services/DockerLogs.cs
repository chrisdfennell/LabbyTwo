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
/// A <see cref="DockerLogReader"/> that works out for itself whether the stream is framed,
/// so a search over forty containers does not need forty inspects first just to learn which
/// ones have a TTY.
///
/// It can tell because every request here asks for <c>timestamps=1</c>. A framed stream then
/// starts with a stream byte of 0, 1 or 2 and three zeros; a TTY stream starts with the
/// first digit of a year. Nothing a container prints can make the one look like the other,
/// since Docker writes the timestamp in front of it. Until eight bytes have arrived the
/// bytes are held; a log shorter than that is decided at <see cref="Flush"/>.
/// </summary>
public sealed class SniffingLogReader
{
    private readonly byte[] _start = new byte[8];
    private int _startFilled;
    private DockerLogReader? _reader;

    /// <summary>What was decided, or null while fewer than eight bytes have arrived.</summary>
    public bool? Tty { get; private set; }

    /// <summary>Whether the first bytes of a <c>timestamps=1</c> log are a frame header.</summary>
    public static bool LooksFramed(ReadOnlySpan<byte> start) =>
        start.Length >= 4 && start[0] <= 2 && start[1] == 0 && start[2] == 0 && start[3] == 0;

    public IReadOnlyList<LogLine> Feed(ReadOnlySpan<byte> data)
    {
        if (_reader is not null)
            return _reader.Feed(data);

        var take = Math.Min(8 - _startFilled, data.Length);
        data[..take].CopyTo(_start.AsSpan(_startFilled));
        _startFilled += take;
        if (_startFilled < 8)
            return [];

        Decide();
        var lines = new List<LogLine>(_reader!.Feed(_start));
        lines.AddRange(_reader.Feed(data[take..]));
        return lines;
    }

    public IReadOnlyList<LogLine> Flush()
    {
        if (_reader is not null)
            return _reader.Flush();
        if (_startFilled == 0)
            return [];

        Decide();
        var lines = new List<LogLine>(_reader!.Feed(_start.AsSpan(0, _startFilled)));
        lines.AddRange(_reader.Flush());
        return lines;
    }

    private void Decide()
    {
        var tty = !LooksFramed(_start.AsSpan(0, _startFilled));
        Tty = tty;
        _reader = new DockerLogReader(tty);
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

    /// <summary>
    /// The logs between two times, for a search or for "the log around this line". Docker
    /// takes <c>since</c> and <c>until</c> as Unix seconds with a fraction, and applies
    /// <c>tail</c> <em>before</em> them — it takes the last N lines of the whole log and then
    /// drops those outside the window — so a tail is only passed when the window ends now.
    /// For a window in the past the caller reads forward from <c>since</c> and stops itself.
    /// </summary>
    /// <param name="tail">The newest this many lines, or null to read the whole window.</param>
    public static string WindowPath(
        string id, DateTimeOffset since, DateTimeOffset? until, int? tail, bool stdout = true, bool stderr = true) =>
        $"/containers/{Uri.EscapeDataString(id)}/logs?stdout={(stdout ? 1 : 0)}&stderr={(stderr ? 1 : 0)}&timestamps=1" +
        $"&since={UnixTime(since)}" +
        (until is { } end ? $"&until={UnixTime(end)}" : "") +
        (tail is { } lines ? $"&tail={Math.Max(0, lines)}" : "");

    /// <summary>Seconds and nanoseconds, the way Docker's own client writes a time.</summary>
    public static string UnixTime(DateTimeOffset at)
    {
        var ticks = at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var seconds = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out var rest);
        if (rest < 0)
        {
            seconds--;
            rest += TimeSpan.TicksPerSecond;
        }
        return FormattableString.Invariant($"{seconds}.{rest * 100:D9}");
    }

    /// <summary>
    /// The lines between two times, oldest first, at most <paramref name="maxLines"/> of
    /// them. The panel's "the log at that time" view: a few minutes either side of a line a
    /// search found.
    /// </summary>
    public static async Task<IReadOnlyList<LogLine>> WindowAsync(
        string endpoint, TimeSpan timeout, string id, DateTimeOffset since, DateTimeOffset until, int maxLines,
        CancellationToken ct)
    {
        await using var stream = await DockerSocket.OpenStreamAsync(
            endpoint, timeout, WindowPath(id, since, until, tail: null), ct);
        var reader = new SniffingLogReader();
        var lines = new List<LogLine>();
        var buffer = new byte[16 * 1024];
        int read;
        while (lines.Count < maxLines && (read = await stream.ReadAsync(buffer, ct)) > 0)
            lines.AddRange(reader.Feed(buffer.AsSpan(0, read)));
        if (lines.Count < maxLines)
            lines.AddRange(reader.Flush());
        return lines.Count > maxLines ? lines.GetRange(0, maxLines) : lines;
    }

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
