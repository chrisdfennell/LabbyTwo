using System.Buffers.Binary;
using System.Text;

namespace LabbyTwo.Tests;

/// <summary>
/// The smallest real-shaped JPEG, PNG and WebP files, built byte by byte, each carrying the
/// metadata a phone photo would — a "where it was taken" marker the tests look for after
/// stripping. Only the headers matter to the code under test; no pixel is ever decoded.
/// </summary>
internal static class BackdropTestImages
{
    /// <summary>Stands in for GPS coordinates and the camera's serial number.</summary>
    public const string Secret = "GPS-51.5007N-0.1246W-SERIAL-12345";

    public static byte[] Png(int width = 64, int height = 48, bool withText = true, bool complete = true)
    {
        using var s = new MemoryStream();
        s.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // truecolour
        Chunk(s, "IHDR", ihdr);
        if (withText)
        {
            Chunk(s, "tEXt", Encoding.ASCII.GetBytes("Comment\0" + Secret));
            Chunk(s, "eXIf", Encoding.ASCII.GetBytes("MM\0*" + Secret));
        }
        Chunk(s, "IDAT", [0x78, 0x9C, 0x63, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01]);
        if (complete)
            Chunk(s, "IEND", []);
        return s.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, header.AsSpan(4));
        s.Write(header);
        s.Write(data);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32([.. header.AsSpan(4).ToArray(), .. data]));
        s.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    /// <param name="orientation">The EXIF orientation to carry, or 0 for no EXIF at all.</param>
    public static byte[] Jpeg(int width = 640, int height = 480, int orientation = 6, bool withFrame = true)
    {
        using var s = new MemoryStream();
        s.Write([0xFF, 0xD8]);

        // APP0 JFIF
        Segment(s, 0xE0, [(byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);

        if (orientation > 0)
        {
            // APP1 EXIF, little-endian, with the orientation and a "GPS" string after the IFD.
            var tiff = new List<byte> { (byte)'I', (byte)'I', 0x2A, 0x00, 8, 0, 0, 0 };
            tiff.AddRange([2, 0]); // two entries
            tiff.AddRange([0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0]);
            tiff.AddRange([0x25, 0x88, 4, 0, 1, 0, 0, 0, 38, 0, 0, 0]); // GPS IFD pointer
            tiff.AddRange([0, 0, 0, 0]);
            tiff.AddRange(Encoding.ASCII.GetBytes(Secret));
            Segment(s, 0xE1, [.. "Exif\0\0"u8.ToArray(), .. tiff]);
        }

        // APP1 XMP, APP13 IPTC and a comment, all carrying the secret.
        Segment(s, 0xE1, Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0<x:xmpmeta>" + Secret + "</x:xmpmeta>"));
        Segment(s, 0xED, Encoding.ASCII.GetBytes("Photoshop 3.0\0" + Secret));
        Segment(s, 0xFE, Encoding.ASCII.GetBytes("Taken at " + Secret));

        // APP2 ICC profile — kept.
        Segment(s, 0xE2, Encoding.ASCII.GetBytes("ICC_PROFILE\0\u0001\u0001colour"));

        // DQT
        Segment(s, 0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]);

        if (withFrame)
        {
            Segment(s, 0xC0,
            [
                8,
                (byte)(height >> 8), (byte)height,
                (byte)(width >> 8), (byte)width,
                3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
            ]);
        }

        // SOS and a little "picture".
        Segment(s, 0xDA, [3, 1, 0, 2, 0x11, 3, 0x11, 0, 0x3F, 0]);
        s.Write([0x12, 0x34, 0x56, 0xFF, 0x00, 0x78]);
        s.Write([0xFF, 0xD9]);
        return s.ToArray();
    }

    private static void Segment(Stream s, byte marker, byte[] payload)
    {
        s.Write([0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)]);
        s.Write(payload);
    }

    public static byte[] WebP(int width = 320, int height = 200)
    {
        using var body = new MemoryStream();

        var vp8x = new byte[10];
        vp8x[0] = 0x08 | 0x04; // EXIF and XMP present
        WriteU24(vp8x.AsSpan(4), width - 1);
        WriteU24(vp8x.AsSpan(7), height - 1);
        WebPChunk(body, "VP8X", vp8x);

        var vp8l = new byte[5];
        vp8l[0] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(vp8l.AsSpan(1), (uint)((width - 1) | ((height - 1) << 14)));
        WebPChunk(body, "VP8L", vp8l);

        WebPChunk(body, "EXIF", Encoding.ASCII.GetBytes("MM\0*" + Secret));
        WebPChunk(body, "XMP ", Encoding.ASCII.GetBytes("<x:xmpmeta>" + Secret + "</x:xmpmeta>"));

        using var s = new MemoryStream();
        s.Write("RIFF"u8);
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(4 + body.Length));
        s.Write(size);
        s.Write("WEBP"u8);
        s.Write(body.ToArray());
        return s.ToArray();
    }

    private static void WebPChunk(Stream s, string type, byte[] data)
    {
        s.Write(Encoding.ASCII.GetBytes(type));
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
        s.Write(size);
        s.Write(data);
        if (data.Length % 2 == 1)
            s.WriteByte(0);
    }

    private static void WriteU24(Span<byte> span, int value)
    {
        span[0] = (byte)value;
        span[1] = (byte)(value >> 8);
        span[2] = (byte)(value >> 16);
    }

    public static bool Contains(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(Encoding.ASCII.GetBytes(needle)) >= 0;
}
