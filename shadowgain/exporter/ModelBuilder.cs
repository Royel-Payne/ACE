/*
 * Shadowgain 130 Stage 1 — assemble a character's body from the dat into glTF primitives.
 *
 * WHAT A CHARACTER ACTUALLY IS, in dat terms:
 *
 *   Setup (0x02......)      a SetupModel: an ordered list of PART ids, each a GfxObj, plus a
 *                           PlacementFrame per part giving its position and orientation.
 *   GfxObj (0x01......)     the geometry: a vertex array (position, normal, UVs) and polygons
 *                           that index into it. Each polygon names a SURFACE by index into the
 *                           GfxObj's own Surfaces list.
 *   Surface (0x08......)    either a solid colour or a texture reference + palette.
 *   Texture (0x06......)    the image. Palettised ones resolve through a Palette (0x04......).
 *
 * So building a body is: walk the parts, transform each by its placement frame, convert polygons
 * to triangles, and resolve each polygon's surface to a texture.
 *
 * TWO THINGS THAT ARE EASY TO GET WRONG AND ARE VISIBLE IMMEDIATELY:
 *
 *   1. UV INDICES ARE PER-POLYGON, NOT PER-VERTEX. A vertex carries a LIST of UVs and each
 *      polygon says which entry to use for each of its corners. A vertex shared between two
 *      polygons with different UV indices must be DUPLICATED, or the texture tears. This is why
 *      vertices are emitted per-corner below rather than reused.
 *   2. Polygons are convex fans, not triangles - NumPts is commonly 3 or 4 but not always.
 */

using System.Numerics;
using ACE.DatLoader;
using ACE.DatLoader.Entity;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;

namespace ACE.Shadowgain.DatExport;

public static class ModelBuilder
{
    /// <summary>
    /// Build the primitives for one SetupModel.
    ///
    /// `paletteOverrides` maps a source palette id to the replacement the character has chosen -
    /// skin, hair and eyes. Empty means "use whatever the surface says", which is the default
    /// appearance for that body.
    /// </summary>
    public static List<GltfPrimitive> BuildSetup(
        PortalDatDatabase portal,
        uint setupId,
        IReadOnlyDictionary<uint, uint> paletteOverrides,
        Action<string> log)
    {
        var primitives = new List<GltfPrimitive>();

        var setup = portal.ReadFromDat<SetupModel>(setupId);

        if (setup == null || setup.Parts.Count == 0)
        {
            log($"!! setup 0x{setupId:X8} has no parts");
            return primitives;
        }

        log($"    setup 0x{setupId:X8}: {setup.Parts.Count} parts");

        for (var i = 0; i < setup.Parts.Count; i++)
        {
            var partId = setup.Parts[i];

            // The placement frame positions and orients this part on the body. Without it every
            // part renders at the origin and the character is a heap.
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

            AddGfxObj(portal, partId, transform, paletteOverrides, primitives, log, $"part{i}");
        }

        return primitives;
    }

    /// <summary>Append one GfxObj's geometry, split into one primitive per surface.</summary>
    public static void AddGfxObj(
        PortalDatDatabase portal,
        uint gfxObjId,
        Matrix4x4 transform,
        IReadOnlyDictionary<uint, uint> paletteOverrides,
        List<GltfPrimitive> primitives,
        Action<string> log,
        string name)
    {
        var obj = portal.ReadFromDat<GfxObj>(gfxObjId);

        if (obj == null || obj.Polygons.Count == 0)
            return;

        // One primitive per surface, because a primitive carries exactly one material. Grouping
        // by surface also means a body part with skin + a face texture emits two draws rather
        // than one texture winning over the other.
        var bySurface = new Dictionary<int, GltfPrimitive>();

        // Normals are rotated but NOT translated, and must not inherit scale skew - the inverse
        // transpose is the correct matrix for that and matters as soon as DefaultScale is not 1.
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
                    prim.TexturePng = SurfacePng(portal, obj.Surfaces[surfaceIdx], paletteOverrides, log);

                bySurface[surfaceIdx] = prim;
                primitives.Add(prim);
            }

            var baseIndex = prim.Positions.Count;

