using System.Buffers.Binary;

namespace LabbyTwo.Core;

/// <summary>
/// Checks an uploaded background picture and takes the personal data out of it, without
/// decoding a single pixel.
///
/// <para>
/// What a file is comes from its first bytes, never from its name or the browser's
/// content type — both are whatever the sender says. Only JPEG, PNG and WebP are accepted:
/// every browser draws them, and none of them can carry script. SVG is refused outright for
/// exactly that reason (an SVG is a document, and a document can run code), and GIF because
/// an animated background on a wall is a battery and a distraction rather than a picture.
/// </para>
///
/// <para>
/// The dimensions are read from the header so a picture that would take a wall tablet
/// hundreds of megabytes to decode is turned away before anything tries. That is also why
/// this is header arithmetic rather than an imaging library: there is nothing to decode, so
/// there is no decoder for a hostile file to attack, and no native dependency in the image.
/// </para>
///
/// <para>
/// Metadata is removed because a phone photo carries where it was taken. Camera EXIF, XMP,
/// IPTC and comments go; what changes how the picture <em>looks</em> stays — the colour
/// profile, Adobe's colour-transform marker, and the EXIF orientation, rewritten as a
/// minimal block of its own so a portrait photo is not drawn on its side.
/// </para>
/// </summary>
public static class BackdropImageFile
{
    /// <summary>
    /// Generous for a background — a 4K photo is two or three megabytes — and small enough
    /// that an upload box is not a way to fill the data volume.
    /// </summary>
    public const long MaxBytes = 10 * 1024 * 1024;

    /// <summary>No side longer than this: browsers start refusing, or tiling badly, beyond it.</summary>
    public const int MaxSide = 12_000;

    /// <summary>
    /// 40 megapixels. A decoded picture costs four bytes a pixel whatever the file size, so a
    /// well-compressed 100-megapixel JPEG is a 400 MB background on a tablet. A copy sized
    /// for the screen looks identical behind the cards.
    /// </summary>
    public const long MaxPixels = 40_000_000;

    /// <summary>What the header says. The extension and content type are this class's, not the uploader's.</summary>
    public sealed record Info(string Format, string Extension, string ContentType, int Width, int Height);

    /// <summary>The three formats, by the extension the stored file is given.</summary>
    public static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [".jpg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
    };

    /// <summary>
    /// What the file is and how big it is, or an <see cref="InvalidDataException"/> saying in
    /// plain words why it cannot be a background.
    /// </summary>
    public static Info Inspect(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            throw new InvalidDataException("That file was empty.");
        if (data.Length > MaxBytes)
            throw new InvalidDataException($"That is {data.Length / 1024 / 1024} MB — the limit is {MaxBytes / 1024 / 1024} MB.");

        var info = Sniff(data) switch
        {
            "jpeg" => Jpeg(data),
            "png" => Png(data),
            "webp" => WebP(data),
            "svg" => throw new InvalidDataException("SVG pictures are not accepted — an SVG can contain script. Save it as a PNG instead."),
            "gif" => throw new InvalidDataException("GIFs are not accepted. Use a JPEG, PNG or WebP."),
            _ => throw new InvalidDataException("That is not a JPEG, PNG or WebP picture."),
        };

        if (info.Width <= 0 || info.Height <= 0)
            throw new InvalidDataException("That picture says it has no size, which means the file is damaged.");
        if (info.Width > MaxSide || info.Height > MaxSide || (long)info.Width * info.Height > MaxPixels)
            throw new InvalidDataException(
                $"That picture is {info.Width} × {info.Height}. The limit is {MaxPixels / 1_000_000} megapixels and " +
                $"{MaxSide} pixels a side — a copy the size of your screen looks the same behind the cards, " +
                "and is far kinder to a wall tablet.");

        return info;
    }

