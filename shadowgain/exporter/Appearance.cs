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
using System.Linq;

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
    /// The accumulated ranges, in the order they were added. Exposed for the 152 ObjDesc dump:
    /// palette ranges are the half of appearance that never shows up as geometry, so a diff that
    /// cannot see them would call two differently-dyed characters identical.
    ///
    /// These are in ABSOLUTE colour indices; the server's own ObjDesc carries eighths.
    /// </summary>
    public IReadOnlyList<SubPalette> SubPalettes => _subPalettes;

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

            // ABSOLUTE, not relative: a palette here is a full 2048-entry table that is
            // meaningful only over the slice it applies to, so the source is read at the SAME
            // index as the destination. Reading it from 0 pulls the wrong band - visibly, it dyed
            // Black Breath's gold robe teal, because the colours at [0..n) are a different
            // material's ramp entirely. Confirmed by dumping a skin palette: its non-black
            // entries start at 38, not 0.
            for (var i = 0; i < sub.NumColors; i++)
            {
                var index = (int)(sub.Offset + i);

                if (index >= overlay.Colors.Count)
                    break;

                overrides[index] = overlay.Colors[index];
            }
        }

        return overrides;
    }

    /// <summary>
    /// Fold in one worn item, via its ClothingTable entry.
    ///
    /// Ported from ACE's Creature_Networking.CalculateObjDesc, which is the server's own
    /// implementation of this calculation:
    ///
    ///   ClothingBaseEffects[setupId]  the CloObjectEffects: for each body PART this item covers,
    ///                                 the GfxObj that REPLACES it, plus texture swaps on it. This
    ///                                 is why a robe is not drawn over the legs - it becomes them.
    ///   ClothingSubPalEffects[tmpl]   the dye: a PaletteSet per range, from which the item's
    ///                                 SHADE selects the actual palette.
    ///
    /// `Ranges[].Offset` and `NumColors` are ABSOLUTE colour indices here. ACE divides them by 8
    /// on the way out because the client works in eighths and multiplies them back; this code
    /// works in absolute indices throughout, so they are used as-is. Dividing here would dye a
    /// eighth of the intended band and leave the rest of the garment its base colour.
    /// </summary>
    public bool ApplyClothing(PortalDatDatabase portal, uint clothingBaseId, uint setupId,
                              int paletteTemplate, double shade, Action<string> log)
    {
        var table = portal.ReadFromDat<ClothingTable>(clothingBaseId);

        if (table == null || table.ClothingBaseEffects.Count == 0)
            return false;

        if (!table.ClothingBaseEffects.TryGetValue(setupId, out var baseEffect))
        {
            // An item with no entry for this body simply does not render on it - Gear Knights and
            // Olthoi are the usual reason. Not an error, and not something to substitute around.
            log($"      no ClothingBaseEffect for setup 0x{setupId:X8}");
            return false;
        }

        foreach (var effect in baseEffect.CloObjectEffects)
        {
            PartSwaps[(int)effect.Index] = effect.ModelId;

            foreach (var tex in effect.CloTextureEffects)
            {
                if (!TextureSwaps.TryGetValue((int)effect.Index, out var map))
                    TextureSwaps[(int)effect.Index] = map = new Dictionary<uint, uint>();

                map[tex.OldTexture] = tex.NewTexture;
            }
        }

        if (table.ClothingSubPalEffects.Count == 0)
            return true;

        // The item's PaletteTemplate picks the dye set; ACE falls back to the first entry when the
        // template has none, rather than leaving the garment untinted.
        if (!table.ClothingSubPalEffects.TryGetValue((uint)paletteTemplate, out var subPal))
            subPal = table.ClothingSubPalEffects.Values.First();

        foreach (var cloSub in subPal.CloSubPalettes)
        {
            var set = portal.ReadFromDat<PaletteSet>(cloSub.PaletteSet);

            if (set == null || set.PaletteList.Count == 0)
                continue;

            // Shade selects WHICH palette in the set - this is the difference between the same
            // robe in pale yellow and in deep amber.
            var paletteId = set.GetPaletteID(shade);

            foreach (var range in cloSub.Ranges)
                AddSubPalette(paletteId, range.Offset, range.NumColors);
        }

        return true;
    }

    public uint SwapTexture(int partIndex, uint textureId)
    {
        if (TextureSwaps.TryGetValue(partIndex, out var map) && map.TryGetValue(textureId, out var replacement))
            return replacement;

        return textureId;
    }
}