            for (var v = 0; v < poly.Vertices.Count; v++)
            {
                var vert = poly.Vertices[v];

                prim.Positions.Add(Vector3.Transform(vert.Origin, transform));
                prim.Normals.Add(Vector3.Normalize(Vector3.TransformNormal(vert.Normal, normalMatrix)));

                // Per-POLYGON uv index into this vertex's own UV list - see the header note.
                var uv = new Vector2(0, 0);

                if (vert.UVs != null && vert.UVs.Count > 0)
                {
                    var uvIdx = (v < poly.PosUVIndices.Count) ? poly.PosUVIndices[v] : 0;

                    if (uvIdx < vert.UVs.Count)
                        uv = new Vector2(vert.UVs[uvIdx].U, vert.UVs[uvIdx].V);
                    else
                        uv = new Vector2(vert.UVs[0].U, vert.UVs[0].V);
                }

                prim.UVs.Add(uv);
            }

            // Convex fan -> triangles. NumPts is usually 3 or 4 but the format does not promise it.
            for (var t = 1; t < poly.Vertices.Count - 1; t++)
            {
                prim.Indices.Add(baseIndex);
                prim.Indices.Add(baseIndex + t);
                prim.Indices.Add(baseIndex + t + 1);
            }
        }
    }

    /// <summary>
    /// Resolve a Surface to PNG bytes, applying the character's palette choice if it replaces the
    /// one the surface names.
    /// </summary>
    private static byte[] SurfacePng(
        PortalDatDatabase portal,
        uint surfaceId,
        IReadOnlyDictionary<uint, uint> paletteOverrides,
        Action<string> log)
    {
        var _step = "read surface";

        try
        {
            var surface = portal.ReadFromDat<Surface>(surfaceId);
            _step = $"surface ok type={surface?.Type} tex=0x{surface?.OrigTextureId:X8} pal=0x{surface?.OrigPaletteId:X8}";

            if (surface == null)
                return null;

            // A solid-colour surface has no texture at all; leave the material untextured rather
            // than inventing a 1x1 image.
            if (surface.OrigTextureId == 0)
                return null;

            // TWO HOPS, NOT ONE. `Surface.OrigTextureId` names a SURFACETEXTURE (0x05......),
            // which is a LIST of Texture (0x06......) ids at descending detail - not a texture
            // itself. Reading it directly as a Texture misparses and throws EndOfStream, which is
            // exactly what it did: every body surface failed and the model came out untextured.
            _step = $"read surfacetexture 0x{surface.OrigTextureId:X8}";
            var surfaceTexture = portal.ReadFromDat<SurfaceTexture>(surface.OrigTextureId);

            if (surfaceTexture == null || surfaceTexture.Textures.Count == 0)
                return null;

            // Last entry is the highest detail in this list.
            var textureId = surfaceTexture.Textures[^1];

            _step = $"read texture 0x{textureId:X8}";
            var texture = portal.ReadFromDat<Texture>(textureId);
            _step = $"texture {texture?.Width}x{texture?.Height} fmt={texture?.Format} len={texture?.Length}";

            if (texture == null || texture.Length == 0)
                return null;

            // The character's own palettes replace the surface's default where they match. This
            // is what makes a body the player's skin tone rather than the setup's stock colour.
            if (surface.OrigPaletteId != 0
                && paletteOverrides.TryGetValue(surface.OrigPaletteId, out var replacement))
            {
                ApplyPalette(portal, texture, replacement);
            }

            _step += " | GetBitmap";
            using var bmp = texture.GetBitmap();
            using var ms = new MemoryStream();

            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);

            return ms.ToArray();
        }
        catch (Exception ex)
        {
            log($"    !! surface 0x{surfaceId:X8} [{_step}]: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Swap a texture's colours for another palette's.
    ///
    /// `Texture.CustomPaletteColors` already exists in ACE.DatLoader for exactly this - it is
    /// consulted by GetImageColorArray before the default palette - so the whole job is filling
    /// it with the replacement palette's colours.
    /// </summary>
    private static void ApplyPalette(PortalDatDatabase portal, Texture texture, uint paletteId)
    {
        var palette = portal.ReadFromDat<Palette>(paletteId);

        if (palette?.Colors == null)
            return;

        for (var i = 0; i < palette.Colors.Count; i++)
            texture.CustomPaletteColors[i] = palette.Colors[i];
    }
}
