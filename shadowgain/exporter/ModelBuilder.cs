/*
 * Shadowgain 130 — assemble a character from the dat into glTF primitives.
 *
 * WHAT A CHARACTER IS, in dat terms:
 *
 *   Setup (0x02......)      a SetupModel: an ordered list of PART ids, each a GfxObj, plus a
 *                           PlacementFrame per part giving its position and orientation.
 *   GfxObj (0x01......)     the geometry: a vertex array (position, normal, UVs) and polygons
 *                           that index into it. Each polygon names a SURFACE by index into the
 *                           GfxObj's own Surfaces list.
 *   Surface (0x08......)    a texture reference + palette, or a solid colour.
 *   SurfaceTexture(0x05...) a LIST of Texture ids at descending detail - NOT a texture.
 *   Texture (0x06......)    the image. Palettised ones resolve through a Palette (0x04......).
 *
 * ...plus an Appearance (see Appearance.cs) accumulated from the body's ObjDesc, the character's
 * chosen palettes and every worn item, which can swap a part's geometry, swap its textures, or
 * recolour ranges of the palette.
 *
 * THREE THINGS THAT WERE WRONG FIRST TIME AND ARE INVISIBLE IN CODE:
 *
 *   1. The character's `Setup` DID is NOT the body - that comes from CharGen by heritage/gender.
 *   2. `Surface.OrigTextureId` names a SURFACETEXTURE, not a Texture. Two hops.
 *   3. UV INDICES ARE PER-POLYGON, NOT PER-VERTEX. A vertex carries a LIST of UVs and each
 *      polygon says which entry each of its corners uses, so a vertex shared between polygons
 *      with different UV indices must be DUPLICATED or the texture tears. Hence per-corner
 *      emission below.
 */

using System.Numerics;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;

namespace ACE.Shadowgain.DatExport;

public static class ModelBuilder
{
    public static List<GltfPrimitive> Build(
        PortalDatDatabase portal,
        uint setupId,
        Appearance appearance,
        Action<string> log)
    {
        var primitives = new List<GltfPrimitive>();

        var setup = portal.ReadFromDat<SetupModel>(setupId);

        if (setup == null || setup.Parts.Count == 0)
        {
            log($"!! setup 0x{setupId:X8} has no parts");
            return primitives;
        }

        // Composed once: every palettised texture on the body indexes the same colour table.
        var palette = appearance.HasPaletteWork ? appearance.ComposePalette(portal) : null;

        log($"    setup 0x{setupId:X8}: {setup.Parts.Count} parts"
            + (palette != null ? $", palette {palette.Count} colours" : ", no palette overrides")
            + (appearance.PartSwaps.Count > 0 ? $", {appearance.PartSwaps.Count} part swaps" : ""));

        for (var i = 0; i < setup.Parts.Count; i++)
        {
            // A worn item can REPLACE a part's geometry outright - that is how a robe becomes the
            // legs rather than being drawn over them.
            var partId = appearance.PartSwaps.TryGetValue(i, out var swap) ? swap : setup.Parts[i];

            var transform = Matrix4x4.Identity;

            if (setup.PlacementFrames.TryGetValue(0, out var placement)
                && i < placement.AnimFrame.Frames.Count)
            {
                var frame = placement.AnimFrame.Frames[i];
                transform = Matrix4x4.CreateFromQuaternion(frame.Orientation)
                          * Matrix4x4.CreateTranslation(frame.Origin);
            }

            var scale = i < setup.DefaultScale.Count ? setup.DefaultScale[i] : Vector3.One;

            if (scale != Vector3.One && scale != Vector3.Zero)
                transform = Matrix4x4.CreateScale(scale) * transform;

            AddGfxObj(portal, partId, transform, appearance, palette, i, primitives, log, $"part{i}");
        }

        return primitives;
    }

    public static void AddGfxObj(
        PortalDatDatabase portal,
        uint gfxObjId,
        Matrix4x4 transform,
        Appearance appearance,
        Dictionary<int, uint> palette,
        int partIndex,
        List<GltfPrimitive> primitives,
        Action<string> log,
        string name)
    {
        var obj = portal.ReadFromDat<GfxObj>(gfxObjId);

        if (obj == null || obj.Polygons.Count == 0)
            return;

        // One primitive per surface: a glTF primitive carries exactly one material.
        var bySurface = new Dictionary<int, GltfPrimitive>();

        // Normals rotate but do not translate, and must not inherit scale skew - the inverse
        // transpose is the right matrix, and it matters the moment DefaultScale is not 1.
        Matrix4x4.Invert(transform, out var inverted);
        var normalMatrix = Matrix4x4.Transpose(inverted);

        foreach (var poly in obj.Polygons.Values)
        {
            poly.LoadVertices(obj.VertexArray);

            if (poly.Vertices == null || poly.Vertices.Count < 3)
                continue;

            var surfaceIdx = poly.PosSurface;

            if (!bySurface.TryGetValue(surfaceIdx, out var prim))
            {
                prim = new GltfPrimitive { Name = $"{name}.s{surfaceIdx}" };

                if (surfaceIdx >= 0 && surfaceIdx < obj.Surfaces.Count)
                {
                    prim.TexturePng = SurfacePng(portal, obj.Surfaces[surfaceIdx],
                                                 appearance, palette, partIndex, log);
                }

                bySurface[surfaceIdx] = prim;
                primitives.Add(prim);
            }

            var baseIndex = prim.Positions.Count;

            for (var v = 0; v < poly.Vertices.Count; v++)
            {
                var vert = poly.Vertices[v];

                prim.Positions.Add(Vector3.Transform(vert.Origin, transform));
                prim.Normals.Add(Vector3.Normalize(Vector3.TransformNormal(vert.Normal, normalMatrix)));

                var uv = new Vector2(0, 0);

                if (vert.UVs != null && vert.UVs.Count > 0)
                {
                    var uvIdx = (v < poly.PosUVIndices.Count) ? poly.PosUVIndices[v] : 0;

                    uv = uvIdx < vert.UVs.Count
                        ? new Vector2(vert.UVs[uvIdx].U, vert.UVs[uvIdx].V)
                        : new Vector2(vert.UVs[0].U, vert.UVs[0].V);
                }

                prim.UVs.Add(uv);
            }

            // Convex fan -> triangles. NumPts is usually 3 or 4, but the format does not promise it.
            for (var t = 1; t < poly.Vertices.Count - 1; t++)
            {
                prim.Indices.Add(baseIndex);
                prim.Indices.Add(baseIndex + t);
                prim.Indices.Add(baseIndex + t + 1);
            }
        }
    }

