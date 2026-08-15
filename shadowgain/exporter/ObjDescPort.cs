/*
 * Shadowgain 152 — the LAYERING half of ObjDesc, ported from the server.
 *
 * `Appearance.ApplyClothing` already ports the INNER loop of ACE's
 * Creature.CalculateObjDesc: given one item and one setup, which parts it replaces and how it is
 * dyed. That part was right. What was missing is everything AROUND it — which items apply, in
 * WHICH ORDER, and against which setup id — and that is where a character stops being identical to
 * the one the game draws.
 *
 * Order is not cosmetic. A later item's part swaps overwrite an earlier one's, so getting the
 * sequence wrong puts the breastplate under the robe instead of over it. The server's sequence is:
 *
 *     clothes and cloaks, by ClothingPriority
 *     then armour, by TopLayerPriority (false, then unset, then true), then VisualClothingPriority
 *
 * The previous pass approximated this with "everything by ClothingPriority", on the stated grounds
 * that VisualClothingPriority is "a runtime value this exporter does not have". THAT WAS WRONG, and
 * it is the whole reason the model was nearly-but-not-quite right. `setVisualClothingPriority()` is
 *
 *     VisualClothingPriority = item.GetVisualPriority();
 *
 * and `ClothingTable.GetVisualPriority()` is a pure function of the ClothingTable record — which
 * this exporter loads from the same dat the server does. Nothing about it is runtime.
 *
 * FAITHFULNESS OVER TIDINESS. Two of the things below look like bugs and are reproduced anyway,
 * because the goal is to match what the client is sent, not to improve on it:
 *
 *   - GetVisualPriority() is called with NO ARGUMENT, so it always evaluates coverage against
 *     HUMAN_MALE (0x02000001) regardless of the wearer's actual body. ACE does this; so do we.
 *   - VisualClothingPriority is only computed for items in an Armor/Extremity slot. An ItemType.
 *     Armor item worn elsewhere sorts as null — first — and that is the real behaviour.
 *
 * Verify with `sg-objdesc` in game, which dumps what the client actually receives.
 *
 * TWO BRANCHES OF CalculateObjDesc ARE DELIBERATELY NOT PORTED, both checked against LIVE on
 * 2026-08-15 rather than assumed:
 *
 *   the `eo.Count == 0` branch, which returns an ObjDesc stored on the biota itself when nothing
 *   is equipped. Players do not have one: biota_properties_anim_part, _palette and _texture_map
 *   hold 0 rows between them for all 72 characters on the shard.
 *
 *   the `coverage.Count == 0 && ClothingBase.HasValue` fallback to WorldObject.CalculateObjDesc,
 *   for a creature wearing its own ClothingBase. No character has PropertyDataId.Setup's sibling
 *   ClothingBase (type 7) set — again 0 of 72.
 *
 * Both are monster and NPC paths. If either ever becomes reachable for a player the diff below
 * will show it as a whole-model mismatch rather than a subtle one, which is the failure mode to
 * want.
 */

using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;

namespace ACE.Shadowgain.DatExport;

/// <summary>
/// One equipped item, with everything CalculateObjDesc consults about it.
///
/// <paramref name="HasSlotInfo"/> is false when the caller supplied only the old four-field spec
/// and therefore knows nothing about item type or wielded slot. Such an item CANNOT be bucketed —
/// there is no honest answer to "is this armour?" — so it is layered by ClothingPriority alone,
/// which is what the pre-152 exporter did. Guessing a slot instead is worse than admitting the
/// gap: EquipMask.Clothing contains the Extremity bits, so guessing it puts every legacy item in
/// the ARMOUR bucket and sorts the lot by coverage mask rather than priority.
/// </summary>
public readonly record struct WornItem(
    uint ClothingBase,
    int PaletteTemplate,
    double Shade,
    int ClothingPriority,
    ItemType ItemType,
    EquipMask Wielded,
    bool? TopLayerPriority,
    uint SetupId = 0,
    string Label = null,
    bool HasSlotInfo = true);

