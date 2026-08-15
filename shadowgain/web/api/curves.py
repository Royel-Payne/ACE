"""Shadowgain 124 — the rank and XP curves, ported from the server.

WHY THIS FILE IS A PORT AND NOT A LOOKUP

The whole point of the web character sheet is showing TRUE ranks — the numbers the retail
client physically cannot display, because its own tables stop at 190 attribute ranks and 208
trained skill ranks. Those true ranks are *not stored in the shard*. `biota_properties_skill`
holds experience; rank is derived from it every time the server is asked. So an API that read
`init_Level + level_From_P_P` and called it the true rank would be reading the CLIENT's clamped
shadow — exactly the bug 005 / 109 / 109b were written to kill — and would quietly disagree with
what `@mystats` tells the same player in game.

So the derivation is reproduced here, function for function, against the SAME dat tables the
server reads (exported to data/xptable.json by shadowgain/exporter). The sources are:

  Player_Skills.CalcSkillRank / CalcSkillRankUncapped / GetOvercapCurve / CalcSkillXpForRank
  Player_Attributes.CalcAttributeRank / CalcAttributeRankScaled / AttributeRankCost / AttributeMaxRanks
  CreatureSkill.TrueExperienceSpent
  AttributeFormula.GetFormula

Any change to those on the server side is a change here. That coupling is real and unavoidable —
the alternative was a new server endpoint, which would have meant game-server code and a restart,
which Part 1 explicitly does not do.

Two details that look like nitpicks and are not:

  * C# `Math.Round(x)` is banker's rounding and so is Python's `round()` — they agree. But
    ACE's `.Round()` extension (FloatExtensions) passes MidpointRounding.AwayFromZero, and that
    one does NOT match Python. `_round_away` exists for those call sites.
  * `(long)someDouble` in C# truncates toward zero; `int()` in Python does the same. `//` does
    not — it floors — so it is deliberately not used on the overcap maths.
"""

from __future__ import annotations

import json
import math
from dataclasses import dataclass
from decimal import Decimal, ROUND_HALF_UP
from functools import lru_cache
from pathlib import Path

DATA_DIR = Path(__file__).parent / "data"

# SkillAdvancementClass, from ACE.Entity.Enum. Stored raw in biota_properties_skill.s_a_c.
SAC_UNTRAINED = 1
SAC_TRAINED = 2
SAC_SPECIALIZED = 3

# Player_Reconcile.AttributeStartingValue. 013 wipes innate attributes to a uniform 10, which is
# what makes the attribute ceiling 280 ranks rather than the dat table's 190.
ATTRIBUTE_STARTING_VALUE = 10

# Player_Skills.MaxTrueSkillXp — long.MaxValue, because PropertyInt64 is signed. Not a design
# ceiling; it stops accumulation wrapping negative and re-deriving a nonsense rank.
MAX_TRUE_SKILL_XP = 2**63 - 1

# Player_Skills.OvercapRatioWindow.
OVERCAP_RATIO_WINDOW = 20

# Player_Skills.RatioEpsilon.
RATIO_EPSILON = 0.000001

# PropertyInt64.ShadowgainSkillXpBase — where a skill's true 64-bit XP lives once it passes
# uint.MaxValue. Absence is the meaningful default: below the ceiling, p_p alone is the truth.
SHADOWGAIN_SKILL_XP_BASE = 9100

UINT_MAX = 0xFFFFFFFF
USHORT_MAX = 0xFFFF


# ---------------------------------------------------------------------------------------------
# tables
# ---------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class XpTables:
    attribute: list[int]
    vital: list[int]
    trained_skill: list[int]
    specialized_skill: list[int]
    level: list[int]
    level_skill_credits: list[int]


@lru_cache(maxsize=1)
def tables() -> XpTables:
    raw = json.loads((DATA_DIR / "xptable.json").read_text(encoding="utf-8"))

    return XpTables(
        attribute=raw["attribute"],
        vital=raw["vital"],
        trained_skill=raw["trainedSkill"],
        specialized_skill=raw["specializedSkill"],
        level=raw["level"],
        level_skill_credits=raw["levelSkillCredits"],
    )


@lru_cache(maxsize=1)
def skill_table() -> dict[int, dict]:
    """SkillTable from the dat: name, icon, costs, usability, attribute formula."""
    raw = json.loads((DATA_DIR / "skills.json").read_text(encoding="utf-8"))

    return {int(k): v for k, v in raw.items()}


