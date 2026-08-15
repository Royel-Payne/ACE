/*
 * Shadowgain 130 Stage 3 — a minimal PNG encoder, and texture decoding without System.Drawing.
 *
 * WHY THIS EXISTS: THE DROPLET IS LINUX.
 *
 * The model has to be assembled server-side per character, because gear combinations are
 * combinatorial and cannot be pre-baked. `System.Drawing.Common` is Windows-only from .NET 7
 * onward, and ACE.DatLoader's `Texture.GetBitmap` is built on it - so the icon exporter can stay
 * Windows-bound (its output is committed) but the MODEL path cannot.
 *
 * Both halves are small and fully specified, which is why this is a better trade than adding
 * ImageSharp or SkiaSharp: a PNG is IHDR + IDAT + IEND with one zlib stream, and the texture
 * formats that actually appear on bodies and clothing are palettised or DXT, both of which
 * DatLoader already decodes to plain byte arrays (DxtUtil returns byte[], not a Bitmap).
 */

using System.Buffers.Binary;
using System.IO.Compression;

namespace ACE.Shadowgain.DatExport;

public static class Png
{
    /// <summary>Encode 8-bit RGBA to PNG. `pixels` is row-major, 4 bytes per pixel.</summary>
    public static byte[] Encode(int width, int height, byte[] pixels)
    {
        using var ms = new MemoryStream();

        // Signature.
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // IHDR: 8-bit, colour type 6 (RGBA), no interlace.
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(ms, "IHDR", ihdr);

        // Scanlines, each prefixed with a filter byte. Filter 0 (None) throughout: the images are
        // tiny and deflate does the work - a filter heuristic would add code for a few kilobytes.
        var raw = new byte[height * (1 + width * 4)];

        for (var y = 0; y < height; y++)
        {
            var dst = y * (1 + width * 4);
            raw[dst] = 0;
            Buffer.BlockCopy(pixels, y * width * 4, raw, dst + 1, width * 4);
        }

        using (var deflated = new MemoryStream())
        {
            using (var z = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw, 0, raw.Length);

            WriteChunk(ms, "IDAT", deflated.ToArray());
        }

        WriteChunk(ms, "IEND", Array.Empty<byte>());

        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);

        // CRC covers the type AND the data, not the length - a classic off-by-one-field that
        // produces a file every viewer rejects with no useful message.
        var crc = Crc32(typeBytes, data);

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            var c = n;

            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        var c = 0xFFFFFFFFu;

        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);

        return c ^ 0xFFFFFFFFu;
    }
}
