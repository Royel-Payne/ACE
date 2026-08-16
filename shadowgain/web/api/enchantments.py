"""Shadowgain 136 — active enchantments, so the sheet shows what the CLIENT shows.

WHY THIS EXISTS

The panel was reporting *base* values while the game reports *buffed* ones. It surfaced as a burden
figure that looked inverted — 46% in game against 54% here — but nothing was inverted: capacity is
`150 * Strength`, and the server uses Strength **Current** while we only had Base. Black Breath is
buffed +35, so the client's capacity is 38,250 and ours was 33,000. The same gap ran through every
attribute, vital and skill on the page.

THE ONE RULE THAT MATTERS: BUFFS DO NOT SUM

Black Breath carries two Strength enchantments, +15 (permanent) and +35 (timed), and the client
shows exactly **+35** — not +50. Enchantments are grouped by **spell category**, only the strongest
in each category applies, and those winners are then summed across categories.

Ported from `PropertiesEnchantmentRegistryExtensions.GetEnchantmentsTopLayerByStatModType`. Note
the winner is chosen by **PowerLevel**, *not* LayerId — LayerId is right there in the source as a
commented-out earlier approach, and reaching for it is the obvious wrong turn.

WHAT THIS DELIBERATELY DOES NOT MODEL

`setSpells` (spell-set bonuses from equipped armour sets) participates in ACE's tie-break, and
Vitae has its own handling. Both are ignored here: the tie-break only matters between two spells of
equal PowerLevel in one category, and Vitae is a death penalty we do not display. If a value ever
disagrees with the client by a small amount on a character wearing a full set, this is the first
place to look.
"""

from __future__ import annotations

from . import curves, db

# --- EnchantmentTypeFlags (ACE.Entity/Enum/EnchantmentTypeFlags.cs) ----------------------------

ATTRIBUTE = 0x0000001
SECOND_ATT = 0x0000002          # vitals
SKILL = 0x0000010
SINGLE_STAT = 0x0001000
MULTIPLE_STAT = 0x0002000
MULTIPLICATIVE = 0x0004000
ADDITIVE = 0x0008000
VITAE = 0x0800000


def is_live(duration: float, start: float) -> bool:
    """Whether an enchantment row is still in force.

    The server's heartbeat ticks `StartTime` *backwards* toward `-Duration`, so a row is spent once
    `StartTime <= -Duration`. A negative Duration means permanent, which is why the comparison is
    guarded rather than just done.

    Split out of `load` so it can be tested directly — a test that re-implements this rule inline
    proves only that the test author can write it twice.
    """
    return not (duration >= 0 and start <= -duration)


def load(cur, character_id: int) -> list[dict]:
    """Every enchantment row still in force for this character.

    Expiry mirrors the server's heartbeat: `StartTime` ticks *backwards* toward `-Duration`, so a
    row is spent once `StartTime <= -Duration`. A negative Duration means permanent and never
    expires — which is why the comparison is guarded on `Duration >= 0` rather than just done.
    """
    rows = db.fetch_all(
        cur,
        """
        SELECT spell_Id, spell_Category, power_Level, start_Time, duration,
               stat_Mod_Type, stat_Mod_Key, stat_Mod_Value
        FROM biota_properties_enchantment_registry
        WHERE object_Id = %s
        """,
        (character_id,),
    )

    live = []

    for r in rows:
        duration = float(r["duration"] or 0)
        start = float(r["start_Time"] or 0)

        if not is_live(duration, start):
            continue

        live.append(
            {
                "spell": r["spell_Id"],
                "category": r["spell_Category"],
                "power": r["power_Level"] or 0,
                "start": start,
                "type": int(r["stat_Mod_Type"] or 0),
                "key": int(r["stat_Mod_Key"] or 0),
                "value": float(r["stat_Mod_Value"] or 0),
            }
        )

    return live


def _top_layer(enchantments: list[dict], want: int, key: int) -> list[dict]:
    """The strongest enchantment in each spell category, for one stat type + key.

    `handleMultiple` in ACE's signature is always true for attributes, vitals and skills, so it is
    folded in here: SingleStat is added to the requested flags, and MultipleStat rows carrying
    key 0 (a spell that buffs a whole group at once) are matched as well.
    """
    single = want | SINGLE_STAT
    multiple = want | MULTIPLE_STAT

    matched = [
        e for e in enchantments
        if (e["type"] & single) == single and e["key"] == key
        or ((e["type"] & multiple) == multiple and not e["type"] & VITAE and e["key"] == 0)
    ]

    by_category: dict[int, dict] = {}

    for e in matched:
        best = by_category.get(e["category"])

        # PowerLevel decides, then the later cast. ACE also breaks ties on level-8 self auras and
        # on spell-set membership; neither is modelled - see the module docstring.
        if best is None or (e["power"], e["start"]) > (best["power"], best["start"]):
            by_category[e["category"]] = e

    return list(by_category.values())


def additive(enchantments: list[dict], want: int, key: int) -> float:
    return sum(e["value"] for e in _top_layer(enchantments, want | ADDITIVE, key))


def multiplier(enchantments: list[dict], want: int, key: int) -> float:
    mod = 1.0

    for e in _top_layer(enchantments, want | MULTIPLICATIVE, key):
        mod *= e["value"]

    return mod


def attribute_current(base: int, enchantments: list[dict], attribute_id: int) -> int:
    """`CreatureAttribute.GetCurrent` — base * multiplier + additive, then floored.

    The floor is the server's: a debuff cannot drive an attribute below 10 (or below 1 for a
    creature that started under 10). Rounding happens before the floor, as it does there.
    """
    mult = multiplier(enchantments, ATTRIBUTE, attribute_id)
    add = additive(enchantments, ATTRIBUTE, attribute_id)

    # ACE rounds HALF UP (its own .Round() extension); Python's round() is banker's, so 118.5
    # would land on 118 here and 119 there. curves._round_away is the exact equivalent and is
    # already used for the attribute formulas - reuse it rather than keep a second convention.
    total = curves._round_away(base * mult + add)
    floor = 10 if base >= 10 else 1

    return max(floor, int(total))


def vital_current_max(base_max: int, enchantments: list[dict], vital_id: int) -> int:
    """A vital's buffed MAXIMUM. Current/Missing is tracked separately by the server."""
    mult = multiplier(enchantments, SECOND_ATT, vital_id)
    add = additive(enchantments, SECOND_ATT, vital_id)

    return max(0, curves._round_away(base_max * mult + add))


def skill_current(base: int, enchantments: list[dict], skill_id: int) -> int:
    mult = multiplier(enchantments, SKILL, skill_id)
    add = additive(enchantments, SKILL, skill_id)

    return max(0, curves._round_away(base * mult + add))
