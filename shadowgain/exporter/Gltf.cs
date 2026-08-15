/*
 * Shadowgain 130 — a minimal glTF 2.0 (.glb) writer.
 *
 * Hand-rolled rather than pulled from NuGet, and the reason is narrow: this needs to emit exactly
 * one shape of file — a static mesh, positions/normals/UVs, one material per surface, PNG
 * textures embedded — and a general-purpose glTF library brings a dependency, a version to track
 * and an API to learn for a job that is two chunks and a JSON header. The format is stable and
 * well specified; the whole writer is below and can be read in one sitting.
 *
 * GLB layout, for anyone editing this without the spec to hand:
 *
 *   magic "glTF" | version 2 | total length
 *   chunk 0: JSON  (length | "JSON" | utf8, padded with SPACES to 4 bytes)
 *   chunk 1: BIN   (length | "BIN\0" | bytes, padded with ZEROS to 4 bytes)
 *
 * The padding bytes differ per chunk type and viewers DO reject the wrong one, which is the kind
 * of detail that costs an hour if you assume both are zeros.
 */

using System.Numerics;
using System.Text;
using System.Text.Json;

namespace ACE.Shadowgain.DatExport;

/// <summary>One drawable piece: a triangle list with a single texture.</summary>
public sealed class GltfPrimitive
{
    public List<Vector3> Positions { get; } = new();
    public List<Vector3> Normals { get; } = new();
    public List<Vector2> UVs { get; } = new();
    public List<int> Indices { get; } = new();

    /// <summary>PNG bytes for this primitive's texture, or null for an untextured material.</summary>
    public byte[] TexturePng { get; set; }

    /// <summary>Used only to name the material readably in a debugger / model viewer.</summary>
    public string Name { get; set; } = "part";
}

public static class Gltf
{
    public static void Write(string path, IReadOnlyList<GltfPrimitive> primitives)
    {
        var bin = new MemoryStream();
        var bufferViews = new List<object>();
        var accessors = new List<object>();
        var images = new List<object>();
        var textures = new List<object>();
        var materials = new List<object>();
        var meshPrimitives = new List<object>();

        // A tiny helper per buffer view: everything here is tightly packed and 4-byte aligned,
        // which glTF requires for accessors that alias the buffer.
        int AddView(byte[] data, int? target = null)
        {
            Align(bin, 4);

            var offset = (int)bin.Length;
            bin.Write(data, 0, data.Length);

            var view = new Dictionary<string, object>
            {
                ["buffer"] = 0,
                ["byteOffset"] = offset,
                ["byteLength"] = data.Length,
            };

            if (target.HasValue)
                view["target"] = target.Value;

            bufferViews.Add(view);

            return bufferViews.Count - 1;
        }

        foreach (var prim in primitives)
        {
            if (prim.Indices.Count == 0)
                continue;

            // --- positions, with the min/max glTF REQUIRES on the POSITION accessor -----------
            var posBytes = FloatBytes(prim.Positions, 3);
            var posView = AddView(posBytes, 34962);   // ARRAY_BUFFER

            var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var max = new[] { float.MinValue, float.MinValue, float.MinValue };

            foreach (var p in prim.Positions)
            {
                min[0] = Math.Min(min[0], p.X); max[0] = Math.Max(max[0], p.X);
                min[1] = Math.Min(min[1], p.Y); max[1] = Math.Max(max[1], p.Y);
                min[2] = Math.Min(min[2], p.Z); max[2] = Math.Max(max[2], p.Z);
            }

            accessors.Add(new Dictionary<string, object>
            {
                ["bufferView"] = posView,
                ["componentType"] = 5126,             // FLOAT
                ["count"] = prim.Positions.Count,
                ["type"] = "VEC3",
                ["min"] = min,
                ["max"] = max,
            });

            var posAccessor = accessors.Count - 1;

            var nrmAccessor = -1;

            if (prim.Normals.Count == prim.Positions.Count)
            {
                var nrmView = AddView(FloatBytes(prim.Normals, 3), 34962);

                accessors.Add(new Dictionary<string, object>
                {
                    ["bufferView"] = nrmView,
                    ["componentType"] = 5126,
                    ["count"] = prim.Normals.Count,
                    ["type"] = "VEC3",
                });

                nrmAccessor = accessors.Count - 1;
            }

            var uvAccessor = -1;

            if (prim.UVs.Count == prim.Positions.Count)
            {
                var uvView = AddView(FloatBytes2(prim.UVs), 34962);

                accessors.Add(new Dictionary<string, object>
                {
                    ["bufferView"] = uvView,
                    ["componentType"] = 5126,
                    ["count"] = prim.UVs.Count,
                    ["type"] = "VEC2",
                });

                uvAccessor = accessors.Count - 1;
            }

            // --- indices ---------------------------------------------------------------------
            var idxBytes = new byte[prim.Indices.Count * 4];

            for (var i = 0; i < prim.Indices.Count; i++)
                BitConverter.GetBytes((uint)prim.Indices[i]).CopyTo(idxBytes, i * 4);

            var idxView = AddView(idxBytes, 34963);   // ELEMENT_ARRAY_BUFFER

            accessors.Add(new Dictionary<string, object>
            {
                ["bufferView"] = idxView,
                ["componentType"] = 5125,             // UNSIGNED_INT
                ["count"] = prim.Indices.Count,
                ["type"] = "SCALAR",
            });

            var idxAccessor = accessors.Count - 1;

            // --- material ---------------------------------------------------------------------
            var pbr = new Dictionary<string, object>
            {
                ["metallicFactor"] = 0.0,
                ["roughnessFactor"] = 0.9,
            };

            if (prim.TexturePng != null)
            {
                var imgView = AddView(prim.TexturePng);

                images.Add(new Dictionary<string, object>
                {
                    ["bufferView"] = imgView,
                    ["mimeType"] = "image/png",
                });

                textures.Add(new Dictionary<string, object> { ["source"] = images.Count - 1 });

                pbr["baseColorTexture"] = new Dictionary<string, object>
                {
                    ["index"] = textures.Count - 1,
                };
            }
            else
            {
                pbr["baseColorFactor"] = new[] { 0.8, 0.8, 0.8, 1.0 };
            }

            materials.Add(new Dictionary<string, object>
            {
                ["name"] = prim.Name,
                ["pbrMetallicRoughness"] = pbr,
                // AC body/clothing textures use their alpha channel for cut-outs (hair, straps).
                // MASK rather than BLEND: these are hard edges, and BLEND would need depth
                // sorting we are not doing.
                ["alphaMode"] = "MASK",
                ["alphaCutoff"] = 0.5,
                ["doubleSided"] = true,
            });

            var attributes = new Dictionary<string, object> { ["POSITION"] = posAccessor };

            if (nrmAccessor >= 0) attributes["NORMAL"] = nrmAccessor;
            if (uvAccessor >= 0) attributes["TEXCOORD_0"] = uvAccessor;

            meshPrimitives.Add(new Dictionary<string, object>
            {
                ["attributes"] = attributes,
                ["indices"] = idxAccessor,
                ["material"] = materials.Count - 1,
            });
        }

        Align(bin, 4);

        var binBytes = bin.ToArray();

        var gltf = new Dictionary<string, object>
        {
            ["asset"] = new Dictionary<string, object>
            {
                ["version"] = "2.0",
                ["generator"] = "Shadowgain sg-datexport",
            },
            ["scene"] = 0,
            ["scenes"] = new object[] { new Dictionary<string, object> { ["nodes"] = new[] { 0 } } },
            // AC is Z-up and glTF is Y-up, so the single root node carries the -90 degrees about
            // X that reconciles them. Doing it here rather than baking it into every vertex keeps
            // the geometry identical to the dat, which matters when comparing against the client.
            ["nodes"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["mesh"] = 0,
                    ["rotation"] = new[] { -0.7071068, 0.0, 0.0, 0.7071068 },
                },
            },
            ["meshes"] = new object[]
            {
                new Dictionary<string, object> { ["primitives"] = meshPrimitives },
            },
            ["materials"] = materials,
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new object[]
            {
                new Dictionary<string, object> { ["byteLength"] = binBytes.Length },
            },
        };