@lru_cache(maxsize=1)
def vital_formulas() -> dict[str, dict]:
    return json.loads((DATA_DIR / "vitals.json").read_text(encoding="utf-8"))


@lru_cache(maxsize=1)
def spell_table() -> dict[int, dict]:
    """Spell id -> name, from the dat. Used to name the spells on an item's examine text."""
    path = DATA_DIR / "spells.json"

    if not path.exists():
        return {}

    return {int(k): v for k, v in json.loads(path.read_text(encoding="utf-8")).items()}


@lru_cache(maxsize=1)
def enums() -> dict[str, dict[int, dict]]:
    raw = json.loads((DATA_DIR / "enums.json").read_text(encoding="utf-8"))

    return {name: {int(k): v for k, v in table.items()} for name, table in raw.items()}


def enum_label(table: str, value: int | None, fallback: str | None = None) -> str | None:
    """Turn a raw shard int into the word a player reads.

    Returns `fallback` (or a bare "<table> <value>") rather than raising, because an unknown id
    on ONE character must not 500 the whole page — a new heritage or title added upstream would
    otherwise take the site down until the exporter was re-run.
    """
    if value is None:
        return None

    entry = enums().get(table, {}).get(value)

    if entry is None:
        return fallback if fallback is not None else f"{table} {value}"

    return entry.get("label") or entry.get("name")


def get_skill_xp_table(sac: int) -> list[int] | None:
    """Player_Skills.GetSkillXPTable — None for anything below Trained, as upstream."""
    t = tables()

    if sac == SAC_TRAINED:
        return t.trained_skill

    if sac == SAC_SPECIALIZED:
        return t.specialized_skill

    return None


# ---------------------------------------------------------------------------------------------
# skills
# ---------------------------------------------------------------------------------------------


def true_experience_spent(pp: int, overflow: int | None) -> int:
    """CreatureSkill.TrueExperienceSpent.

    `pp` is the uint the client is shown; `overflow` is PropertyInt64 (9100 + skillId), which
    only exists once the real total passes uint.MaxValue. When it exists it IS the truth and pp
    is pinned at the top of the wire format — so overflow wins outright rather than being added.
    """
    return overflow if overflow is not None else pp


def calc_skill_rank(sac: int, xp: int) -> int:
    """Player_Skills.CalcSkillRank — the plain table lookup. -1 below the table's first entry."""
    table = get_skill_xp_table(sac)

    if table is None:
        return -1

    for i in range(len(table) - 1, -1, -1):
        if xp >= table[i]:
            return i

    return -1


@lru_cache(maxsize=4)
def overcap_curve(sac: int) -> tuple[float, float]:
    """Player_Skills.GetOvercapCurve — (lastStep, ratio).

    109b: progression past the table is the table's own final step compounding at the table's own
    ratio, so there is no seam and nothing to tune. The ratio is a geometric mean over the last 20
    steps because the specialized tail is noisy enough that a single step would set a bad slope.
    """
    table = get_skill_xp_table(sac)

    top = (len(table) - 1) if table else 0

    if table is None or top < 2:
        return (1.0, 1.0)

    last_step = float(table[top] - table[top - 1])

    n = min(OVERCAP_RATIO_WINDOW, top - 1)

    older_step = float(table[top - n] - table[top - n - 1])

    ratio = math.pow(last_step / older_step, 1.0 / n) if older_step > 0 else 1.0

    # Guards from upstream. Neither fires on the real dat; they exist so a flattened or ragged
    # table cannot make ranks free, negative, or unreachable.
    if math.isnan(ratio) or ratio < 1.0:
        ratio = 1.0
    if ratio > 2.0:
        ratio = 2.0
    if last_step < 1.0:
        last_step = 1.0

    return (last_step, ratio)


