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
    /// ATTRIBUTES AND VITALS HAVE NO ICONS IN THE DAT, so these are drawn rather than extracted.
    ///
    /// This was checked, not assumed. Skills have icons because the dat's SkillTable carries an
    /// IconId per skill; there is no equivalent table for attributes, and the retail client's
    /// attribute panel labels its rows with TEXT. A sweep of the 0x060011xx UI band turned up
    /// window chrome - coloured fill squares, a green arrow, a red no-entry glyph - and nothing
    /// resembling a Strength or Focus icon.
    ///
    /// So the exporter generates them, in the mockup's own visual language: a rounded tile
    /// tinted by category (physical / defensive / magical) carrying the attribute's initial,
    /// which is exactly what `iconTile()` draws in character-sheet-mockup.html. The front-end
    /// needs no change - Contract 2's paths are filled with real PNGs either way - and if the
    /// genuine icons ever turn up, this table becomes a list of ids and the drawing goes away.
    ///
    /// Colours match landing/character-sheet-mockup.html's PALETTE so the generated tiles sit
    /// beside the real dat skill icons without looking like a different site.
    /// </summary>
    private static readonly (string Key, string Letter, string Tint)[] AttributeTiles =
    {
        // Two letters where one would be ambiguous. Strength, Self and Stamina all start with
        // S, and three identical tiles in one column is not an icon set - it is a puzzle.
        ("strength",     "St", "#c98a4b"),   // phys
        ("endurance",    "E",  "#57d98a"),   // def
        ("quickness",    "Q",  "#c98a4b"),   // phys
        ("coordination", "C",  "#c98a4b"),   // phys
        ("focus",        "F",  "#6ea8ff"),   // magic
        ("self",         "Sf", "#6ea8ff"),   // magic
    };

    private static readonly (string Key, string Letter, string Tint)[] VitalTiles =
    {
        ("health",  "H",  "#57d98a"),
        ("stamina", "St", "#c98a4b"),
        ("mana",    "M",  "#6ea8ff"),
    };

    public static int Main(string[] args)
    {
        string datDir = null, outDir = null, itemIdFile = null;
        bool doIcons = false, doTables = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dat": datDir = Next(args, ref i); break;
                case "--out": outDir = Next(args, ref i); break;
                case "--item-ids": itemIdFile = Next(args, ref i); break;
                case "--icons": doIcons = true; break;
                case "--tables": doTables = true; break;
                default:
                    Console.Error.WriteLine($"unknown argument: {args[i]}");
                    return Usage();
            }
        }

        if (datDir == null || outDir == null)
            return Usage();

        // Neither flag means both - the common case is a full refresh.
        if (!doIcons && !doTables)
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

        // keepOpen: true - the icon pass reads tens of thousands of records, and reopening the
        // file handle per read turns a 30-second export into a multi-minute one.
        PortalDatDatabase portal;

        try
        {
            portal = new PortalDatDatabase(portalPath, keepOpen: true);
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

        // AddRetiredSkills is what DatManager.Initialize does for the server, and the reason is
        // the same here: the dat's own SkillTable predates the retired-skill consolidation, so
        // without this call Axe/Bow/Dagger/... resolve to nothing and every character who still
        // carries frozen ranks in one shows a blank row.
        portal.SkillTable.AddRetiredSkills();

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
            attribute = AttributeTiles.ToDictionary(a => a.Key, a => $"/assets/icons/attribute/{a.Key}.png"),
            vital = VitalTiles.ToDictionary(v => v.Key, v => $"/assets/icons/vital/{v.Key}.png"),
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

        // --- attribute + vital tiles, GENERATED (see AttributeTiles) -------------------------
        foreach (var (key, letter, tint) in AttributeTiles)
            WriteTile(Path.Combine(attrDir, $"{key}.png"), letter, tint);

        foreach (var (key, letter, tint) in VitalTiles)
            WriteTile(Path.Combine(vitalDir, $"{key}.png"), letter, tint);

        Console.WriteLine($"    attribute tiles   {AttributeTiles.Length} generated (no dat icons exist)");
        Console.WriteLine($"    vital tiles       {VitalTiles.Length} generated");

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

        // --- placeholder ---------------------------------------------------------------------
        // Contract 2 names /assets/icons/placeholder.png as the fallback for anything missing.
        // Generating it here rather than committing a binary means the fallback can never drift
        // out of size with the real icons.
        WritePlaceholder(Path.Combine(assets, "placeholder.png"));
        Console.WriteLine("    placeholder.png   written");
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

            using var bmp = tex.GetBitmap();

            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);

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