public static class ObjDescPort
{
    /// <summary>
    /// Ported verbatim from CalculateObjDesc's switch. Some player races wear a setup that has no
    /// entry of its own in the Clothing Table, so the lookup falls back to one that does.
    ///
    /// Without this an Umbraen or Penumbraen renders NAKED — every garment misses and is silently
    /// skipped, which looks like a missing item rather than a missing remap.
    /// </summary>
    public static uint RemapSetup(uint setupId) => setupId switch
    {
        (uint)SetupConst.UmbraenMaleCrownGen or
        (uint)SetupConst.UmbraenMaleNoCrown or
        (uint)SetupConst.UmbraenMaleVoid => (uint)SetupConst.UmbraenMaleCrown,

        (uint)SetupConst.UmbraenFemaleNoCrown or
        (uint)SetupConst.UmbraenFemaleVoid => (uint)SetupConst.UmbraenFemaleCrown,

        (uint)SetupConst.PenumbraenMaleCrownGen or
        (uint)SetupConst.PenumbraenMaleNoCrown or
        (uint)SetupConst.PenumbraenMaleVoid => (uint)SetupConst.PenumbraenMaleCrown,

        (uint)SetupConst.PenumbraenFemaleNoCrown or
        (uint)SetupConst.PenumbraenFemaleVoid => (uint)SetupConst.PenumbraenFemaleCrown,

        (uint)SetupConst.UndeadMaleUndeadGen or
        (uint)SetupConst.UndeadMaleSkeleton or
        (uint)SetupConst.UndeadMaleSkeletonNoFlame or
        (uint)SetupConst.UndeadMaleZombie or
        (uint)SetupConst.UndeadMaleZombieNoFlame => (uint)SetupConst.UndeadMaleUndead,

        (uint)SetupConst.UndeadFemaleUndeadGen or
        (uint)SetupConst.UndeadFemaleSkeleton or
        (uint)SetupConst.UndeadFemaleSkeletonNoFlame or
        (uint)SetupConst.UndeadFemaleZombie or
        (uint)SetupConst.UndeadFemaleZombieNoFlame => (uint)SetupConst.UndeadFemaleUndead,

        (uint)SetupConst.AnakshayMale => (uint)SetupConst.HumanMale,
        (uint)SetupConst.AnakshayFemale => (uint)SetupConst.HumanFemale,

        _ => setupId,
    };

    /// <summary>
    /// `setVisualClothingPriority()`. Null unless the item both has a ClothingBase AND occupies an
    /// Armor/Extremity slot — the same guard the server applies, kept because the null is load
    /// bearing in the sort below.
    ///
    /// KNOWN EDGE, not currently reachable. On the server `VisualClothingPriority` is a STORED
    /// property that `setVisualClothingPriority()` overwrites; when its guard fails the previously
    /// stored value survives rather than becoming null. Here there is no stored value to survive.
    /// The two can therefore only disagree for an item that is `ItemType.Armor` but worn OUTSIDE an
    /// Armor/Extremity slot — which reaches the armour bucket by type and then sorts on a value
    /// this side does not have. No such item exists on either shard today, and Fred Sandford's
    /// verified-identical diff covers every item that does. If one ever appears, pass its stored
    /// priority through as a ninth `--item` field rather than guessing it.
    /// </summary>
    private static CoverageMask? VisualPriority(PortalDatDatabase portal, WornItem item)
    {
        if (item.ClothingBase == 0 || (item.Wielded & (EquipMask.Armor | EquipMask.Extremity)) == 0)
            return null;

        // No setup argument, exactly as ACE calls it — see the header note.
        return portal.ReadFromDat<ClothingTable>(item.ClothingBase)?.GetVisualPriority();
    }

    /// <summary>Nullable ordering that matches Comparer&lt;CoverageMask?&gt;.Default: null first.</summary>
    private static long SortKey(CoverageMask? mask) => mask.HasValue ? (long)mask.Value : -1L;

    /// <summary>
    /// The apply order. This is the function the whole file exists for.
    /// </summary>
    public static List<WornItem> Order(PortalDatDatabase portal, IEnumerable<WornItem> items)
    {
        var all = items.ToList();

        // Items with no slot information cannot be bucketed at all; they layer by priority alone.
        // See WornItem.HasSlotInfo.
        var legacy = all.Where(x => !x.HasSlotInfo).OrderBy(x => x.ClothingPriority).ToList();

        if (legacy.Count == all.Count)
            return legacy;

        // "Armor items" is BROADER than ItemType.Armor: anything worn in an Armor or Extremity slot
        // counts, which is what pulls gloves, boots and helms in even when they are typed Clothing.
        var armorItems = all
            .Where(x => x.HasSlotInfo)
            .Where(x => x.ItemType == ItemType.Armor || (x.Wielded & (EquipMask.Armor | EquipMask.Extremity)) != 0)
            .Select(x => (item: x, vis: VisualPriority(portal, x)))
            .ToList();

        var top = armorItems.Where(x => x.item.TopLayerPriority == true).OrderBy(x => SortKey(x.vis));
        var noLayer = armorItems.Where(x => x.item.TopLayerPriority == null).OrderBy(x => SortKey(x.vis));
        var bottom = armorItems.Where(x => x.item.TopLayerPriority == false).OrderBy(x => SortKey(x.vis));

        var sortedArmor = bottom.Concat(noLayer).Concat(top).Select(x => x.item).ToList();

        // Clothing NOT in an armour slot — robes, shirts, pants, cloaks. Extremity is excluded here
        // because it was already swept into armorItems above.
        var clothesAndCloaks = all
            .Where(x => x.HasSlotInfo)
            .Where(x => x.ItemType == ItemType.Clothing && (x.Wielded & (EquipMask.Armor | EquipMask.Extremity)) == 0)
            .OrderBy(x => x.ClothingPriority)
            .ToList();

        // Legacy items go first, so a mixed call still layers the fully-described items on top.
        return legacy.Concat(clothesAndCloaks).Concat(sortedArmor).ToList();
    }