def calc_skill_rank_uncapped(sac: int, xp: int) -> int:
    """Player_Skills.CalcSkillRankUncapped — rank from true XP, continuing past the dat table.

    This is THE function the sheet's headline number comes from. Below the table top it is the
    ordinary lookup; above it, the closed-form inverse of

        extra = lastStep * ratio * (ratio^n - 1) / (ratio - 1)
    """
    table = get_skill_xp_table(sac)

    if table is None or len(table) < 2:
        return calc_skill_rank(sac, min(xp, UINT_MAX))

    top_rank = len(table) - 1
    top_xp = table[top_rank]

    if xp < top_xp:
        return calc_skill_rank(sac, xp)

    last_step, ratio = overcap_curve(sac)

    extra = float(xp) - top_xp

    # The first step past the table already carries one ratio: rank 209 costs MORE than 208 did.
    first_step = last_step * ratio

    if ratio <= 1.0 + RATIO_EPSILON:
        extra_ranks = extra / first_step
    else:
        extra_ranks = math.log(1.0 + extra * (ratio - 1.0) / first_step) / math.log(ratio)

    if math.isnan(extra_ranks) or extra_ranks < 0:
        extra_ranks = 0.0

    # int() truncates toward zero, matching C#'s (long) cast. floor() would differ on negatives.
    total = top_rank + int(extra_ranks)

    return min(total, USHORT_MAX - 1)


def calc_skill_xp_for_rank(sac: int, rank: int) -> int | None:
    """Player_Skills.CalcSkillXpForRank — total XP needed to REACH a rank.

    A binary search over the forward function rather than a closed-form mirror of it. 109 found
    the mirror disagreed on 1,741 of the first 5,000 ranks past the wall, which showed up as
    "0 more needed" for a rank that had not ticked over. Searching cannot drift by construction.
    """
    table = get_skill_xp_table(sac)

    if table is None or len(table) < 2 or rank < 0:
        return None

    top_rank = len(table) - 1

    if rank <= top_rank:
        return table[rank]

    if calc_skill_rank_uncapped(sac, MAX_TRUE_SKILL_XP) < rank:
        return None

    lo = table[top_rank]
    hi = MAX_TRUE_SKILL_XP

    while lo < hi:
        mid = lo + (hi - lo) // 2

        if calc_skill_rank_uncapped(sac, mid) >= rank:
            hi = mid
        else:
            lo = mid + 1

    return lo


def skill_progress(sac: int, xp: int) -> tuple[int, int, int]:
    """(trueRank, xpIntoRank, xpToNextRank) — what the sheet's XP bar draws.

    `xpIntoRank` is measured from the XP that bought the CURRENT rank, so the bar fills from the
    start of this rank rather than from zero. Both are 0 for a skill below Trained: a pruned skill
    keeps its XP (093) but earns nothing, and drawing a part-filled bar for it would imply motion
    that is not happening.
    """
    if sac < SAC_TRAINED:
        return (0, 0, 0)

    rank = calc_skill_rank_uncapped(sac, xp)

    if rank < 0:
        rank = 0

    this_rank_xp = calc_skill_xp_for_rank(sac, rank)
    next_rank_xp = calc_skill_xp_for_rank(sac, rank + 1)

    into = max(0, xp - this_rank_xp) if this_rank_xp is not None else 0
    to_next = max(0, next_rank_xp - xp) if next_rank_xp is not None else 0

    return (rank, into, to_next)


# ---------------------------------------------------------------------------------------------
# attributes
# ---------------------------------------------------------------------------------------------


def attribute_max_ranks(attributes_start_at_ten: bool, attribute_max_value: int) -> int:
    """Player_Attributes.AttributeMaxRanks.

    Attribute value is StartingValue + Ranks, so a start-10 attribute needs
    (attribute_max_value - 10) ranks — 280 for the live ceiling of 290, against a dat table that
    only defines 190.
    """
    table_max = len(tables().attribute) - 1

    if not attributes_start_at_ten:
        return table_max

    ranks = int(attribute_max_value - ATTRIBUTE_STARTING_VALUE)

    return ranks if ranks > 0 else table_max


def attribute_rank_cost(rank: int, max_ranks: int) -> int:
    """Player_Attributes.AttributeRankCost — the dat table STRETCHED across the wider rank range.

    Stretched rather than extended because the table's own final step (308,765,680) exceeds the
    remaining uint headroom above its total, so continuing at the table's pace buys zero further
    ranks. Stretching keeps a maxed attribute costing exactly what retail charges and spreads the
    same climb over more, smaller ranks. Linear interpolation between entries keeps it smooth.
    """
    table = tables().attribute
    table_max = len(table) - 1

    if rank <= 0:
        return 0

    if rank >= max_ranks:
        return table[table_max]

    t = rank * table_max / max_ranks

    lower = math.floor(t)
    frac = t - lower

    if lower >= table_max:
        return table[table_max]

    cost = table[lower] + frac * (table[lower + 1] - table[lower])

    if cost < 0:
        return 0

    # C# `(uint)Math.Round(cost)` is banker's rounding, and so is Python's round(). They agree
    # here; the away-from-zero variant is only used where ACE's own .Round() extension is.
    return min(UINT_MAX, round(cost))


