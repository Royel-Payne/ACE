/*
 * Shadowgain 124 - dat asset + table exporter for the web character sheet.
 *
 * WHY THIS EXISTS
 *
 * my.shadowgain.com renders skills, attributes and inventory the way the in-game panel does,
 * which means it needs the client's own icons and the client's own XP curves. Both live in
 * portal.dat, which is on Chris's machine and reachable from nowhere else - Cowork's sandbox
 * cannot see it, and the droplet has no client install. So the dat is read HERE, once, and the
 * output is committed: PNGs the front-end serves as static assets, and JSON the API reads at
 * start-up. Nothing at runtime ever touches a dat file.
 *
 * The tables matter as much as the icons. The web sheet's whole reason for existing is showing
 * TRUE ranks past the point the client can display them, and rank is a function of the dat's XP
 * table (see Player_Skills.CalcSkillRankUncapped / Player_Attributes.AttributeRankCost). Porting
 * that math to Python without the table would mean hard-coding 200-odd numbers by hand. Exporting
 * the table instead means the Python side reads the SAME numbers the server does, from the SAME
 * file, and a dat swap re-exports rather than needing a transcription pass.
 *
 * USAGE
 *
 *   sg-datexport --dat "<dir with portal.dat>" --out "<web/api/data + landing/assets root>"
 *                [--icons] [--tables] [--item-ids <file>]
 *
 *   With neither --icons nor --tables, it does both.
 *
 * The dat directory is the retail client install, whose files are named `portal.dat` /
 * `cell.dat`. ACE's own DatManager.Initialize expects the `client_portal.dat` naming it ships
 * with, so this opens PortalDatDatabase directly rather than going through DatManager - it needs
 * exactly one dat and none of DatManager's static wiring.
 */

using System.Globalization;
using System.Text.Json;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Shadowgain.DatExport;

public static class Program
{
    /// <summary>
    /// Icons in portal.dat are 32x32 textures inside the 0x06 (RenderSurface) range. That size
    /// test is the whole filter: the same range also holds wall textures, UI art and terrain
    /// alphas at every other dimension, and exporting all ~30,000 of them would take minutes and
    /// produce ~200MB of assets the site would never reference.
    /// </summary>
    private const int IconSize = 32;

    private const uint TextureRangeStart = 0x06000000;
    private const uint TextureRangeEnd = 0x07FFFFFF;

    /// <summary>
    /// The client's own attribute and vital icons, 25x25, palettised.
    ///
    /// FOUND ON THE SECOND ATTEMPT. The first hunt swept only 32x32 textures - the size item and
    /// skill icons use - found nothing that looked like an attribute, and concluded the dat had
    /// none, so the exporter drew substitutes. Both halves of that were wrong:
    ///
    ///   * these are 25x25, not 32x32, so the sweep never looked at them;
    ///   * and they are PALETTISED, so they would have failed to decode anyway while
    ///     DatManager.PortalDat was unset - see the note on DatManager.Initialize in Main.
    ///
    /// THE ORDER IS NOT THE ENUM ORDER, which is the trap here. The textures run
    /// Endurance, Focus, Quickness, Self, Strength, Coordination across 02C4..02C9, while
    /// PropertyAttribute runs Strength, Endurance, Quickness, Coordination, Focus, Self.
    /// Assigning them in id order would have given every attribute the wrong picture, and it
    /// would have looked plausible. Each pairing below was confirmed by cropping the icon column
    /// out of an in-game screenshot of the attribute panel and diffing it against the export.
    /// </summary>
    private static readonly (string Key, uint IconId)[] AttributeIcons =
    {
        ("strength",     0x060002C8),
        ("endurance",    0x060002C4),
        ("coordination", 0x060002C9),
        ("quickness",    0x060002C6),
        ("focus",        0x060002C5),
        ("self",         0x060002C7),
    };

    /// <summary>
    /// UI icons that belong to no table and no item.
    ///
    /// `mainpack` is the character's own inventory, which is not an object in the shard and so
    /// has no IconId to read - every other pack in the side bar is a real item and carries its
    /// own. The client draws a fixed backpack there, and it was identified by template-matching
    /// the slot out of an in-game screenshot against every 32x32 texture in the dat: 0x0600127E
    /// beat the runner-up by 4.7x, and matches by eye. Matched on the middle strip only, because
    /// the client overlays a yellow collapse chevron on the left of that slot and a fullness bar
    /// down the right.
    /// </summary>
    private static readonly (string Key, uint IconId)[] UiIcons =
    {
        ("mainpack", 0x0600127E),
    };

    /// <summary>The three hearts, in the panel's own order: red, yellow, blue.</summary>
    private static readonly (string Key, uint IconId)[] VitalIcons =
    {
        ("health",  0x06004C3B),
        ("stamina", 0x06004C3C),
        ("mana",    0x06004C3D),
    };

