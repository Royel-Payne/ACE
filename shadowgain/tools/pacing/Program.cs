// Shadowgain pacing simulator.
//
// Answers "where does a character actually end up?" without anyone grinding for
// a year. Runs the real award formula over the real cost tables and the real
// creature distribution, and reports projected skills/attributes at checkpoints.
//
// WHAT IS REAL HERE (not modelled, not guessed):
//   - skill / attribute / vital / character-level XP tables, extracted once from
//     client_portal.dat into tables.tsv (so this runs while the client is open)
//   - the creature difficulty-vs-reward distribution, exported from ace_world
//     (mobs.tsv: 5,573 XP-bearing creatures)
//   - the award formula, mirrored from Proficiency.OnSuccessUse
//   - the 013 stretched attribute curve, mirrored from Player_Attributes
//
// WHAT IS AN INPUT (i.e. a guess you can change):
//   - LANDED hits per kill, hits taken per kill, kills per hour
//     (measure the first with tools/hitrate.py against ACBridge telemetry -
//      two independent methods put it near 3, not the 12 originally assumed)
//   - the content policy: how far above your own skill you choose to fight
//
// Content is selected by DEFENCE TIER, never by level. Chris: "levels are not
// always a measure of difficulty... level 80 mobs aren't always equal, and XP
// per mob increases as you access higher tier areas." The data agrees - within
// one level band melee defence varies up to 850x and XP by five orders of
// magnitude, while bucketing by defence makes median XP rise cleanly and
// monotonically. So the character's capability picks the tier, and the tier
// carries its own reward.

using System.Globalization;

var ci = CultureInfo.InvariantCulture;

// ---------------------------------------------------------------- inputs ----

var policy = Env("SG_POLICY", "matched");          // matched | aggressive | farming
var swingsPerKill = double.Parse(Env("SG_SWINGS", "3"), ci);   // LANDED hits, not swings attempted
var hitsTakenPerKill = double.Parse(Env("SG_HITS", "4"), ci);
var killsPerHour = double.Parse(Env("SG_KPH", "40"), ci);
var skillMult = double.Parse(Env("SG_SKILL_MULT", "1.0"), ci);
var attrMult = double.Parse(Env("SG_ATTR_MULT", "1.0"), ci);
var maxLevel = int.Parse(Env("SG_MAX_LEVEL", "200"), ci);
var xpMult   = double.Parse(Env("SG_XP_MULT", "1.0"), ci);   // 021: scales kill XP (ACE xp_modifier)

// how hard you choose to fight, relative to your own weapon skill
var policyRatio = policy switch
{
    "aggressive" => 1.50,
    "farming" => 0.60,
    _ => 1.00,
};

// dial defaults mirrored from PropertyManager
const double DIFF_FLOOR = 0.05;
const double DIFF_CAP = 2.00;
const double ATTR_OVERLAP = 0.25;
const int OVERCAP_RATIO_WINDOW = 20;    // matches Player_Skills.OvercapRatioWindow
const uint ATTR_START = 10;
const int ATTR_MAX_VALUE = 290;

// Tables come from tables.tsv, extracted once from client_portal.dat, so this
// tool needs neither the dat nor the server - and keeps working while the AC
// client holds the dat open. Regenerate with xpdump if the dat ever changes.
var tables = LoadTables(Find("tables.tsv"));
var trainedTable = tables["trained"];
var attrTable = tables["attribute"];
var vitalTable = tables["vital"];
var levelTable = tables["level"];

var attrTableMax = attrTable.Count - 1;
var attrMaxRanks = ATTR_MAX_VALUE - (int)ATTR_START;
var skillTableMax = trainedTable.Count - 1;

// Shadowgain 109b: past the table the curve is the TABLE'S OWN final step, compounding at the
// table's own ratio - so the model has no free parameters here either. Was a flat 1,000,000,
// which was a workaround for the old uint wire ceiling and made rank 209 cost ~300x LESS than
// rank 208; modelling that today would overstate high-rank progress by orders of magnitude.
var overcapLastStep = trainedTable[skillTableMax] - trainedTable[skillTableMax - 1];
var overcapRatio = Math.Pow(
    overcapLastStep / (trainedTable[skillTableMax - OVERCAP_RATIO_WINDOW] - trainedTable[skillTableMax - OVERCAP_RATIO_WINDOW - 1]),
    1.0 / OVERCAP_RATIO_WINDOW);

// ------------------------------------------------------- creature tiers ----

// (melee defence, xp) pairs, bucketed so a lookup by defence returns what that
// tier of content actually pays.
var mobs = new List<(int def, long xp)>();
foreach (var line in File.ReadLines(Find("mobs.tsv")))
{
    var p = line.Split('\t');
    if (p.Length < 7) continue;
    if (!int.TryParse(p[3], out var xp) || !int.TryParse(p[4], out var def)) continue;
    if (def <= 0 || xp <= 0) continue;
    mobs.Add((def, xp));
}
mobs.Sort((a, b) => a.def.CompareTo(b.def));