    /// <summary>
    /// Fold every worn item into <paramref name="appearance"/>, in the server's order, against the
    /// server's setup ids.
    ///
    /// Returns the part indices that ended up COVERED, which the caller does not currently need —
    /// ModelBuilder walks the setup and treats PartSwaps as overrides, which reaches the same result
    /// as the server's "add the parts nothing covered" step — but which is returned because a
    /// divergence here is otherwise invisible.
    /// </summary>
    public static HashSet<int> ApplyAll(PortalDatDatabase portal, Appearance appearance,
                                        uint setupTableId, IEnumerable<WornItem> items,
                                        bool showHelm, bool showCloak, Action<string> log)
    {
        var thisSetupId = RemapSetup(setupTableId);
        var coverage = new HashSet<int>();

        if (thisSetupId != setupTableId)
            log($"    setup 0x{setupTableId:X8} has no clothing entries; falling back to 0x{thisSetupId:X8}");

        foreach (var item in Order(portal, items))
        {
            // CurrentWieldedLocation == HeadWear, an EQUALITY test in ACE, not a mask test.
            if (item.Wielded == EquipMask.HeadWear && !showHelm)
            {
                log($"    skip {item.Label ?? "headwear"} (ShowYourHelmOrHeadGear is off)");
                continue;
            }

            if (item.Wielded == EquipMask.Cloak && !showCloak)
            {
                log($"    skip {item.Label ?? "cloak"} (ShowYourCloak is off)");
                continue;
            }

            // A wand or a ring is wielded but covers nothing. Only meaningful when the caller told
            // us the slot — a legacy item has no wielded location, and dropping it for failing a
            // test it was never given the data to pass would render the character naked.
            if (item.HasSlotInfo
                && (item.Wielded & (EquipMask.Clothing | EquipMask.Armor | EquipMask.Cloak)) == 0)
                continue;

            if (item.ClothingBase == 0)
            {
                // No ClothingBase: the item's own Setup stands in as one. Ursuin Guise (WCID 32155)
                // is the usual example. Dropping these — which an inner join on ClothingBase does
                // silently — makes the item vanish from the model while the player is wearing it.
                foreach (var index in AddSetupAsClothingBase(portal, item, appearance))
                    coverage.Add(index);

                continue;
            }

            var table = portal.ReadFromDat<ClothingTable>(item.ClothingBase);

            if (table == null || table.ClothingBaseEffects.Count == 0)
                continue;

            // Real setup first, remapped setup second — the server's precedence, not either alone.
            var key = table.ClothingBaseEffects.ContainsKey(setupTableId) ? setupTableId
                    : table.ClothingBaseEffects.ContainsKey(thisSetupId) ? thisSetupId
                    : 0;

            if (key == 0)
            {
                log($"    {item.Label ?? $"0x{item.ClothingBase:X8}"} has no entry for this body");
                continue;
            }

            foreach (var effect in table.ClothingBaseEffects[key].CloObjectEffects)
                coverage.Add((int)effect.Index);

            appearance.ApplyClothing(portal, item.ClothingBase, key,
                                     item.PaletteTemplate, item.Shade, log);
        }

        return coverage;
    }

    /// <summary>
    /// `AddSetupAsClothingBase`. Every part of the item's own setup becomes a part swap, except the
    /// null head part, which ACE excludes by the same two-part test reproduced here.
    /// </summary>
    private static List<int> AddSetupAsClothingBase(PortalDatDatabase portal, WornItem item, Appearance appearance)
    {
        var added = new List<int>();

        if (item.SetupId == 0)
            return added;

        var setup = portal.ReadFromDat<SetupModel>(item.SetupId);

        if (setup == null)
            return added;

        for (var i = 0; i < setup.Parts.Count; i++)
        {
            if (setup.Parts[i] == 0x010001EC && i == 16)
                continue;

            appearance.PartSwaps[i] = setup.Parts[i];
            added.Add(i);
        }

        return added;
    }
}
