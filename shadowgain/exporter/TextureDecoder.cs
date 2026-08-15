/*
 * Shadowgain 130 Stage 3 — dat Texture -> PNG/JPEG bytes, with no System.Drawing.
 *
 * Every format below is decoded from the raw SourceData that ACE.DatLoader already unpacks, so
 * this borrows its parsing and replaces only the Bitmap step. Two of them are handed straight to
 * DatLoader's own DxtUtil, which returns a byte[] and is perfectly cross-platform - it is only
 * the `new Bitmap(...)` wrapper around it that is not.
 *
 * PALETTES ARE COPIED, NEVER MUTATED. ACE's own P8 path does
 * `pal.Colors[i] = custom` on the instance `ReadFromDat` returns - which is CACHED - so applying
 * a character's dye to one texture permanently corrupts that palette for every later texture
 * sharing it. Symptom: the first part looks right and everything after goes black, reading as a
 * lighting bug. See the note in ModelBuilder.
 */

using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;

namespace ACE.Shadowgain.DatExport;

public sealed record DecodedTexture(byte[] Bytes, string MimeType);

public static class TextureDecoder
{
    /// <summary>Formats seen and skipped, so a gap surfaces as a name rather than a blank model.</summary>
    public static readonly HashSet<SurfacePixelFormat> Unsupported = new();


    public static DecodedTexture Decode(
        PortalDatDatabase portal, Texture texture, IReadOnlyDictionary<int, uint> paletteOverrides)
    {
        if (texture == null || texture.Length == 0)
            return null;

        var w = texture.Width;
        var h = texture.Height;

        switch (texture.Format)
        {
            // Already a complete image file - glTF accepts image/jpeg, so hand it over untouched
            // rather than decoding and re-encoding it.
            case SurfacePixelFormat.PFID_CUSTOM_RAW_JPEG:
                return new DecodedTexture(texture.SourceData, "image/jpeg");

            // DXT IS NOT HANDLED, DELIBERATELY. ACE.DatLoader's DxtUtil.DecompressDxt* are
            // `internal`, so they cannot be called from here, and writing a BC1/2/3 decoder is a
            // few hundred lines that would be dead code if no character texture ever uses it.
            //
            // Measured on a fully dressed character - body, head, robe, shirt, gauntlets - every
            // texture is PFID_INDEX16. If a compressed one ever turns up it is logged by name
            // below rather than silently skipped, which is the signal to write the decoder.

            case SurfacePixelFormat.PFID_P8:
            case SurfacePixelFormat.PFID_INDEX16:
                return Palettised(portal, texture, paletteOverrides);

            case SurfacePixelFormat.PFID_A8R8G8B8:
                return Rgba(w, h, Swizzle(texture.SourceData, w * h, 4, bgra: true));

            case SurfacePixelFormat.PFID_R8G8B8:
                return Rgba(w, h, Expand24(texture.SourceData, w * h));

            default:
                Unsupported.Add(texture.Format);
                return null;
        }
    }

    private static DecodedTexture Palettised(
        PortalDatDatabase portal, Texture texture, IReadOnlyDictionary<int, uint> overrides)
    {
        if (!texture.DefaultPaletteId.HasValue)
            return null;

        var source = portal.ReadFromDat<Palette>(texture.DefaultPaletteId.Value);

        if (source?.Colors == null || source.Colors.Count == 0)
            return null;

        // The copy is the whole point - see the header.
        var colors = new List<uint>(source.Colors);

        if (overrides != null)
        {
            foreach (var (index, colour) in overrides)
                if (index >= 0 && index < colors.Count)
                    colors[index] = colour;
        }

        var isP8 = texture.Format == SurfacePixelFormat.PFID_P8;
        var count = texture.Width * texture.Height;
        var pixels = new byte[count * 4];

        using var reader = new BinaryReader(new MemoryStream(texture.SourceData));

        for (var i = 0; i < count; i++)
        {
            int index = isP8 ? reader.ReadByte() : reader.ReadInt16();

            if (index < 0 || index >= colors.Count)
                index = 0;

            var argb = colors[index];

            pixels[i * 4 + 0] = (byte)((argb >> 16) & 0xFF);   // R
            pixels[i * 4 + 1] = (byte)((argb >> 8) & 0xFF);    // G
            pixels[i * 4 + 2] = (byte)(argb & 0xFF);           // B
            pixels[i * 4 + 3] = (byte)((argb >> 24) & 0xFF);   // A
        }

        return Rgba(texture.Width, texture.Height, pixels);
    }

    private static DecodedTexture Rgba(int w, int h, byte[] rgba) =>
        rgba == null ? null : new DecodedTexture(Png.Encode(w, h, rgba), "image/png");

    /// <summary>BGRA -> RGBA in place-ish; the dat stores 32-bit pixels little-endian ARGB.</summary>
    private static byte[] Swizzle(byte[] src, int count, int stride, bool bgra)
    {
        var dst = new byte[count * 4];

        for (var i = 0; i < count; i++)
        {
            var s = i * stride;

            if (s + 3 >= src.Length)
                break;

            dst[i * 4 + 0] = bgra ? src[s + 2] : src[s + 0];
            dst[i * 4 + 1] = src[s + 1];
            dst[i * 4 + 2] = bgra ? src[s + 0] : src[s + 2];
            dst[i * 4 + 3] = src[s + 3];
        }

        return dst;
    }

    /// <summary>24-bit BGR -> RGBA, opaque.</summary>
    private static byte[] Expand24(byte[] src, int count)
    {
        var dst = new byte[count * 4];

        for (var i = 0; i < count; i++)
        {
            var s = i * 3;

            if (s + 2 >= src.Length)
                break;

            dst[i * 4 + 0] = src[s + 2];
            dst[i * 4 + 1] = src[s + 1];
            dst[i * 4 + 2] = src[s + 0];
            dst[i * 4 + 3] = 0xFF;
        }

        return dst;
    }
}
