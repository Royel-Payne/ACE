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
INT = 0x0000004                 # a PropertyInt on an ITEM, e.g. ArmorLevel
FLOAT = 0x0000008               # a PropertyFloat on an ITEM, e.g. WeaponDefense
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


def load_many(cur, object_ids: list[int]) -> dict[int, list[dict]]:
    """The same rows, for many objects at once — ITEMS carry their own enchantments (158).

    An item's buffs live on the ITEM, not on the character wielding it, and `AppraiseInfo` reads
    both: `item + wielder` for the defense mods, `item * wielder` for mana conversion. Loading
    them per item would be one query per row of inventory, so this is the bulk form.
    """
    if not object_ids:
        return {}

    marks = ",".join(["%s"] * len(object_ids))

    rows = db.fetch_all(
        cur,
        f"""
        SELECT object_Id, spell_Id, spell_Category, power_Level, start_Time, duration,
               stat_Mod_Type, stat_Mod_Key, stat_Mod_Value
        FROM biota_properties_enchantment_registry
        WHERE object_Id IN ({marks})
        """,
        tuple(object_ids),
    )

    out: dict[int, list[dict]] = {}

    for r in rows:
        duration = float(r["duration"] or 0)
        start = float(r["start_Time"] or 0)

        if not is_live(duration, start):
            continue

        out.setdefault(int(r["object_Id"]), []).append(
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

    return out


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


# --- item stats the client shows BUFFED (158) --------------------------------------------------
#
# `AppraiseInfo` does not send the stored value. It merges enchantments from the item AND from the
# wielder before the client ever sees a number, which is why a stored WeaponDefense of 1.17 was
# displayed in game as +32.0%.
#
# THE TYPE FLAG IS WHAT MAKES THIS WORK, and it is easy to miss. Black Breath buffed carries TWO
# rows keyed 29:
#
#   type 0x2009008  key 169  +0.15   Float  -> WeaponAuraDefense, belongs here
#   type 0x2009010  key  29  +40     Skill  -> the Melee Defense SKILL, does not
#
# Matching on the key alone would add 40 to a multiplier and report +4017%. `FLOAT` is the filter
# that separates them.

FLOAT_WEAPON_DEFENSE = 29
FLOAT_WEAPON_AURA_DEFENSE = 169
FLOAT_MANA_CONVERSION_MOD = 144


def defense_mod(enchantments: list[dict]) -> float:
    """`EnchantmentManager.GetDefenseMod()` — additive over WeaponDefense AND WeaponAuraDefense."""
    return (additive(enchantments, FLOAT, FLOAT_WEAPON_DEFENSE)
            + additive(enchantments, FLOAT, FLOAT_WEAPON_AURA_DEFENSE))


def mana_conv_mod(enchantments: list[dict]) -> float:
    """`EnchantmentManager.GetManaConvMod()` — MULTIPLICATIVE, unlike the defense mods above.

    ACE's own comment: *"enchantments are multiplicative, so they are only effective if there is a
    base mod"* — a 1.7x aura on an item with no ManaConversionMod is still nothing.
    """
    return multiplier(enchantments, FLOAT, FLOAT_MANA_CONVERSION_MOD)


INT_ARMOR_LEVEL = 28


def armor_mod(enchantments: list[dict]) -> int:
    """`EnchantmentManager.GetArmorMod()` — additive over ArmorLevel, from the ITEM's own rows.

    AppraiseInfo takes this from the item alone (`wo.EnchantmentManager.GetArmorMod()`), NOT from
    the wielder: Impenetrability is cast onto the armour, so it lives on the armour.
    """
    return int(additive(enchantments, INT, INT_ARMOR_LEVEL))


INT_DAMAGE = 44
INT_WEAPON_TIME = 49
INT_WEAPON_AURA_DAMAGE = 360
INT_WEAPON_AURA_SPEED = 361


def damage_bonus(enchantments: list[dict]) -> int:
    """`EnchantmentManager.GetDamageBonus()` — additive over Damage AND WeaponAuraDamage.

    Two keys because of a quirk ACE documents in that method: Blood Drinker 1-7 are defined against
    PropertyInt.Damage while the later ones use WeaponAuraDamage, so both have to be summed or half
    the game's damage buffs vanish depending on which spell tier is up.
    """
    return int(additive(enchantments, INT, INT_DAMAGE)
               + additive(enchantments, INT, INT_WEAPON_AURA_DAMAGE))


def speed_mod(enchantments: list[dict]) -> int:
    """`EnchantmentManager.GetWeaponSpeedMod()` — additive over WeaponTime AND WeaponAuraSpeed.

    NEGATIVE is faster: WeaponTime counts up from 0, so Swift Killer subtracts.
    """
    return int(additive(enchantments, INT, INT_WEAPON_TIME)
               + additive(enchantments, INT, INT_WEAPON_AURA_SPEED))
