/*
 * Shadowgain 130 Stage 1b/2 — the appearance model.
 *
 * `ObjDesc` is the ONE structure AC uses to say "this thing looks different from its setup", and
 * everything that changes a character's look produces one:
 *
 *   SexCG.BaseObjDesc          the naked body for a heritage/gender
 *   ClothingTable base effects a worn item: which parts it replaces and with what textures
 *   the character's own row    chosen skin / hair / eye palettes
 *
 * It carries three kinds of change, and all three matter:
 *
 *   AnimPartChanges   part index -> a DIFFERENT GfxObj. This is how a robe replaces the legs
 *                     with robe geometry rather than drawing over them.
 *   TextureChanges    part index -> replace texture A with texture B on that part.
 *   SubPalettes       overlay a palette's colours into a RANGE of the base palette. This is the
 *                     one that makes skin a skin tone and hair a hair colour, because a body
 *                     texture is palettised and every recolour is a range swap, never a new image.
 *
 * Accumulating these in order - body first, then clothing - is the whole of character assembly.
 * Stage 2 is therefore not new machinery, it is more ObjDescs fed to the same accumulator.
 */

using ACE.DatLoader;
using ACE.DatLoader.Entity;
using ACE.DatLoader.FileTypes;

namespace ACE.Shadowgain.DatExport;

public sealed class Appearance
{
    /// <summary>part index -> the GfxObj that replaces the setup's own part.</summary>
    public Dictionary<int, uint> PartSwaps { get; } = new();

    /// <summary>part index -> (old texture -> new texture).</summary>
    public Dictionary<int, Dictionary<uint, uint>> TextureSwaps { get; } = new();

    /// <summary>The base palette every SubPalette range is overlaid onto.</summary>
    public uint BasePalette { get; private set; }

    private readonly List<SubPalette> _subPalettes = new();

    /// <summary>
    /// Fold one ObjDesc in. Order matters: later calls win, which is why the body goes first and
    /// clothing after.
    /// </summary>
    public void Apply(ObjDesc desc)
    {
        if (desc == null)
            return;

        if (desc.PaletteID != 0)
            BasePalette = desc.PaletteID;

        foreach (var sub in desc.SubPalettes)
            _subPalettes.Add(sub);

        foreach (var change in desc.AnimPartChanges)
            PartSwaps[change.PartIndex] = change.PartID;

        foreach (var change in desc.TextureChanges)
        {
            if (!TextureSwaps.TryGetValue(change.PartIndex, out var map))
                TextureSwaps[change.PartIndex] = map = new Dictionary<uint, uint>();

            map[change.OldTexture] = change.NewTexture;
        }
    }

    /// <summary>Add a bare palette range, for a choice that arrives as an id rather than an ObjDesc.</summary>
    public void AddSubPalette(uint paletteId, uint offset, uint numColors)
    {
        if (paletteId != 0)
            _subPalettes.Add(new SubPalette { SubID = paletteId, Offset = offset, NumColors = numColors });
    }

    public void SetBasePalette(uint paletteId)
    {
        if (paletteId != 0)
            BasePalette = paletteId;
    }

    public bool HasPaletteWork => _subPalettes.Count > 0;

    /// <summary>
    /// The colour OVERRIDES only - the sub-palette ranges, and nothing else.
    ///
    /// THIS DELIBERATELY DOES NOT RETURN THE WHOLE BASE PALETTE, and getting that wrong is what
    /// turned the first attempt's skin black. `Texture.CustomPaletteColors` is OVERLAID on top of
    /// whatever palette the texture itself names (see Texture.GetPaletteIndexes), and different
    /// body textures name different palettes. Filling all 2048 entries from the body's base
    /// palette therefore does not "set the palette" - it DESTROYS each texture's own colours and
    /// replaces them with a table they were never indexed against.
    ///
    /// Only the ranges the character actually chose belong here: skin [0x00,0x18), hair
    /// [0x18,0x20), eyes [0x20,0x28) in raw units - the same offsets ACE's own
    /// WorldObject.AddBaseModelData uses.
    /// </summary>
    public Dictionary<int, uint> ComposePalette(PortalDatDatabase portal)
    {
        var overrides = new Dictionary<int, uint>();

        foreach (var sub in _subPalettes)
        {
            var overlay = portal.ReadFromDat<Palette>(sub.SubID);

            if (overlay?.Colors == null || overlay.Colors.Count == 0)
                continue;

            for (var i = 0; i < sub.NumColors && i < overlay.Colors.Count; i++)
                overrides[(int)(sub.Offset + i)] = overlay.Colors[i];
        }

        return overrides;
    }

    public uint SwapTexture(int partIndex, uint textureId)
    {
        if (TextureSwaps.TryGetValue(partIndex, out var map) && map.TryGetValue(textureId, out var replacement))
            return replacement;

        return textureId;
    }
}