if (mobs.Count == 0) { Console.Error.WriteLine("no creature data - is mobs.tsv present?"); return 1; }

// Build a tier curve: median XP per defence bin, then forced non-decreasing.
//
// The monotonic pass matters. Raw medians spike in the low bins because a
// creature's authored MELEE defence is a poor proxy for its tier - casters and
// bosses carry modest melee defence but pay enormous XP, and a handful of them
// dragged the low-tier median to 150,000, which made the first ten levels take
// eight kills. Taking a running maximum expresses the real constraint: a harder
// tier never pays less than an easier one.
const int BIN = 10;
var binMedian = new Dictionary<int, long>();
var byBin = new Dictionary<int, List<long>>();
foreach (var m in mobs)
{
    var b = m.def / BIN;
    if (!byBin.TryGetValue(b, out var l)) byBin[b] = l = new List<long>();
    l.Add(m.xp);
}
var maxBin = byBin.Keys.Max();
long running = 0;
for (var b = 0; b <= maxBin; b++)
{
    if (byBin.TryGetValue(b, out var l) && l.Count >= 3)
    {
        l.Sort();
        running = Math.Max(running, l[l.Count / 2]);
    }
    binMedian[b] = running;
}

// linear interpolation between bin centres, so the curve is smooth rather than stepped
long XpForDefence(double defence)
{
    var pos = defence / BIN - 0.5;
    var lo = (int)Math.Floor(pos);
    var frac = pos - lo;
    var a = binMedian[Math.Clamp(lo, 0, maxBin)];
    var b = binMedian[Math.Clamp(lo + 1, 0, maxBin)];
    return (long)Math.Max(1, a + frac * (b - a));
}

var maxMobDefence = mobs[^1].def;

// ------------------------------------------------------------ mirrors ------

// Proficiency.OnSuccessUse: award = difficulty x clamp(difficulty/Base, floor, cap) x multiplier
double Award(double difficulty, double baseValue, double mult)
{
    if (difficulty <= 0) return 0;
    var ratio = baseValue > 0 ? difficulty / baseValue : DIFF_CAP;
    var factor = Math.Clamp(ratio, DIFF_FLOOR, DIFF_CAP);
    return Math.Max(1.0, difficulty * factor * mult);
}

// Player_Skills.CalcSkillRankUncapped - the table, continued at its own slope (109b)
int SkillRank(double xp)
{
    if (xp >= trainedTable[skillTableMax])
    {
        var firstStep = overcapLastStep * overcapRatio;
        var extra = xp - trainedTable[skillTableMax];
        return skillTableMax + (int)(Math.Log(1.0 + extra * (overcapRatio - 1.0) / firstStep) / Math.Log(overcapRatio));
    }
    var lo = 0; var hi = skillTableMax;
    while (lo < hi) { var mid = (lo + hi + 1) / 2; if (trainedTable[mid] <= xp) lo = mid; else hi = mid - 1; }
    return lo;
}

// Player_Attributes.AttributeRankCost - the 013 stretched curve
double AttrCost(int rank)
{
    if (rank <= 0) return 0;
    if (rank >= attrMaxRanks) return attrTable[attrTableMax];
    var t = (double)rank * attrTableMax / attrMaxRanks;
    var lower = (int)Math.Floor(t);
    if (lower >= attrTableMax) return attrTable[attrTableMax];
    return attrTable[lower] + (t - lower) * (attrTable[lower + 1] - attrTable[lower]);
}

int AttrRank(double xp)
{
    var lo = 0; var hi = attrMaxRanks;
    while (lo < hi) { var mid = (lo + hi + 1) / 2; if (AttrCost(mid) <= xp) lo = mid; else hi = mid - 1; }
    return lo;
}

int LevelFor(double totalXp)
{
    var lo = 1;
    for (var i = 1; i < levelTable.Count; i++) { if (levelTable[i] <= totalXp) lo = i; else break; }
    return lo;
}

// ------------------------------------------------------------- simulate ----

double weaponXp = 0, defenceXp = 0;
double strXp = 0, coordXp = 0, endXp = 0;
double totalXp = 0, kills = 0;

var checkpoints = new[] { 5, 10, 20, 30, 50, 75, 100, 126, 150, 180, 200, 226, 250, 275 }
    .Where(l => l <= maxLevel).ToArray();
var nextCp = 0;

var rows = new List<string>();