def calc_attribute_rank(
    xp: int, attributes_start_at_ten: bool, attribute_max_value: int
) -> int:
    """Player_Attributes.CalcAttributeRank, including the 013 scaled path.

    With attributes_start_at_ten OFF this is the plain dat lookup; with it ON (which is the live
    setting) it binary-searches the stretched cost function, which is monotonic by construction.
    """
    if not attributes_start_at_ten:
        table = tables().attribute

        for i in range(len(table) - 1, -1, -1):
            if xp >= table[i]:
                return i

        return -1

    max_ranks = attribute_max_ranks(attributes_start_at_ten, attribute_max_value)

    if xp < attribute_rank_cost(1, max_ranks):
        return 0

    lo, hi = 0, max_ranks

    while lo < hi:
        mid = (lo + hi + 1) // 2

        if attribute_rank_cost(mid, max_ranks) <= xp:
            lo = mid
        else:
            hi = mid - 1

    return lo


def attribute_progress(
    xp: int, attributes_start_at_ten: bool, attribute_max_value: int
) -> tuple[int, int, int, int]:
    """(rank, xpIntoRank, xpToNextRank, maxRanks) for an attribute.

    Mirrors what `@mystats <attribute>` prints, so a player reading both sees one number.
    """
    max_ranks = attribute_max_ranks(attributes_start_at_ten, attribute_max_value)

    rank = calc_attribute_rank(xp, attributes_start_at_ten, attribute_max_value)

    if rank < 0:
        rank = 0

    if rank >= max_ranks:
        return (rank, 0, 0, max_ranks)

    this_cost = attribute_rank_cost(rank, max_ranks)
    next_cost = attribute_rank_cost(rank + 1, max_ranks)

    return (rank, max(0, xp - this_cost), max(0, next_cost - xp), max_ranks)


# ---------------------------------------------------------------------------------------------
# vitals, skill bases, and level
# ---------------------------------------------------------------------------------------------


def _round_away(value: float) -> int:
    """ACE.Common.Extensions.FloatExtensions.Round — MidpointRounding.AwayFromZero.

    Python's round() is banker's, so 85.5 would give 86 here but 86 there only by luck and 84.5
    would give 84 against ACE's 85. Decimal's ROUND_HALF_UP is the exact equivalent.
    """
    return int(Decimal(repr(value)).quantize(Decimal("1"), rounding=ROUND_HALF_UP))


def apply_formula(formula: dict, attribute_bases: dict[int, int]) -> int:
    """AttributeFormula.GetFormula, base variant.

    `attribute_bases` is keyed by PropertyAttribute id (1..6). The `current` variant of this on
    the server adds live enchantments; the sheet reads a saved snapshot where those do not exist,
    so only the base path is ported. See the `buffed` note in payload.py.
    """
    # X == 0 means no attribute contribution at all, and upstream tests it FIRST. Dropping the
    # test would quietly hand an attribute bonus to skills that are supposed to have none.
    if not formula or formula.get("x", 0) == 0:
        return 0

    attr1 = formula.get("attr1Id", 0)
    attr2 = formula.get("attr2Id", 0)
    divisor = formula.get("divisor", 1)

    total = attribute_bases.get(attr1, 0)

    if attr2:  # PropertyAttribute.Undef == 0
        total += attribute_bases.get(attr2, 0)

    if divisor != 1:
        total = _round_away(total / divisor)

    return total


def level_progress(total_xp: int, level: int) -> tuple[int, int]:
    """(xpIntoLevel, xpToNextLevel) against the dat's own level table.

    Uses the STORED level rather than re-deriving one from XP. The stored level is what the
    server acts on, and re-deriving would invent a disagreement the moment anything (a grant, an
    admin fix) moved one without the other.
    """
    table = tables().level

    top = len(table) - 1

    if level >= top:
        return (0, 0)

    this_level_xp = table[level] if 0 <= level <= top else 0
    next_level_xp = table[level + 1]

    return (max(0, total_xp - this_level_xp), max(0, next_level_xp - total_xp))