    public static int Main(string[] args)
    {
        string datDir = null, outDir = null, itemIdFile = null;
        bool doIcons = false, doTables = false, doSizes = false;
        string modelSetup = null, modelHead = null, skinPalette = null, hairPalette = null, eyesPalette = null;
        string heritage = null, gender = null, charSetup = null, objDescOut = null;
        // Default TRUE, matching CharacterOptions2.Default, which has ShowHelm and ShowCloak set.
        // A character who has never touched the option shows both, so absence must mean shown.
        bool showHelm = true, showCloak = true;
        var items = new List<string>();
        var headTextures = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dat": datDir = Next(args, ref i); break;
                case "--out": outDir = Next(args, ref i); break;
                case "--item-ids": itemIdFile = Next(args, ref i); break;
                case "--icons": doIcons = true; break;
                case "--sizes": doSizes = true; break;
                case "--model": modelSetup = Next(args, ref i); break;
                case "--heritage": heritage = Next(args, ref i); break;
                case "--gender": gender = Next(args, ref i); break;
                case "--head": modelHead = Next(args, ref i); break;
                case "--skin": skinPalette = Next(args, ref i); break;
                case "--hair": hairPalette = Next(args, ref i); break;
                case "--eyes": eyesPalette = Next(args, ref i); break;
                case "--item": items.Add(Next(args, ref i)); break;
                // The character's OWN SetupTableId. Not the same thing as the heritage/gender
                // default from CharGen: the Barber writes this, and the server keys every clothing
                // lookup on it (152).
                case "--setup": charSetup = Next(args, ref i); break;
                case "--no-helm": showHelm = false; break;
                case "--no-cloak": showCloak = false; break;
                // "old:new" texture swaps on the head (part 0x10) - eyes, nose, mouth and hair.
                // AddBaseModelData applies all four this way; without them the model wears the
                // head model's DEFAULT face instead of the one the player chose (152).
                case "--head-tex": headTextures.Add(Next(args, ref i)); break;
                // Emit the computed ObjDesc as JSON, for diffing against the server's `sg-objdesc`.
                case "--objdesc-json": objDescOut = Next(args, ref i); break;
                case "--tables": doTables = true; break;
                default:
                    Console.Error.WriteLine($"unknown argument: {args[i]}");
                    return Usage();
            }
        }

        if (datDir == null || outDir == null)
            return Usage();

        // Neither flag means both - the common case is a full refresh.
        if (!doIcons && !doTables && !doSizes && modelSetup == null && heritage == null)
            doIcons = doTables = true;

        // The dat stores strings in codepage 1252, which .NET Framework had built in and .NET
        // Core does not. Without this, opening the portal dat dies inside GeneratorTable with
        // "No data is available for encoding 1252" - ACE.Server registers the same provider at
        // start-up for exactly this reason.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        var portalPath = FindPortalDat(datDir);

        if (portalPath == null)
        {
            Console.Error.WriteLine($"!! no portal.dat / client_portal.dat under {datDir}");
            return 1;
        }

        Console.WriteLine($"==> opening {portalPath}");

        // GO THROUGH DatManager, NOT `new PortalDatDatabase(...)`.
        //
        // This started as a direct construction, because the exporter needs exactly one dat and
        // none of DatManager's static wiring. That was wrong in a way that failed SILENTLY:
        // Texture.GetBitmap resolves a palettised image through `DatManager.PortalDat` (see
        // Texture.GetPaletteIndexes), and with that static never set every PFID_P8 / PFID_INDEX16
        // texture threw a NullReferenceException. The catch in SaveTexture counted those as
        // "skipped" and moved on - so the export looked like it worked, and the entire palettised
        // half of the dat, which is where the UI icons live, was quietly missing.
        //
        // DatManager.Initialize insists on the `client_portal.dat` name; FindPortalDat still
        // accepts `portal.dat`, so that case is warned about rather than silently degraded.
        PortalDatDatabase portal;
        var retiredSkillsAdded = false;

        try
        {
            if (Path.GetFileName(portalPath).Equals("client_portal.dat", StringComparison.OrdinalIgnoreCase))
            {
                // loadCell: false - nothing here reads landblocks, and the cell dat is 200MB.
                DatManager.Initialize(Path.GetDirectoryName(portalPath), keepOpen: true, loadCell: false);
                portal = DatManager.PortalDat;

                // Initialize already called AddRetiredSkills; calling it again throws on the
                // duplicate key. Only the fallback path below has to do it for itself.
                retiredSkillsAdded = true;
            }
            else
            {
                Console.Error.WriteLine("!! WARNING: this dat is not named client_portal.dat, so DatManager cannot");
                Console.Error.WriteLine("!! be initialised and PALETTISED TEXTURES WILL FAIL TO DECODE.");
                portal = new PortalDatDatabase(portalPath, keepOpen: true);
            }
        }
        catch (IOException ex)
        {
            // A RUNNING CLIENT HOLDS AN EXCLUSIVE WRITE LOCK on its dats, and ACE.DatLoader opens
            // with the default FileShare.Read - so this fails for as long as the game is up. The
            // fix is a second install rather than a patch to ACE.DatLoader: that file is upstream
            // ACEmulator's and every edit there is a merge conflict on the next pull from master.
            Console.Error.WriteLine($"!! cannot open {portalPath}: {ex.Message}");
            Console.Error.WriteLine("!! the AC client locks its own dats while running - close it, or point");
            Console.Error.WriteLine("!! --dat at a second install (see shadowgain/web/README.md).");
            return 1;
        }

        Console.WriteLine($"    iteration {portal.Iteration}, {portal.AllFiles.Count:N0} records");

        // The dat's own SkillTable predates the retired-skill consolidation, so without this
        // Axe/Bow/Dagger/... resolve to nothing and every character still carrying frozen ranks
        // in one shows a blank row. DatManager.Initialize does it for us on that path.
        if (!retiredSkillsAdded)
            portal.SkillTable.AddRetiredSkills();

        // --sizes: a census of the texture range by dimension. Added because the first hunt for
        // the attribute icons assumed they were 32x32 like item icons, found nothing, and
        // concluded the dat had none. It has them - they are UI chrome at a different size, and
        // this is how you find that out instead of guessing.
        if (doSizes)
        {
            ReportSizes(portal, outDir);
            return 0;
        }

        // --model: Shadowgain 130 Stage 1. Assemble one character's body into a .glb.
        if (modelSetup != null || heritage != null)
        {
            BuildModel(portal, outDir, heritage, gender, skinPalette, hairPalette, eyesPalette, modelHead, items,
                       charSetup, showHelm, showCloak, objDescOut, headTextures);
            return 0;
        }

        if (doTables)
            ExportTables(portal, outDir);

        if (doIcons)
            ExportIcons(portal, outDir, itemIdFile);

        Console.WriteLine("==> done");
        return 0;
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"{args[i]} needs a value");

        return args[++i];
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "usage: sg-datexport --dat <client dir> --out <asset root> [--icons] [--tables] [--item-ids <file>]");
        return 2;
    }

    private static string FindPortalDat(string dir)
    {
        foreach (var name in new[] { "client_portal.dat", "portal.dat" })
        {
            var path = Path.Combine(dir, name);

            if (File.Exists(path))
                return path;
        }

        return null;
    }

    // ---- tables -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static void ExportTables(PortalDatDatabase portal, string outDir)
    {
        var dataDir = Path.Combine(outDir, "data");
        Directory.CreateDirectory(dataDir);

        // --- xptable.json: the curves the API re-derives true rank from ---------------------
        //
        // CharacterLevelXPList is ulong (retail level 275 costs more than a uint holds); the rest
        // are uint. JSON numbers cover both without ceremony, and Python's ints are arbitrary
        // precision, so nothing is lost on the way across.
        var xp = portal.XpTable;

        Write(Path.Combine(dataDir, "xptable.json"), new
        {
            _source = "portal.dat 0x0E000018",
            attribute = xp.AttributeXpList,
            vital = xp.VitalXpList,
            trainedSkill = xp.TrainedSkillXpList,
            specializedSkill = xp.SpecializedSkillXpList,
            level = xp.CharacterLevelXPList,
            levelSkillCredits = xp.CharacterLevelSkillCreditList,
        });

        Console.WriteLine($"    xptable.json      attr {xp.AttributeXpList.Count}, trained {xp.TrainedSkillXpList.Count}, "
                          + $"spec {xp.SpecializedSkillXpList.Count}, levels {xp.CharacterLevelXPList.Count}");

        // --- skills.json: name, icon, costs, and usability ---------------------------------
        //
        // MinLevel is the one field the Skills tab cannot be built without: it is what separates
        // "Untrained (pruned - ranks kept)" from "Unusable". 1 = usable while untrained,
        // 2 = needs training. The front-end groups on it; see the mockup's four group headers.
        var skills = new SortedDictionary<uint, object>();

        foreach (var (id, sb) in portal.SkillTable.SkillBaseHash)
        {
            var skill = (Skill)id;

            skills[id] = new
            {
                name = string.IsNullOrEmpty(sb.Name) ? skill.ToSentence() : sb.Name,
                sentence = skill.ToSentence(),
                enumName = skill.ToString(),
                description = sb.Description,
                iconId = sb.IconId,
                trainedCost = sb.TrainedCost,
                specializedCost = sb.SpecializedCost,
                // The upgrade a player actually pays from Trained: SpecializedCost includes the
                // trained cost, so quoting it raw overstates what specializing costs them.
                upgradeCost = sb.UpgradeCostFromTrainedToSpecialized,
                category = sb.Category,          // 1 combat, 2 other, 3 magic
                usableUntrained = sb.MinLevel == 1,
                valid = SkillHelper.ValidSkills.Contains(skill),
                retired = SkillExtensions.RetiredWeapons.Contains(skill),
                formula = FormulaOf(sb.Formula),
            };
        }

        Write(Path.Combine(dataDir, "skills.json"), skills);
        Console.WriteLine($"    skills.json       {skills.Count} skills");

        // --- vitals.json: the attribute formula each vital is derived from -------------------
        //
        // 004 holds vitals at the same fraction of their ceiling as the attribute that governs
        // them - they earn nothing of their own. So MaxHealth is not a stored number the API can
        // just read: it is StartingValue + Ranks + formula(attributes), and the formula lives
        // here in the dat. Exporting it is what lets Python reproduce AttributeFormula.GetFormula
        // instead of hard-coding "endurance / 2".
        var vt = portal.SecondaryAttributeTable;

        Write(Path.Combine(dataDir, "vitals.json"), new
        {
            _source = "portal.dat 0x0E000003",
            maxHealth = FormulaOf(vt.MaxHealth.Formula),
            maxStamina = FormulaOf(vt.MaxStamina.Formula),
            maxMana = FormulaOf(vt.MaxMana.Formula),
        });

        Console.WriteLine("    vitals.json       health/stamina/mana attribute formulas");

        // --- spells.json: the names on an item's spellbook -----------------------------------
        //
        // 127 ask #1: an item's examine text lists the spells on it, and the shard stores only
        // their ids. The names live here in the dat, so they are exported alongside everything
        // else rather than fetched at request time.
        var spells = new SortedDictionary<uint, object>();

        foreach (var (id, sb) in portal.SpellTable.Spells)
        {
            spells[id] = new
            {
                name = sb.Name,
                desc = sb.Desc,
                school = sb.School.ToString(),
                // Power orders the tiers within a category (Strength Self I..VIII), which is what
                // lets the page show "Strength Self VI" rather than a bare id.
                power = sb.Power,
                mana = sb.BaseMana,
                icon = sb.Icon,
            };
        }

        Write(Path.Combine(dataDir, "spells.json"), spells);
        Console.WriteLine($"    spells.json       {spells.Count} spells");

        // --- enums.json: every id the shard stores that the payload must name ---------------
        //
        // The shard is all raw ints - heritage 10, gender 2, pk status 4, title 765. Every one of
        // those has to become a word somewhere, and reflecting the enums here means the web can
        // never disagree with the server about what a number means.
        Write(Path.Combine(dataDir, "enums.json"), new
        {
            skill = EnumMap<Skill>(),
            heritage = EnumMap<HeritageGroup>(),
            gender = EnumMap<Gender>(),
            playerKillerStatus = EnumMap<PlayerKillerStatus>(),
            skillAdvancementClass = EnumMap<SkillAdvancementClass>(),
            attribute = EnumMap<PropertyAttribute>(),
            attribute2nd = EnumMap<PropertyAttribute2nd>(),
            title = EnumMap<CharacterTitle>(),
            positionType = EnumMap<PositionType>(),
            weenieType = EnumMap<WeenieType>(),
            equipMask = EnumMap<EquipMask>(),
        });

        Console.WriteLine("    enums.json        skill/heritage/gender/pk/sac/attr/title/position/weenie/equip");

        // --- attribute icon map ------------------------------------------------------------
        // Written next to the tables so the API can serve icon paths without the front-end
        // needing to know the dat ids at all.
        Write(Path.Combine(dataDir, "icon-map.json"), new
        {
            ui = UiIcons.ToDictionary(u => u.Key, u => u.IconId),
            attribute = AttributeIcons.ToDictionary(a => a.Key, a => a.IconId),
            vital = VitalIcons.ToDictionary(v => v.Key, v => v.IconId),
            skill = portal.SkillTable.SkillBaseHash.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.IconId),
        });

        Console.WriteLine("    icon-map.json     attribute/vital/skill -> IconId");
    }

    /// <summary>
    /// A dat SkillFormula in the shape AttributeFormula.GetFormula consumes.
    ///
    /// X == 0 means "no attribute contribution at all" and is checked FIRST in the server - an
    /// exported formula that drops it would silently add an attribute bonus to skills that should
    /// have none. Z is the divisor; Attr1/Attr2 are PropertyAttribute values, with Attr2 == Undef
    /// meaning single-attribute.
    /// </summary>
    private static object FormulaOf(ACE.DatLoader.Entity.SkillFormula f) => new
    {
        attr1 = ((PropertyAttribute)f.Attr1).ToString(),
        attr1Id = f.Attr1,
        attr2 = ((PropertyAttribute)f.Attr2).ToString(),
        attr2Id = f.Attr2,
        divisor = f.Z,
        x = f.X,
    };

    /// <summary>
    /// value -> display name for an enum, keyed by the number the shard actually stores.
    ///
    /// Duplicate values are real in ACE's enums (aliases, and Undef/None pairs), and a plain
    /// ToDictionary throws on the second one. First name wins - they are declared canonical-first.
    /// </summary>
    private static SortedDictionary<long, object> EnumMap<T>() where T : struct, Enum
    {
        var map = new SortedDictionary<long, object>();

        foreach (var value in Enum.GetValues<T>())
        {
            var key = Convert.ToInt64(value, CultureInfo.InvariantCulture);

            if (map.ContainsKey(key))
                continue;

            var name = value.ToString();

            map[key] = new { name, label = Spaced(name) };
        }

        return map;
    }

    /// <summary>
    /// "MagicItemTinkering" -> "Magic Item Tinkering". Enum.ToString gives PascalCase and every
    /// one of these ends up on a page a player reads.
    /// </summary>
    private static string Spaced(string pascal)
    {
        if (string.IsNullOrEmpty(pascal))
            return pascal;

        var sb = new System.Text.StringBuilder(pascal.Length + 8);

        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];

            // Break before a capital, but not inside a run of them (PK, NPK, XP stay intact) and
            // not at the very start.
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(pascal[i - 1]))
                sb.Append(' ');

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static void Write(string path, object value)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    }

    /// <summary>
    /// Shadowgain 152: the computed appearance, in the same shape `sg-objdesc` dumps from the game
    /// server, so `tools/objdesc-diff.py` can compare them field for field.
    ///
    /// UNITS ARE NORMALISED TO EIGHTHS HERE. This exporter works in absolute colour indices and the
    /// server's ObjDesc carries eighths (the client multiplies back by 8). Comparing the two raw
    /// reports every single palette range as a mismatch, which buries any real one.
    /// </summary>
    private static void WriteObjDescJson(string path, PortalDatDatabase portal, uint bodySetupId,
                                         uint setupTableId, Appearance appearance,
                                         bool showHelm, bool showCloak)
    {
        // THE RESOLVED PART LIST, not the overrides. The server's ObjDesc names EVERY part of the
        // setup, including the ones nothing covered and the empty ones (0x010001EC appears 17 times
        // on a human setup). This exporter stores only the changes and lets ModelBuilder fall back
        // to the setup - the same final geometry by a different route. Emitting only the overrides
        // made 17 identical parts look like 17 disagreements and buried the real ones.
        var bodySetup = portal.ReadFromDat<SetupModel>(bodySetupId);
        var resolved = new List<object>();

        for (var i = 0; i < (bodySetup?.Parts.Count ?? 0); i++)
        {
            var id = appearance.PartSwaps.TryGetValue(i, out var swap) ? swap : bodySetup.Parts[i];
            resolved.Add(new { index = i, animationId = id });
        }

        var dump = new
        {
            source = "exporter",
            setupTableId,
            showHelm,
            showCloak,
            paletteId = appearance.BasePalette,
            subPalettes = appearance.SubPalettes
                .Select(p => new { subPaletteId = p.SubID, offset = p.Offset / 8, length = p.NumColors / 8 })
                .OrderBy(p => p.subPaletteId).ThenBy(p => p.offset)
                .ToList(),
            textureChanges = appearance.TextureSwaps
                .SelectMany(part => part.Value.Select(swap => new
                {
                    partIndex = part.Key, oldTexture = swap.Key, newTexture = swap.Value,
                }))
                .OrderBy(t => t.partIndex).ThenBy(t => t.oldTexture)
                .ToList(),
            animPartChanges = resolved,
        };

        Write(path, dump);

        Console.WriteLine($"    wrote {path} (objdesc: {dump.subPalettes.Count} palette ranges, "
            + $"{dump.textureChanges.Count} texture changes, {resolved.Count} parts)");
    }

    // ---- icons --------------------------------------------------------------------------

    private static void ExportIcons(PortalDatDatabase portal, string outDir, string itemIdFile)
    {
        var assets = Path.Combine(outDir, "assets", "icons");

        var skillDir = Path.Combine(assets, "skill");
        var attrDir = Path.Combine(assets, "attribute");
        var vitalDir = Path.Combine(assets, "vital");
        var itemDir = Path.Combine(assets, "item");

        foreach (var d in new[] { skillDir, attrDir, vitalDir, itemDir })
            Directory.CreateDirectory(d);

        // --- skill icons, named by SKILL ID not icon id -------------------------------------
        // Contract 2 says /assets/icons/skill/<skillId>.png, so the front-end can build the path
        // from the payload's skill key without a second lookup.
        var skillCount = 0;

        foreach (var (id, sb) in portal.SkillTable.SkillBaseHash)
        {
            if (SaveTexture(portal, sb.IconId, Path.Combine(skillDir, $"{id}.png")))
                skillCount++;
        }

        Console.WriteLine($"    skill icons       {skillCount}/{portal.SkillTable.SkillBaseHash.Count}");

        // --- attribute + vital icons, from the dat, named by key ----------------------------
        var uiDir = Path.Combine(assets, "ui");
        Directory.CreateDirectory(uiDir);

        var uiCount = UiIcons.Count(u => SaveTexture(portal, u.IconId, Path.Combine(uiDir, $"{u.Key}.png")));
        Console.WriteLine($"    ui icons          {uiCount}/{UiIcons.Length}");

        var attrCount = AttributeIcons.Count(a => SaveTexture(portal, a.IconId, Path.Combine(attrDir, $"{a.Key}.png")));
        var vitalCount = VitalIcons.Count(v => SaveTexture(portal, v.IconId, Path.Combine(vitalDir, $"{v.Key}.png")));

        Console.WriteLine($"    attribute icons   {attrCount}/{AttributeIcons.Length}");
        Console.WriteLine($"    vital icons       {vitalCount}/{VitalIcons.Length}");

        // A zero here means the palette did not resolve - which is the DatManager trap, not a
        // missing texture. Fail loudly rather than shipping blanks.
        if (attrCount < AttributeIcons.Length || vitalCount < VitalIcons.Length)
            Console.Error.WriteLine("!! some attribute/vital icons failed - is DatManager initialised?");

        // --- item icons ---------------------------------------------------------------------
        //
        // Named by DECIMAL IconId, per Contract 2, because that is the form the shard stores in
        // biota_properties_d_i_d and therefore the form the API already has in hand. Converting
        // to hex on both sides would be two chances to disagree about zero-padding for no gain.
        //
        // Two selection modes. --item-ids takes the exact set the shard references (generated by
        // web/tools/icon-ids.sh), which is the smaller and more honest export. Without it, every
        // 32x32 texture in the range goes out, so an item nobody owns yet still has an icon the
        // day someone loots it.
        var ids = itemIdFile != null ? ReadIdFile(itemIdFile) : AllIconSizedTextures(portal);

        Console.WriteLine($"    item icons        exporting {ids.Count:N0} "
                          + (itemIdFile != null ? $"from {itemIdFile}" : $"({IconSize}x{IconSize} textures in range)"));

        var itemCount = 0;
        var failed = 0;

        foreach (var id in ids)
        {
            if (SaveTexture(portal, id, Path.Combine(itemDir, $"{id}.png")))
                itemCount++;
            else
                failed++;
        }

        Console.WriteLine($"    item icons        {itemCount:N0} written, {failed:N0} skipped");

        // A format we cannot decode is reported BY NAME rather than folded into the skip count,
        // so a systematic gap (every DXT icon, say) is distinguishable from scattered bad records.
        if (TextureDecoder.Unsupported.Count > 0)
            Console.WriteLine("    unsupported       "
                              + string.Join(", ", TextureDecoder.Unsupported));

        // --- icon-set.json: the CACHE STAMP for the icon export ------------------------------
        //
        // The API versions every icon URL with `?v=<mtime>` so Caddy can serve them immutable
        // (see payload._icon_version). That stamp was taken from icon-map.json - which is written
        // by the TABLES export, not this one. So an icons-only run added thousands of files and
        // moved nothing: browsers that had already cached a 404 for a previously-missing icon kept
        // showing the fallback tile. Exactly the staleness the version stamp exists to prevent,
        // reintroduced through the back door.
        //
        // Writing a manifest here gives the icon export its own stamp. The contents are useful on
        // their own, but the mtime is the point.
        var dataDir = Path.Combine(outDir, "data");
        Directory.CreateDirectory(dataDir);

        Write(Path.Combine(dataDir, "icon-set.json"), new
        {
            items = itemCount,
            skipped = failed,
            source = itemIdFile != null ? "shard id list" : $"all {IconSize}x{IconSize} in range",
            unsupported = TextureDecoder.Unsupported.Select(u => u.ToString()).ToArray(),
        });

        Console.WriteLine("    icon-set.json     written (cache stamp for the icon export)");

        // --- placeholder ---------------------------------------------------------------------
        // Contract 2 names /assets/icons/placeholder.png as the fallback for anything missing.
        //
        // WINDOWS ONLY, and deliberately left that way. It is drawn with System.Drawing, which is
        // the dependency the rest of this file has just been freed from - but unlike the icons it
        // is a single static design asset that has not changed since it was written and is already
        // deployed. Porting the dashed rounded outline to the hand-rolled PNG writer would be real
        // rasterisation work in exchange for nothing. Skipping it lets the icon sweep - the part
        // that genuinely needs to be repeatable - run on the droplet.
        if (OperatingSystem.IsWindows())
        {
            WritePlaceholder(Path.Combine(assets, "placeholder.png"));
            Console.WriteLine("    placeholder.png   written");
        }
        else
        {
            Console.WriteLine("    placeholder.png   skipped (needs Windows; already deployed)");
        }
    }

    /// <summary>
    /// Print how many textures exist at each dimension, and write the id list per size so a
    /// promising size can be exported and eyeballed without re-scanning the whole dat.
    /// </summary>
    private static void ReportSizes(PortalDatDatabase portal, string outDir)
    {
        var bySize = new SortedDictionary<(int W, int H), List<uint>>();

        foreach (var id in portal.AllFiles.Keys)
        {
            if (id < TextureRangeStart || id > TextureRangeEnd)
                continue;

            try
            {
                var tex = portal.ReadFromDat<Texture>(id);

                if (tex == null || tex.Length == 0)
                    continue;

                var key = (tex.Width, tex.Height);

                if (!bySize.TryGetValue(key, out var list))
                    bySize[key] = list = new List<uint>();

                list.Add(id);
            }
            catch { }
        }

        Directory.CreateDirectory(outDir);

        foreach (var (size, ids) in bySize.OrderByDescending(kv => kv.Value.Count))
        {
            Console.WriteLine($"    {size.W,4} x {size.H,-4} {ids.Count,7:N0}");
            ids.Sort();
            File.WriteAllLines(Path.Combine(outDir, $"ids-{size.W}x{size.H}.txt"), ids.Select(i => i.ToString()));
        }
    }

    private static uint ParseId(string s) =>
        s == null ? 0
        : s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToUInt32(s[2..], 16)
            : uint.Parse(s);

    /// <summary>
    /// 130 Stage 1: a character's body, head and chosen palettes, out as one .glb.
    ///
    /// The palette map is keyed by the palette a SURFACE names, so an override only fires where
    /// the body actually uses that palette. That is why skin/hair/eyes are passed as the values
    /// the character picked rather than being applied blindly to every texture - applying a hair
    /// palette to skin is precisely the kind of mistake that looks plausible in code and
    /// grotesque on screen.
    /// </summary>
    /// <summary>
    /// 130: assemble one character and write a .glb.
    ///
    /// The appearance is accumulated in the order the game builds it - the body's own ObjDesc
    /// first, then the character's chosen skin/hair/eye palettes over it. Stage 2 adds worn items
    /// to the end of that same list; nothing else about this changes.
    /// </summary>
    private static void BuildModel(PortalDatDatabase portal, string outDir,
        string heritage, string gender, string skin, string hair, string eyes, string head,
        List<string> items, string charSetup = null, bool showHelm = true, bool showCloak = true,
        string objDescOut = null, List<string> headTextures = null)
    {
        Directory.CreateDirectory(outDir);

        var hg = ParseId(heritage);
        var sx = (int)ParseId(gender ?? "1");

        if (!portal.CharGen.HeritageGroups.TryGetValue(hg, out var group)
            || !group.Genders.TryGetValue(sx, out var sex))
        {
            Console.Error.WriteLine($"!! no chargen entry for heritage {hg} gender {sx}");
            return;
        }

        Console.WriteLine($"==> {group.Name} / {sex.Name}");
        Console.WriteLine($"    setup 0x{sex.SetupID:X8}  basePalette 0x{sex.BasePalette:X8}  skinPalSet 0x{sex.SkinPalSet:X8}");

        if (System.Environment.GetEnvironmentVariable("SG_MODEL_DEBUG") == "1")
        {
            var od = sex.BaseObjDesc;
            Console.WriteLine($"    DEBUG BaseObjDesc: paletteID=0x{od.PaletteID:X8} subPalettes={od.SubPalettes.Count} texChanges={od.TextureChanges.Count} partChanges={od.AnimPartChanges.Count}");
            foreach (var sp in od.SubPalettes)
                Console.WriteLine($"      DEBUG sub 0x{sp.SubID:X8} offset={sp.Offset} numColors={sp.NumColors}");
            foreach (var pc in od.AnimPartChanges)
                Console.WriteLine($"      DEBUG part[{pc.PartIndex}] -> 0x{pc.PartID:X8}");
            var ps = portal.ReadFromDat<ACE.DatLoader.FileTypes.PaletteSet>(sex.SkinPalSet);
            Console.WriteLine($"      DEBUG skinPalSet has {ps?.PaletteList.Count} palettes: " +
                string.Join(", ", (ps?.PaletteList ?? new List<uint>()).Take(8).Select(x => $"0x{x:X8}")));
            Console.WriteLine($"      DEBUG hairColors: " + string.Join(", ", sex.HairColorList.Take(8).Select(x => $"0x{x:X8}")));
            Console.WriteLine($"      DEBUG eyeColors: " + string.Join(", ", sex.EyeColorList.Take(8).Select(x => $"0x{x:X8}")));
        }

        if (System.Environment.GetEnvironmentVariable("SG_PAL_DUMP") == "1")
        {
            foreach (var (label, pid) in new[] { ("base", sex.BasePalette), ("skin", ParseId(skin)), ("hair", ParseId(hair)), ("eyes", ParseId(eyes)) })
            {
                if (pid == 0) continue;
                var pal = portal.ReadFromDat<ACE.DatLoader.FileTypes.Palette>(pid);
                var cols = pal?.Colors ?? new List<uint>();
                // Where are this palette's NON-BLACK colours? A character palette is a full
                // 2048-entry table that is meaningful only over the slice it applies to.
                var first = -1; var last = -1;
                for (var k = 0; k < cols.Count; k++)
                    if ((cols[k] & 0x00FFFFFF) != 0) { if (first < 0) first = k; last = k; }

                Console.WriteLine($"    DUMP {label} 0x{pid:X8}: {cols.Count} colours, non-black [{first}..{last}], "
                    + $"sample@{Math.Max(first,0)}: " + string.Join(" ", cols.Skip(Math.Max(first,0)).Take(6).Select(c => $"{c:X8}")));
            }
        }

        var appearance = new Appearance();

        // The body's own description: its base palette, and any part/texture it differs from the
        // raw setup by.
        appearance.SetBasePalette(sex.BasePalette);
        appearance.Apply(sex.BaseObjDesc);

        // The character's choices, as SUB-PALETTE RANGES over that base. Ranges rather than whole
        // palettes because a body texture is palettised: skin, hair and eyes each own a slice of
        // the same colour table, which is why swapping the entire palette turns a character into
        // one flat colour.
        //
        // The offsets below are the retail character-generation ranges. They are stated here
        // rather than derived because the derivation lives in the client's chargen code, not in
        // the dat - and being explicit means a wrong one is a visible, fixable number.
        // Offsets and lengths ported verbatim from ACE's own WorldObject.AddBaseModelData, which
        // is the server's implementation of this exact calculation - skin 0x00/0x18, hair
        // 0x18/0x08, eyes 0x20/0x08. Not guessed.
        AddRange(appearance, skin, 0x00, 0x18, "skin");
        AddRange(appearance, hair, 0x18, 0x08, "hair");
        AddRange(appearance, eyes, 0x20, 0x08, "eyes");

        // THE HEAD IS PART 0x10, NOT A LOOSE OBJECT. AddBaseModelData adds it as an AnimPartChange
        // at index 0x10; attaching it separately at the origin left a head-shaped spike under the
        // model's feet and stretched its bounding box from 1.82 to 2.42.
        if (head != null)
        {
            var headId = ParseId(head);

            if (headId != 0)
            {
                appearance.PartSwaps[0x10] = headId;
                Console.WriteLine($"    head    0x{headId:X8} as part 0x10");
            }
        }

        // THE FACE. Eyes, nose, mouth and hair are TEXTURE SWAPS on the head part, not geometry and
        // not palettes - AddBaseModelData adds all four as `oldTexture -> newTexture` on part 0x10.
        //
        // Omitting them does not fail or look broken; it silently draws the head model's DEFAULT
        // face, so every character of the same heritage and head shape shares one face. Found by
        // diffing against the server's own ObjDesc, which listed four part-16 texture changes this
        // exporter had none of (152).
        foreach (var spec in headTextures ?? new List<string>())
        {
            var parts = spec.Split(':');

            if (parts.Length != 2)
            {
                Console.Error.WriteLine($"!! --head-tex wants old:new, got '{spec}'");
                continue;
            }

            var oldTex = ParseId(parts[0]);
            var newTex = ParseId(parts[1]);

            if (oldTex == 0 || newTex == 0)
                continue;

            if (!appearance.TextureSwaps.TryGetValue(0x10, out var map))
                appearance.TextureSwaps[0x10] = map = new Dictionary<uint, uint>();

            map[oldTex] = newTex;

            Console.WriteLine($"    face    0x{oldTex:X8} -> 0x{newTex:X8} on part 0x10");
        }

        // WORN ITEMS, LAST AND IN THE SERVER'S ORDER.
        //
        // Order is the whole of layering: a later item's part swaps overwrite an earlier one's. The
        // sequence, the setup fallback and the helm/cloak suppression are all ported in ObjDescPort
        // (152) rather than approximated here - see that file for why the previous approximation
        // was wrong rather than merely rough.
        //
        // THE SETUP IS THE CHARACTER'S OWN, not the heritage default. CharGen's SetupID is the
        // right body to BUILD, but the Barber can leave a character wearing a different setup id,
        // and every clothing lookup is keyed on that one.
        var setupTableId = charSetup != null ? ParseId(charSetup) : sex.SetupID;

        if (setupTableId != sex.SetupID)
            Console.WriteLine($"    setup 0x{setupTableId:X8} (chargen default is 0x{sex.SetupID:X8})");

        if (!showHelm) Console.WriteLine("    ShowYourHelmOrHeadGear is OFF");
        if (!showCloak) Console.WriteLine("    ShowYourCloak is OFF");

        var worn = items.Select(ParseItem).ToList();

        foreach (var spec in ObjDescPort.Order(portal, worn))
            Console.WriteLine($"    item 0x{spec.ClothingBase:X8} template={spec.PaletteTemplate} "
                + $"shade={spec.Shade:0.###} priority={spec.ClothingPriority} type={spec.ItemType} "
                + $"wielded={spec.Wielded} topLayer={(spec.TopLayerPriority?.ToString() ?? "unset")}");

        ObjDescPort.ApplyAll(portal, appearance, setupTableId, worn, showHelm, showCloak, Console.WriteLine);

        if (objDescOut != null)
            WriteObjDescJson(objDescOut, portal, sex.SetupID, setupTableId, appearance, showHelm, showCloak);

        // The BODY is still built from the chargen setup: that is the mesh this character is, and
        // the remap above only ever affects which clothing entries are looked up.
        var prims = ModelBuilder.Build(portal, sex.SetupID, appearance, Console.WriteLine);

        var tris = prims.Sum(p => p.Indices.Count) / 3;
        var verts = prims.Sum(p => p.Positions.Count);
        var textured = prims.Count(p => p.TexturePng != null);

        Console.WriteLine($"    {prims.Count} primitives, {verts:N0} vertices, {tris:N0} triangles, {textured} textured");

        if (TextureDecoder.Unsupported.Count > 0)
            Console.Error.WriteLine("!! unsupported texture formats seen: " + string.Join(", ", TextureDecoder.Unsupported));

        var path = Path.Combine(outDir, "character.glb");
        Gltf.Write(path, prims);

        Console.WriteLine($"    wrote {path} ({new FileInfo(path).Length:N0} bytes)");
    }

    /// <summary>
    /// "clothingBase:paletteTemplate:shade:priority:itemType:wielded:topLayer:setup" — every field
    /// after the first is optional, so the four-field form older callers pass still parses.
    ///
    /// The last four exist because the server's layering consults them (152), and the API passes
    /// all eight.
    ///
    /// A four-field caller supplies no slot information, and the item is then flagged as such
    /// rather than given an invented default — see WornItem.HasSlotInfo for why a plausible
    /// default is actively worse than an admitted gap here.
    ///
    /// `topLayer` is TRI-STATE: "1" true, "0" false, empty unset. Unset is not the same as false —
    /// it sorts between the two, and collapsing it to a bool moves every unmarked item under every
    /// explicitly-bottom one.
    /// </summary>
    private static WornItem ParseItem(string spec)
    {
        var parts = spec.Split(':');

        string Field(int n) => parts.Length > n && parts[n].Length > 0 ? parts[n] : null;

        // The slot fields travel together: type without a wielded location cannot be bucketed
        // either, so both must be present before the server's layering can be applied.
        var hasSlotInfo = Field(4) != null && Field(5) != null;

        return new WornItem(
            ClothingBase: ParseId(parts[0]),
            PaletteTemplate: Field(1) is { } t ? int.Parse(t) : 0,
            Shade: Field(2) is { } s ? double.Parse(s, CultureInfo.InvariantCulture) : 0,
            ClothingPriority: Field(3) is { } p ? int.Parse(p) : 0,
            ItemType: Field(4) is { } it ? (ACE.Entity.Enum.ItemType)int.Parse(it) : 0,
            Wielded: Field(5) is { } w ? (ACE.Entity.Enum.EquipMask)uint.Parse(w) : 0,
            TopLayerPriority: Field(6) is { } tl ? tl == "1" : null,
            SetupId: Field(7) is { } su ? ParseId(su) : 0,
            Label: $"0x{ParseId(parts[0]):X8}",
            HasSlotInfo: hasSlotInfo);
    }

    private static void AddRange(Appearance appearance, string paletteId, uint offset, uint count, string what)
    {
        if (paletteId == null)
            return;

        var id = ParseId(paletteId);

        if (id == 0)
            return;

        // Offset and NumColors are stored /8 in the dat and scaled back up by DatLoader's
        // unpacker, so the values handed in here are in the SAME units it produces.
        appearance.AddSubPalette(id, offset * 8, count * 8);

        Console.WriteLine($"    {what,-5} palette 0x{id:X8} over [{offset * 8}..{offset * 8 + count * 8})");
    }

    private static List<uint> ReadIdFile(string path)
    {
        var ids = new List<uint>();

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // Accept decimal (what the shard stores) or 0x-prefixed hex (what the dat docs use).
            var ok = line.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? uint.TryParse(line.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)
                : uint.TryParse(line, out id);

            if (ok)
                ids.Add(id);
            else
                Console.Error.WriteLine($"    !! unparseable id: {line}");
        }

        return ids;
    }

    /// <summary>
    /// Every texture in the 0x06 range whose dimensions say "icon". Reading each record's header
    /// is the only way to know its size, so this pass genuinely decodes the whole range once -
    /// about 30 seconds on a warm cache, and only on a full export.
    /// </summary>
    private static List<uint> AllIconSizedTextures(PortalDatDatabase portal)
    {
        var ids = new List<uint>();

        foreach (var id in portal.AllFiles.Keys)
        {
            if (id < TextureRangeStart || id > TextureRangeEnd)
                continue;

            try
            {
                var tex = portal.ReadFromDat<Texture>(id);

                if (tex != null && tex.Width == IconSize && tex.Height == IconSize && tex.Length > 0)
                    ids.Add(id);
            }
            catch
            {
                // A handful of records in the range are not textures at all. Silently skipping
                // them here is right: the alternative is 200 lines of noise on every run, which
                // would bury the failures that DO matter (reported per-id by SaveTexture).
            }
        }

        ids.Sort();
        return ids;
    }

    /// <summary>
    /// Decode one texture to PNG. Returns false rather than throwing so one bad record cannot end
    /// a 20,000-icon export - the caller counts the misses and prints a total.
    /// </summary>
    private static bool SaveTexture(PortalDatDatabase portal, uint iconId, string path)
    {
        if (iconId == 0)
            return false;

        try
        {
            var tex = portal.ReadFromDat<Texture>(iconId);

            if (tex == null || tex.Length == 0)
                return false;

            // Decoded with our own TextureDecoder rather than Texture.GetBitmap(), which goes via
            // System.Drawing -> GDI+ and therefore only runs on Windows. That is precisely what
            // made the icon set a Windows-only SNAPSHOT: it had to be exported on Chris's machine
            // and rsynced up, so anything looted afterwards had no icon until someone remembered
            // to re-run it. Hyssop - created the same day it was reported missing - is the case
            // that exposed it. Decoding here runs on the droplet, so the sweep is repeatable in
            // place and the icon set can simply cover the whole range once.
            var decoded = TextureDecoder.Decode(portal, tex, null);

            if (decoded == null)
                return false;

            // Decode hands back the dat's own bytes untouched for formats that are already
            // complete image files, so honour the extension it actually produced.
            var target = decoded.MimeType == "image/jpeg"
                ? Path.ChangeExtension(path, ".jpg")
                : path;

            File.WriteAllBytes(target, decoded.Bytes);

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"    !! 0x{iconId:X8}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Draw one attribute/vital tile: a dark rounded square, a tinted ring, and a letter.
    ///
    /// Rendered at 4x and downsampled, because System.Drawing's text hinting at 32px produces a
    /// letter with visibly ragged edges next to the dat's own anti-aliased skill icons - and
    /// these two sets sit in the same column on the page.
    /// </summary>
    private static void WriteTile(string path, string letter, string tintHex)
    {
        const int scale = 4;
        const int size = IconSize * scale;

        var tint = System.Drawing.ColorTranslator.FromHtml(tintHex);

        using var big = new System.Drawing.Bitmap(size, size);
        using (var g = System.Drawing.Graphics.FromImage(big))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            var inset = 2 * scale;
            var rect = new System.Drawing.Rectangle(inset, inset, size - inset * 2, size - inset * 2);

            using (var path2 = RoundedRect(rect, 7 * scale))
            {
                // Dark body first so the tint reads as a rim light rather than as a flat swatch -
                // a solid tinted square would fight the dat icons it sits beside.
                using var body = new System.Drawing.Drawing2D.LinearGradientBrush(
                    rect,
                    Blend(tint, System.Drawing.Color.FromArgb(18, 27, 39), 0.72f),
                    Blend(tint, System.Drawing.Color.FromArgb(9, 12, 18), 0.88f),
                    System.Drawing.Drawing2D.LinearGradientMode.Vertical);

                g.FillPath(body, path2);

                using var rim = new System.Drawing.Pen(System.Drawing.Color.FromArgb(190, tint), 1.5f * scale);
                g.DrawPath(rim, path2);
            }

            // Two-letter labels need a smaller face, or they touch the rim on a 32px tile.
            var pointSize = (letter.Length > 1 ? 11f : 15f) * scale;

            using var font = new System.Drawing.Font("Segoe UI", pointSize,
                System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);

            using var format = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center,
            };

            using var ink = new System.Drawing.SolidBrush(Blend(tint, System.Drawing.Color.White, 0.45f));
            g.DrawString(letter, font, ink, rect, format);
        }

        using var small = new System.Drawing.Bitmap(IconSize, IconSize);
        using (var g = System.Drawing.Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.Clear(System.Drawing.Color.Transparent);
            g.DrawImage(big, 0, 0, IconSize, IconSize);
        }

        small.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(System.Drawing.Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }

    /// <summary>Mix `a` toward `b` by `t` (0 = all a, 1 = all b).</summary>
    private static System.Drawing.Color Blend(System.Drawing.Color a, System.Drawing.Color b, float t) =>
        System.Drawing.Color.FromArgb(
            255,
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));

    /// <summary>
    /// The missing-icon tile: a flat dark square matching the mockup's empty inventory cell, so a
    /// gap in the export reads as "no icon" rather than as a broken image.
    /// </summary>
    private static void WritePlaceholder(string path)
    {
        using var bmp = new System.Drawing.Bitmap(IconSize, IconSize);
        using var g = System.Drawing.Graphics.FromImage(bmp);

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Transparent body with only a faint dashed outline. A FILLED tile - which the first
        // version drew - reads as an item you cannot identify; an outline reads as an empty slot,
        // which is what a missing icon actually means.
        g.Clear(System.Drawing.Color.Transparent);

        using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(70, 120, 180, 190), 1f)
        {
            DashStyle = System.Drawing.Drawing2D.DashStyle.Dash,
        };

        using var outline = RoundedRect(new System.Drawing.Rectangle(2, 2, IconSize - 5, IconSize - 5), 6);
        g.DrawPath(pen, outline);

        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