    /// <summary>The format from the magic bytes, or null. Recognises the two refused formats so it can say why.</summary>
    public static string? Sniff(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "jpeg";
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
            return "png";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
            return "webp";
        if (data.StartsWith("GIF8"u8))
            return "gif";

        // SVG is text; look for its root element near the top, past any BOM, XML prolog or comment.
        var head = System.Text.Encoding.UTF8.GetString(data[..Math.Min(data.Length, 1024)]);
        if (head.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            return "svg";

        return null;
    }

    /// <summary>
    /// A copy of the file without its metadata. The picture data itself is copied byte for
    /// byte; only the segments around it are dropped.
    /// </summary>
    public static byte[] StripMetadata(byte[] data, Info info) => info.Format switch
    {
        "jpeg" => StripJpeg(data),
        "png" => StripPng(data),
        "webp" => StripWebP(data),
        _ => throw new InvalidDataException("Unknown format."),
    };

    // ---- JPEG ---------------------------------------------------------------------------

    private static Info Jpeg(ReadOnlySpan<byte> data)
    {
        foreach (var (marker, start, length) in JpegSegments(data))
        {
            // Start-of-frame: C0–CF, except C4 (Huffman tables), C8 (reserved) and CC (arithmetic coding).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (length < 7)
                    break;
                var height = BinaryPrimitives.ReadUInt16BigEndian(data[(start + 5)..]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(data[(start + 7)..]);
                return new Info("jpeg", ".jpg", ContentTypes[".jpg"], width, height);
            }
        }

        throw new InvalidDataException("That JPEG is damaged — it has no picture size in it.");
    }

    /// <summary>
    /// The marker segments before the picture data, as (marker, offset of the 0xFF, bytes
    /// including the marker). Stops at start-of-scan, which is yielded with a length of 0:
    /// everything from there on is compressed picture and is copied as it is.
    /// </summary>
    private static List<(byte Marker, int Start, int Length)> JpegSegments(ReadOnlySpan<byte> data)
    {
        var segments = new List<(byte, int, int)>();
        var i = 2;
        while (i + 1 < data.Length)
        {
            if (data[i] != 0xFF)
                throw new InvalidDataException("That JPEG is damaged.");

            // Any number of 0xFF fill bytes may come before a marker; the segment starts at the last.
            while (i < data.Length && data[i] == 0xFF)
                i++;
            if (i >= data.Length)
                break;
            var at = i - 1;
            var marker = data[i];
            i++;

            if (marker == 0xDA)
            {
                segments.Add((marker, at, 0));
                return segments;
            }

            // Markers with no length: restart markers and TEM.
            if (marker is (>= 0xD0 and <= 0xD7) or 0x01)
                continue;
            if (marker == 0xD9)
                break;

            if (i + 2 > data.Length)
                break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
            if (length < 2 || i + length > data.Length)
                throw new InvalidDataException("That JPEG is damaged.");

            segments.Add((marker, at, i + length - at));
            i += length;
        }

        throw new InvalidDataException("That JPEG is incomplete.");
    }

    private static byte[] StripJpeg(byte[] data)
    {
        var segments = JpegSegments(data);
        using var output = new MemoryStream(data.Length);
        output.Write([0xFF, 0xD8]);

        var orientation = 1;
        var wroteOrientation = false;

        foreach (var (marker, start, length) in segments)
        {
            if (marker == 0xDA)
            {
                if (!wroteOrientation)
                    WriteOrientation(output, orientation);
                output.Write(data, start, data.Length - start);
                break;
            }

            var keep = marker switch
            {
                0xE0 => true,   // JFIF — how to read the rest
                0xE2 => true,   // ICC colour profile, split over APP2 segments
                0xEE => true,   // Adobe — says whether the colours are transformed
                0xE1 => false,  // EXIF and XMP: camera, time, and where it was taken
                >= 0xE3 and <= 0xEF => false, // other applications' notes, IPTC among them
                0xFE => false,  // comments
                _ => true,      // tables, frame header: the picture itself
            };

            if (marker == 0xE1 && ExifOrientation(data.AsSpan(start + 4, length - 4)) is { } found)
                orientation = found;

            if (!keep)
                continue;

            // The orientation goes straight after JFIF (which must come first) or at the
            // front, before the tables — where a decoder expects application segments.
            if (!wroteOrientation && marker != 0xE0)
            {
                WriteOrientation(output, orientation);
                wroteOrientation = true;
            }

            output.Write(data, start, length);
        }

        return output.ToArray();

        void WriteOrientation(MemoryStream stream, int value)
        {
            wroteOrientation = true;
            if (value is < 2 or > 8)
                return;
            stream.Write(MinimalExif(value));
        }
    }

    /// <summary>
    /// An APP1 segment holding nothing but the orientation: a big-endian TIFF header and one
    /// IFD with one entry. 34 bytes, no camera, no time, no place.
    /// </summary>
    public static byte[] MinimalExif(int orientation) =>
    [
        0xFF, 0xE1, 0x00, 0x22,
        (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
        (byte)'M', (byte)'M', 0x00, 0x2A, 0, 0, 0, 8,
        0x00, 0x01,
        0x01, 0x12, 0x00, 0x03, 0, 0, 0, 1, 0x00, (byte)orientation, 0, 0,
        0, 0, 0, 0,
    ];

    /// <summary>The orientation tag from an EXIF APP1 payload, or null when there is not one (or it is XMP).</summary>
    public static int? ExifOrientation(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 14 || !payload.StartsWith("Exif\0\0"u8))
            return null;

        var tiff = payload[6..];
        var little = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
        if (!little && !(tiff[0] == (byte)'M' && tiff[1] == (byte)'M'))
            return null;

        ushort U16(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
        uint U32(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);

        var ifd = U32(tiff[4..]);
        if (ifd + 2 > tiff.Length)
            return null;
        var count = U16(tiff[(int)ifd..]);
        for (var n = 0; n < count; n++)
        {
            var entry = (int)ifd + 2 + n * 12;
            if (entry + 12 > tiff.Length)
                return null;
            if (U16(tiff[entry..]) == 0x0112 && U16(tiff[(entry + 2)..]) == 3)
            {
                var value = U16(tiff[(entry + 8)..]);
                return value is >= 1 and <= 8 ? value : null;
            }
        }
        return null;
    }

    // ---- PNG ----------------------------------------------------------------------------

    /// <summary>Text, EXIF and the modification time. Everything else describes the picture.</summary>
    private static readonly HashSet<string> PngDropped = ["tEXt", "zTXt", "iTXt", "eXIf", "tIME"];

    private static Info Png(ReadOnlySpan<byte> data)
    {
        var chunks = PngChunks(data);
        if (chunks.Count == 0 || chunks[0].Type != "IHDR" || chunks[0].Length < 8 + 4 + 13)
            throw new InvalidDataException("That PNG is damaged — it does not start with its header.");

        var width = BinaryPrimitives.ReadUInt32BigEndian(data[(chunks[0].Start + 8)..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(data[(chunks[0].Start + 12)..]);
        return new Info("png", ".png", ContentTypes[".png"], (int)Math.Min(width, int.MaxValue), (int)Math.Min(height, int.MaxValue));
    }

    /// <summary>Every chunk as (type, offset of its length field, bytes including length, type and CRC), up to IEND.</summary>
    private static List<(string Type, int Start, int Length)> PngChunks(ReadOnlySpan<byte> data)
    {
        var chunks = new List<(string, int, int)>();
        var i = 8;
        while (i + 12 <= data.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
            if (size > int.MaxValue - 12 || i + 12 + (long)size > data.Length)
                throw new InvalidDataException("That PNG is damaged.");

            var type = System.Text.Encoding.ASCII.GetString(data.Slice(i + 4, 4));
            var total = 12 + (int)size;
            chunks.Add((type, i, total));
            i += total;

            if (type == "IEND")
                return chunks;
        }

        throw new InvalidDataException("That PNG is incomplete.");
    }

    private static byte[] StripPng(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 8);
        foreach (var (type, start, length) in PngChunks(data))
        {
            if (!PngDropped.Contains(type))
                output.Write(data, start, length);
        }
        return output.ToArray();
    }

    // ---- WebP ---------------------------------------------------------------------------

    private static Info WebP(ReadOnlySpan<byte> data)
    {
        var chunks = WebPChunks(data);
        if (chunks.Count == 0)
            throw new InvalidDataException("That WebP is damaged.");

        var (type, start, _) = chunks[0];
        var body = data[(start + 8)..];
        int width, height;
        switch (type)
        {
            case "VP8 " when body.Length >= 10 && body[3] == 0x9D && body[4] == 0x01 && body[5] == 0x2A:
                width = BinaryPrimitives.ReadUInt16LittleEndian(body[6..]) & 0x3FFF;
                height = BinaryPrimitives.ReadUInt16LittleEndian(body[8..]) & 0x3FFF;
                break;
            case "VP8L" when body.Length >= 5 && body[0] == 0x2F:
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(body[1..]);
                width = (int)(bits & 0x3FFF) + 1;
                height = (int)((bits >> 14) & 0x3FFF) + 1;
                break;
            case "VP8X" when body.Length >= 10:
                width = (body[4] | body[5] << 8 | body[6] << 16) + 1;
                height = (body[7] | body[8] << 8 | body[9] << 16) + 1;
                break;
            default:
                throw new InvalidDataException("That WebP is damaged — it has no picture size in it.");
        }

        return new Info("webp", ".webp", ContentTypes[".webp"], width, height);
    }

    /// <summary>Every chunk as (FourCC, offset of the FourCC, bytes including header and padding).</summary>
    private static List<(string Type, int Start, int Length)> WebPChunks(ReadOnlySpan<byte> data)
    {
        var riff = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var end = (int)Math.Min(data.Length, 8L + riff);
        var chunks = new List<(string, int, int)>();
        var i = 12;
        while (i + 8 <= end)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
            var padded = 8L + size + (size & 1);
            if (i + 8L + size > end)
                throw new InvalidDataException("That WebP is damaged.");

            chunks.Add((System.Text.Encoding.ASCII.GetString(data.Slice(i, 4)), i, (int)Math.Min(padded, end - i)));
            i += (int)Math.Min(padded, end - i);
        }
        return chunks;
    }

    private static byte[] StripWebP(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 12);
        foreach (var (type, start, length) in WebPChunks(data))
        {
            if (type is "EXIF" or "XMP ")
                continue;

            var chunk = data.AsSpan(start, length).ToArray();
            // VP8X announces which optional chunks follow; it must not promise ones that are gone.
            if (type == "VP8X" && chunk.Length > 8)
                chunk[8] &= unchecked((byte)~(0x08 | 0x04));
            output.Write(chunk);
        }

        var result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
        return result;
    }
}