    private static byte[] SurfacePng(
        PortalDatDatabase portal,
        uint surfaceId,
        Appearance appearance,
        Dictionary<int, uint> palette,
        int partIndex,
        Action<string> log)
    {
        try
        {
            var surface = portal.ReadFromDat<Surface>(surfaceId);

            if (surface == null || surface.OrigTextureId == 0)
                return null;

            // TWO HOPS. Surface.OrigTextureId names a SURFACETEXTURE (0x05......), which is a
            // LIST of Texture ids at descending detail. Reading it as a Texture throws
            // EndOfStream on every body surface and leaves the model silently untextured.
            var surfaceTextureId = appearance.SwapTexture(partIndex, surface.OrigTextureId);

            var surfaceTexture = portal.ReadFromDat<SurfaceTexture>(surfaceTextureId);

            if (surfaceTexture == null || surfaceTexture.Textures.Count == 0)
                return null;

            var texture = portal.ReadFromDat<Texture>(surfaceTexture.Textures[^1]);

            if (texture == null || texture.Length == 0)
                return null;

            // DECODE PALETTISED TEXTURES OURSELVES. DO NOT USE Texture.CustomPaletteColors.
            //
            // ACE's own P8/INDEX16 path does this:
            //
            //     Palette pal = DatManager.PortalDat.ReadFromDat<Palette>(DefaultPaletteId);
            //     foreach (entry in CustomPaletteColors) pal.Colors[entry.Key] = entry.Value;
            //
            // `ReadFromDat` returns the CACHED Palette instance, and that loop MUTATES it. So
            // applying a custom palette to one texture permanently corrupts the shared palette
            // for every later texture that references it. Clearing CustomPaletteColors afterwards
            // does not help - the damage is to the cached Palette, not to the texture.
            //
            // Symptom, which is why this is worth the extra thirty lines: the first part comes
            // out roughly right and everything after it goes black, so it reads as a lighting or
            // normals problem rather than a palette one. Decoding against a LOCAL copy is both a
            // fix and a guarantee that this exporter never leaves state behind in the dat cache.
            var png = DecodePalettised(portal, texture, palette);

            if (png != null)
                return png;

            using (var bmp = texture.GetBitmap())
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
        }
        catch (Exception ex)
        {
            log($"    !! surface 0x{surfaceId:X8}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// P8 / INDEX16 -> PNG, against a LOCAL palette copy with the character's overrides applied.
    /// Returns null for any format that is not palettised, so the caller falls back to GetBitmap.
    /// </summary>
    private static byte[] DecodePalettised(
        PortalDatDatabase portal, Texture texture, Dictionary<int, uint> overrides)
    {
        if (!texture.DefaultPaletteId.HasValue)
            return null;

        var isP8 = texture.Format == SurfacePixelFormat.PFID_P8;
        var isIndex16 = texture.Format == SurfacePixelFormat.PFID_INDEX16;

        if (!isP8 && !isIndex16)
            return null;

        var source = portal.ReadFromDat<Palette>(texture.DefaultPaletteId.Value);

        if (System.Environment.GetEnvironmentVariable("SG_PAL_DEBUG") == "1" && source != null)
        {
            var idxs = new List<int>();
            using (var r = new BinaryReader(new MemoryStream(texture.SourceData)))
                for (var k = 0; k < texture.Width * texture.Height; k++)
                    idxs.Add(isP8 ? r.ReadByte() : r.ReadInt16());

            Console.WriteLine($"      PAL tex {texture.Width}x{texture.Height} fmt={texture.Format} defPal=0x{texture.DefaultPaletteId:X8} palColors={source.Colors.Count} idxRange={idxs.Min()}..{idxs.Max()}");
        }

        if (source?.Colors == null || source.Colors.Count == 0)
            return null;

        // The copy is the whole point - see the note at the call site.
        var colors = new List<uint>(source.Colors);

        if (overrides != null)
        {
            foreach (var (index, colour) in overrides)
            {
                if (index >= 0 && index < colors.Count)
                    colors[index] = colour;
            }
        }

        using var bmp = new System.Drawing.Bitmap(texture.Width, texture.Height);
        using var reader = new BinaryReader(new MemoryStream(texture.SourceData));

        for (var y = 0; y < texture.Height; y++)
        {
            for (var x = 0; x < texture.Width; x++)
            {
                int index = isP8 ? reader.ReadByte() : reader.ReadInt16();

                if (index < 0 || index >= colors.Count)
                    index = 0;

                var argb = colors[index];

                bmp.SetPixel(x, y, System.Drawing.Color.FromArgb(
                    (int)((argb & 0xFF000000) >> 24),
                    (int)((argb & 0x00FF0000) >> 16),
                    (int)((argb & 0x0000FF00) >> 8),
                    (int)(argb & 0x000000FF)));
            }
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);

        return ms.ToArray();
    }
}