        if (images.Count > 0)
        {
            gltf["images"] = images;
            gltf["textures"] = textures;
            gltf["samplers"] = new object[]
            {
                new Dictionary<string, object> { ["wrapS"] = 10497, ["wrapT"] = 10497 },
            };

            foreach (Dictionary<string, object> t in textures)
                t["sampler"] = 0;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(gltf);

        // JSON chunk pads with SPACES, BIN pads with ZEROS. Viewers reject the wrong filler.
        var jsonPad = (4 - json.Length % 4) % 4;
        var binPad = (4 - binBytes.Length % 4) % 4;

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);

        var total = 12 + 8 + json.Length + jsonPad + 8 + binBytes.Length + binPad;

        w.Write(0x46546C67u);          // "glTF"
        w.Write(2u);
        w.Write((uint)total);

        w.Write((uint)(json.Length + jsonPad));
        w.Write(0x4E4F534Au);          // "JSON"
        w.Write(json);
        for (var i = 0; i < jsonPad; i++) w.Write((byte)0x20);

        w.Write((uint)(binBytes.Length + binPad));
        w.Write(0x004E4942u);          // "BIN\0"
        w.Write(binBytes);
        for (var i = 0; i < binPad; i++) w.Write((byte)0x00);
    }

    private static void Align(Stream s, int to)
    {
        while (s.Length % to != 0)
            s.WriteByte(0);
    }

    private static byte[] FloatBytes(List<Vector3> values, int _)
    {
        var bytes = new byte[values.Count * 12];

        for (var i = 0; i < values.Count; i++)
        {
            BitConverter.GetBytes(values[i].X).CopyTo(bytes, i * 12);
            BitConverter.GetBytes(values[i].Y).CopyTo(bytes, i * 12 + 4);
            BitConverter.GetBytes(values[i].Z).CopyTo(bytes, i * 12 + 8);
        }

        return bytes;
    }

    private static byte[] FloatBytes2(List<Vector2> values)
    {
        var bytes = new byte[values.Count * 8];

        for (var i = 0; i < values.Count; i++)
        {
            BitConverter.GetBytes(values[i].X).CopyTo(bytes, i * 8);
            BitConverter.GetBytes(values[i].Y).CopyTo(bytes, i * 8 + 4);
        }

        return bytes;
    }
}