// starting state: all skills Trained, all attributes 10 (013)
while (nextCp < checkpoints.Length)
{
    var weaponBase = SkillRank(weaponXp) + AttributeBase(strXp, coordXp);
    var defenceBase = SkillRank(defenceXp) + AttributeBase(coordXp, coordXp);

    // choose content: defence tier scaled off current capability, capped by what exists
    var contentDefence = Math.Min(Math.Max(5.0, weaponBase * policyRatio), maxMobDefence);
    var killXp = XpForDefence(contentDefence);

    // skills
    weaponXp += Award(contentDefence, weaponBase, skillMult) * swingsPerKill;
    defenceXp += Award(contentDefence, defenceBase, skillMult) * hitsTakenPerKill;

    // attributes - Strength primary on a landed hit, Coordination as the 0.25 overlap,
    // Endurance from being hit (010 exertion not modelled; it only adds)
    strXp += Award(contentDefence, 10 + AttrRank(strXp), attrMult) * swingsPerKill;
    coordXp += Award(contentDefence, 10 + AttrRank(coordXp), attrMult) * swingsPerKill * ATTR_OVERLAP;
    endXp += Award(contentDefence, 10 + AttrRank(endXp), attrMult) * hitsTakenPerKill;

    totalXp += killXp * xpMult;
    kills++;

    var level = LevelFor(totalXp);
    while (nextCp < checkpoints.Length && level >= checkpoints[nextCp])
    {
        rows.Add(Row(checkpoints[nextCp], kills, totalXp, contentDefence, killXp,
                     weaponXp, defenceXp, strXp, coordXp, endXp, killsPerHour));
        nextCp++;
    }

    if (kills > 80_000_000) break;   // runaway guard
}

// ---------------------------------------------------------------- output ----

Console.WriteLine($"# Shadowgain pacing projection");
Console.WriteLine();
Console.WriteLine($"policy **{policy}** (fight content at {policyRatio:0.00}x your weapon skill) · "
                + $"{swingsPerKill:0} swings/kill · {hitsTakenPerKill:0} hits taken/kill · {killsPerHour:0} kills/hour · "
                + $"skill x{skillMult:0.00} · attr x{attrMult:0.00} · xp x{xpMult:0.00}");
Console.WriteLine();
Console.WriteLine("| lvl | kills | hours | content def | xp/kill | weapon skill | defence skill | Str | Coord | End | Health |");
Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
foreach (var r in rows) Console.WriteLine(r);

return 0;

// ------------------------------------------------------------- helpers -----

string Row(int lvl, double kills, double totalXp, double contentDef, long killXp,
           double wXp, double dXp, double sXp, double cXp, double eXp, double kph)
{
    var str = 10 + AttrRank(sXp);
    var end = 10 + AttrRank(eXp);
    var coord = 10 + AttrRank(cXp);
    var health = VitalFromAttribute(AttrRank(eXp));
    return $"| {lvl} | {kills:N0} | {kills / kph:N0} | {contentDef:N0} | {killXp:N0} | SKILLXP{wXp:N0} | "
         + $"{SkillRank(wXp) + AttributeBase(sXp, cXp):N0} | {SkillRank(dXp) + AttributeBase(cXp, cXp):N0} | "
         + $"{str:N0} | {coord:N0} | {end:N0} | {health:N0} |";
}

// skill base includes an attribute contribution; AC formulas are (a+b)/k style,
// approximated here as the mean of the two governing attributes over 3
int AttributeBase(double aXp, double bXp) => (int)((10 + AttrRank(aXp) + 10 + AttrRank(bXp)) / 3.0);

// vitals track their attribute's PROPORTION of the ceiling (004 + 013 re-base)
int VitalFromAttribute(int attrRanks)
{
    var vitalMax = vitalTable.Count - 1;
    var target = (int)Math.Round((double)attrRanks * vitalMax / attrMaxRanks);
    return Math.Min(target, vitalMax);
}

static string Find(string name)
{
    foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(),
                                Path.Combine(AppContext.BaseDirectory, "..", "..", "..") })
    {
        var p = Path.GetFullPath(Path.Combine(dir, name));
        if (File.Exists(p)) return p;
    }
    throw new FileNotFoundException($"{name} not found - it ships alongside this tool");
}

static Dictionary<string, List<double>> LoadTables(string path)
{
    // double, not uint: the character-level table reaches 191,226,310,247 at level 275,
    // far past uint.MaxValue. Truncating it would silently break every high-level row.
    var d = new Dictionary<string, List<double>>();
    foreach (var line in File.ReadLines(path))
    {
        if (line.StartsWith('#')) continue;
        var p = line.Split('\t');
        if (p.Length < 3) continue;
        if (!d.TryGetValue(p[0], out var list)) d[p[0]] = list = new List<double>();
        list.Add(double.TryParse(p[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0);
    }
    return d;
}

static int LowerBound(List<(int def, long xp)> list, int value)
{
    int lo = 0, hi = list.Count;
    while (lo < hi) { var mid = (lo + hi) / 2; if (list[mid].def < value) lo = mid + 1; else hi = mid; }
    return lo;
}

static string Env(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;
