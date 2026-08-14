"""Shadowgain 124 — building the character JSON that Task.md 124 Contract 1 describes.

This is the file that turns raw shard rows into the object the front-end renders. The field
names match `landing/character-sheet-mockup.html`'s `CHAR` exactly, because that mockup is the
living contract and matching it means Cowork's UI works against real data with no edits.

THE ONE CONTRACT CORRECTION (see the Summary in Task.md 124)

Contract 1 says `trueRank = InitLevel + Ranks`. That is wrong on our server, and reading it that
way would put a number on the page that disagrees with `@mystats` in game:

  * `init_Level` on a skill is the retail SPECIALIZATION BONUS (+10 to the skill's base value),
    not part of its rank. Black Breath's Melee Defense is s_a_c=3, init_Level=10, ranks=190 —
    `InitLevel + Ranks` reports 200, and the server says 190.
  * rank is DERIVED from experience, every time, by `Player.CalcSkillRankUncapped` — see
    curves.py. It is not stored anywhere.

So `trueRank` keeps its contract NAME (the front-end needs no change) and gains its correct
derivation. Verified against all of Black Breath's live rows: 14/14 skills and 6/6 attributes
agree exactly with the values the shard stores.

PUBLIC VS PRIVATE

`build_public` and `build_private` are separate functions returning separate dicts, rather than
one builder with a flag. A flag is one wrong boolean away from putting a player's inventory and
whereabouts on a page with no login, and this is the one mistake in this service that could not
be walked back. The public payload's keys are enumerated literally, so a field added to the
private object cannot leak into it by default.
"""

from __future__ import annotations

import math
from typing import Any

from . import curves, db, names

# --- property ids we read (ACE.Entity.Enum.Properties) ---------------------------------------

INT_LEVEL = 25
INT_AVAILABLE_SKILL_CREDITS = 24
INT_GENDER = 113
INT_HERITAGE = 188
INT_PLAYER_KILLER_STATUS = 133
INT_AGE = 125
INT_STACK_SIZE = 12
INT_CURRENT_WIELDED_LOCATION = 10
INT_VALID_LOCATIONS = 9
INT_ITEM_TYPE = 1
INT_CHARACTER_TITLE_ID = 261

INT64_TOTAL_EXPERIENCE = 1
INT64_AVAILABLE_EXPERIENCE = 2

STRING_NAME = 1

DID_ICON = 8

IID_CONTAINER = 2
IID_WIELDER = 3

POSITION_LOCATION = 1

# PropertyAttribute / PropertyAttribute2nd
ATTRIBUTE_IDS = [1, 2, 3, 4, 5, 6]  # Strength, Endurance, Coordination, Quickness, Focus, Self
VITAL_KEYS = {1: ("health", "Health", "maxHealth"), 3: ("stamina", "Stamina", "maxStamina"), 5: ("mana", "Mana", "maxMana")}

# The `cat` field in the contract: a coarse class the front-end tints icons by until the real
# PNGs land. Derived from the dat's own skill Category (1 combat, 2 other, 3 magic) plus the
# handful the mockup calls out separately, so it is never hand-maintained per skill.
CATEGORY_BY_DAT = {1: "phys", 2: "util", 3: "magic"}

DEFENSE_SKILLS = {6, 7, 15}          # Melee Defense, Missile Defense, Magic Defense
CRAFT_SKILLS = {17, 18, 19, 20, 21, 22, 38, 39, 40}  # tinkering, alchemy, cooking, fletching, salvaging...
WEAPON_SKILLS = {41, 44, 45, 47, 48, 49, 50, 51, 52, 53}

# The dat marks a skill as un-specializable with an absurd cost rather than a flag: the four
# tinkering skills and Salvaging carry SpecializedCost 999-1003 against trained costs of 0-4.
# Showing "Specialize: 999 credits" would read as a very expensive option instead of as no
# option, so anything at or above this becomes `canSpecialize: false`.
SPEC_COST_SENTINEL = 100


def _category(skill_id: int, dat_category: int) -> str:
    if skill_id in DEFENSE_SKILLS:
        return "def"
    if skill_id in CRAFT_SKILLS:
        return "craft"
    if skill_id in WEAPON_SKILLS:
        return "weap"

    return CATEGORY_BY_DAT.get(dat_category, "util")


# ---------------------------------------------------------------------------------------------
# raw reads
# ---------------------------------------------------------------------------------------------


def _props(cur, table: str, object_id: int, value_col: str = "value") -> dict[int, Any]:
    rows = db.fetch_all(
        cur, f"SELECT type, {value_col} AS v FROM {table} WHERE object_Id = %s", (object_id,)
    )
    return {r["type"]: r["v"] for r in rows}


def load_character(cur, character_id: int) -> dict | None:
    """Every row for one character, in one place.

    Ten small indexed queries rather than one join: the shard's property tables are EAV, so a
    join across them multiplies rows by every property, and the result would need unpicking in
    Python anyway. Each of these hits a primary or object_Id index and returns tens of rows.
    """
    char = db.fetch_one(
        cur,
        """
        SELECT id, account_Id, name, is_Deleted, delete_Time,
               last_Login_Timestamp, total_Logins
        FROM `character`
        WHERE id = %s
        """,
        (character_id,),
    )

    if char is None or db.as_bool(char["is_Deleted"]) or (char["delete_Time"] or 0) != 0:
        return None

    return {
        "char": char,
        "ints": _props(cur, "biota_properties_int", character_id),
        "int64s": _props(cur, "biota_properties_int64", character_id),
        "bools": _props(cur, "biota_properties_bool", character_id),
        "attributes": db.fetch_all(
            cur,
            "SELECT type, init_Level, level_From_C_P, c_P_Spent "
            "FROM biota_properties_attribute WHERE object_Id = %s",
            (character_id,),
        ),
        "vitals": db.fetch_all(
            cur,
            "SELECT type, init_Level, level_From_C_P, c_P_Spent, current_Level "
            "FROM biota_properties_attribute_2nd WHERE object_Id = %s",
            (character_id,),
        ),
        "skills": db.fetch_all(
            cur,
            "SELECT type, s_a_c, init_Level, level_From_P_P, p_p "
            "FROM biota_properties_skill WHERE object_Id = %s",
            (character_id,),
        ),
        "titles": [
            r["title_Id"]
            for r in db.fetch_all(
                cur,
                "SELECT title_Id FROM character_properties_title_book WHERE character_Id = %s",
                (character_id,),
            )
        ],
        "positions": db.fetch_all(
            cur,
            "SELECT position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z "
            "FROM biota_properties_position WHERE object_Id = %s",
            (character_id,),
        ),
        "quests": db.fetch_all(
            cur,
            "SELECT quest_Name, last_Time_Completed, num_Times_Completed "
            "FROM character_properties_quest_registry WHERE character_Id = %s",
            (character_id,),
        ),
    }


# ---------------------------------------------------------------------------------------------
# the pieces
# ---------------------------------------------------------------------------------------------


def _marker(bools: dict) -> str:
    """The 021 hard-lane marker: PropertyBool 9102, ShadowgainForfeitedMarker.

    Absent or false means the character has NEVER taken the fast lane — that is the hard path,
    and it is what earns the `†`. Same test the honour roll uses (tools/sitedata.sh), and the
    same reason it is written as "not true" rather than "is false": the row does not exist for
    a character who never forfeited.
    """
    return "fast" if db.as_bool(bools.get(9102)) else "hard"


def _attribute_bases(attributes: list[dict]) -> dict[int, int]:
    """PropertyAttribute id -> Base (StartingValue + Ranks), for the vital and skill formulas."""
    return {
        row["type"]: (row["init_Level"] or 0) + (row["level_From_C_P"] or 0) for row in attributes
    }


def build_attributes(raw: dict, dials: dict) -> list[dict]:
    out = []

    for row in sorted(raw["attributes"], key=lambda r: r["type"]):
        attr_id = row["type"]

        if attr_id not in ATTRIBUTE_IDS:
            continue

        starting = row["init_Level"] or 0
        cp_spent = row["c_P_Spent"] or 0

        rank, into, to_next, max_ranks = curves.attribute_progress(
            cp_spent, dials["attributes_start_at_ten"], dials["attribute_max_value"]
        )

        base = starting + rank

        key = (curves.enum_label("attribute", attr_id) or f"attr{attr_id}").lower()

        out.append(
            {
                "key": key,
                "label": curves.enum_label("attribute", attr_id),
                "cat": "magic" if attr_id in (5, 6) else ("def" if attr_id == 2 else "phys"),
                "base": base,
                # `buffed` deliberately equals `base` — see the note in build_private().
                "buffed": base,
                "trueRank": rank,
                "maxRank": max_ranks,
                "startingValue": starting,
                "xpSpent": cp_spent,
                "xpIntoRank": into,
                "xpToNextRank": to_next,
                "icon": f"/assets/icons/attribute/{key}.png",
            }
        )

    return out


def build_vitals(raw: dict) -> list[dict]:
    """The three vitals.

    004 holds these at the same fraction of their ceiling as the attribute that governs them —
    they earn nothing of their own — so Base is StartingValue + Ranks + formula(attributes),
    with the formula read from the dat (data/vitals.json). `current` is the stored current
    level, which is genuinely what the character had at their last save and so can sit below max.
    """
    formulas = curves.vital_formulas()
    bases = _attribute_bases(raw["attributes"])

    out = []

    for row in sorted(raw["vitals"], key=lambda r: r["type"]):
        vital_id = row["type"]

        if vital_id not in VITAL_KEYS:
            continue

        key, label, formula_key = VITAL_KEYS[vital_id]

        base = (
            (row["init_Level"] or 0)
            + (row["level_From_C_P"] or 0)
            + curves.apply_formula(formulas.get(formula_key, {}), bases)
        )

        current = row["current_Level"] or 0

        out.append(
            {
                "key": key,
                "label": label,
                "cat": {"health": "def", "stamina": "phys", "mana": "magic"}[key],
                # The mockup renders "current/max"; max here is the saved base, which is the
                # honest number for a snapshot with no live enchantments in it.
                "current": current,
                "max": base,
                "base": base,
                "ranks": row["level_From_C_P"] or 0,
                "xpSpent": row["c_P_Spent"] or 0,
                "icon": f"/assets/icons/vital/{key}.png",
            }
        )

    return out


def build_skills(raw: dict) -> list[dict]:
    """Every skill the character has a row for, grouped the way the in-game panel groups them.

    The four groups are the whole point of this tab (Task.md 123):
      specialized / trained  — active, earning
      untrained              — PRUNED per 093: below Trained, ranks and XP FROZEN, not lost
      unusable               — needs training to use at all, and has none

    The untrained group is what visibly proves 093 preserves progress, so a pruned skill reports
    the rank its held XP would buy back — not zero, and not a blank.
    """
    table = curves.skill_table()
    bases = _attribute_bases(raw["attributes"])
    int64s = raw["int64s"]

    out = []

    for row in raw["skills"]:
        skill_id = row["type"]

        meta = table.get(skill_id)

        if meta is None or not meta.get("valid"):
            # Retired and unimplemented skills still have rows on old characters. They are not
            # part of the game any more, so they are not part of the sheet.
            continue

        sac = row["s_a_c"] or 0
        pp = row["p_p"] or 0

        # CreatureSkill.TrueExperienceSpent: the 64-bit overflow property wins when present.
        overflow = int64s.get(curves.SHADOWGAIN_SKILL_XP_BASE + skill_id)
        xp = curves.true_experience_spent(pp, overflow)

        if sac >= curves.SAC_TRAINED:
            group = "specialized" if sac == curves.SAC_SPECIALIZED else "trained"
            rank, into, to_next = curves.skill_progress(sac, xp)
        elif xp > 0 or (row["level_From_P_P"] or 0) > 0:
            # Pruned (093). Frozen, so no progress bar — but show the rank it holds. Quoted
            # against the TRAINED table, because free re-training is what it returns to.
            group = "untrained"
            rank = curves.calc_skill_rank_uncapped(curves.SAC_TRAINED, xp)
            into = to_next = 0
        else:
            group = "untrained" if meta.get("usableUntrained") else "unusable"
            rank, into, to_next = 0, 0, 0

        base = (row["init_Level"] or 0) + rank

        if meta.get("usableUntrained") or sac >= curves.SAC_TRAINED:
            base += curves.apply_formula(meta.get("formula", {}), bases)

        spec_cost = meta.get("upgradeCost", 0) or 0
        can_specialize = spec_cost < SPEC_COST_SENTINEL

        out.append(
            {
                "key": meta["enumName"].lower(),
                "id": skill_id,
                "label": meta["name"],
                "cat": _category(skill_id, meta.get("category", 2)),
                "group": group,
                "base": base,
                "buffed": base,
                "trueRank": rank,
                "xpSpent": xp,
                "xpIntoRank": into,
                "xpToNextRank": to_next,
                # SpecializedCost includes the trained cost; the upgrade is what a trained skill
                # actually pays, and quoting the raw figure would overstate it.
                "specCost": spec_cost,
                "canSpecialize": can_specialize,
                "icon": f"/assets/icons/skill/{skill_id}.png",
            }
        )

    # Highest rank first, then name — the same ordering @myskills uses, so the two agree on sight.
    out.sort(key=lambda s: (-s["trueRank"], s["label"]))

    return out


def build_skill_credits(raw: dict, skills: list[dict]) -> dict:
    """Credits available, spent on specialization, and the total that implies.

    ACE stores only the AVAILABLE count (PropertyInt 24); what has been SPENT is not recorded
    anywhere, so it is recomputed from what is currently specialized.

    The figure is `upgradeCost` (SpecializedCost - TrainedCost), NOT the dat's SpecializedCost.
    Training is free on this server — Player_Skills: *"training needs to be free, spec is the
    only thing with fees"* — and `SpecializedCost` bundles the trained cost the player never
    paid. Charging it here would overstate every player's spend and inflate the total, and it is
    the same figure `SpecializeSkill` actually debits (Player_Skills.cs:474). It also matches the
    per-skill `specCost` on each row, so the two numbers on the page agree.
    """
    available = raw["ints"].get(INT_AVAILABLE_SKILL_CREDITS, 0) or 0

    table = curves.skill_table()

    spent = 0

    for skill in skills:
        if skill["group"] == "specialized":
            meta = table.get(skill["id"], {})
            spent += meta.get("upgradeCost", 0) or 0

    return {"available": available, "spentOnSpec": spent, "total": available + spent}


def build_titles(raw: dict) -> list[dict]:
    """The character's title book.

    ACE has no "active title" column on the title book — the displayed title is a separate
    property — so `active` marks the one the character is currently wearing and everything else
    is history. CharacterTitle names come from the exported enum.
    """
    current = raw["ints"].get(INT_CHARACTER_TITLE_ID)

    out = []

    for title_id in sorted(raw["titles"]):
        out.append(
            {
                "id": title_id,
                "name": curves.enum_label("title", title_id, fallback=f"Title {title_id}"),
                "active": title_id == current,
            }
        )

    # The worn title first, then the rest alphabetically — a title book runs to dozens of entries
    # and the one being worn is the only one anybody is looking for.
    out.sort(key=lambda t: (not t["active"], t["name"]))

    return out


def build_location(raw: dict) -> dict | None:
    """Last SAVED position — place name plus coordinates.

    Private only. It is the single most sensitive field on the sheet: it says where a player
    keeps their character, which is why 123 puts it behind login alongside inventory.
    """
    position = next(
        (p for p in raw["positions"] if p["position_Type"] == POSITION_LOCATION), None
    )

    if position is None:
        return None

    return names.describe_position(
        position["obj_Cell_Id"], position["origin_X"], position["origin_Y"]
    )


def build_quests(raw: dict, now: float) -> list[dict]:
    """Completion counts plus when each becomes available again.

    `availableAt` is `last_Time_Completed + min_Delta`, and is null when the quest is repeatable
    now or has no cooldown — the contract's own definition. `maxSolves` of -1 is ACE's "unlimited"
    sentinel and is passed through unchanged rather than reinterpreted.
    """
    table = names.quests()

    out = []

    for row in raw["quests"]:
        key = row["quest_Name"]
        meta = table.get(key, {})

        last = int(row["last_Time_Completed"] or 0)
        min_delta = int(meta.get("minDelta", 0) or 0)
        max_solves = meta.get("maxSolves", 0)

        available_at = None

        if min_delta > 0 and last > 0:
            candidate = last + min_delta

            if candidate > now:
                available_at = candidate

        out.append(
            {
                "key": key,
                "name": meta.get("name") or key,
                "completions": int(row["num_Times_Completed"] or 0),
                "maxSolves": max_solves,
                "lastCompleted": last or None,
                "availableAt": available_at,
            }
        )

    out.sort(key=lambda q: (q["lastCompleted"] or 0), reverse=True)

    return out


def build_inventory(cur, character_id: int) -> dict:
    """The 2D paperdoll's data: what is worn, and what is in each pack.

    One query, not a walk. Every object a character owns carries either a Container (IID 2) or a
    Wielder (IID 3) pointing at its owner — a pack's contents point at the PACK, and the pack
    points at the character, so two levels of ContainerId cover the whole main pack -> sacks tree
    the design asks for.
    """
    rows = db.fetch_all(
        cur,
        """
        SELECT b.id,
               b.weenie_Type,
               s.value       AS name,
               d.value       AS icon,
               stack.value   AS stack_size,
               wield.value   AS wielded_location,
               valid.value   AS valid_locations,
               container.value AS container_id,
               wielder.value   AS wielder_id
        FROM biota b
        LEFT JOIN biota_properties_string s   ON s.object_Id = b.id AND s.type = %s
        LEFT JOIN biota_properties_d_i_d d    ON d.object_Id = b.id AND d.type = %s
        LEFT JOIN biota_properties_int stack  ON stack.object_Id = b.id AND stack.type = %s
        LEFT JOIN biota_properties_int wield  ON wield.object_Id = b.id AND wield.type = %s
        LEFT JOIN biota_properties_int valid  ON valid.object_Id = b.id AND valid.type = %s
        LEFT JOIN biota_properties_i_i_d container ON container.object_Id = b.id AND container.type = %s
        LEFT JOIN biota_properties_i_i_d wielder   ON wielder.object_Id = b.id AND wielder.type = %s
        WHERE container.value = %s OR wielder.value = %s
           OR container.value IN (
                SELECT object_Id FROM biota_properties_i_i_d
                WHERE type = %s AND value = %s
              )
        """,
        (
            STRING_NAME,
            DID_ICON,
            INT_STACK_SIZE,
            INT_CURRENT_WIELDED_LOCATION,
            INT_VALID_LOCATIONS,
            IID_CONTAINER,
            IID_WIELDER,
            character_id,
            character_id,
            IID_CONTAINER,
            character_id,
        ),
    )

    equipped: list[dict] = []
    by_container: dict[int, list[dict]] = {}
    container_names: dict[int, str] = {}

    for row in rows:
        item = {
            "id": row["id"],
            "name": row["name"] or "(unnamed)",
            "iconId": row["icon"],
            "icon": f"/assets/icons/item/{row['icon']}.png"
            if row["icon"]
            else "/assets/icons/placeholder.png",
            "stack": int(row["stack_size"] or 1),
            "cat": "util",
        }

        # WeenieType.Container == 21. A pack is both an item and a place items live, so it is
        # recorded on both sides: it appears in the character's own grid AND gets its own tab.
        if row["weenie_Type"] == 21 and row["container_id"] == character_id:
            container_names[row["id"]] = item["name"]

        if row["wielder_id"] == character_id:
            equipped.append(
                {
                    **item,
                    "slot": _slot_name(row["wielded_location"]),
                    # The raw mask travels too, so the front-end can get more specific later
                    # (a full doll with ring/neck/cloak tiles) without a backend change.
                    "wieldedLocation": row["wielded_location"],
                }
            )
            continue

        parent = row["container_id"]

        if parent is not None:
            by_container.setdefault(parent, []).append(item)

    containers = [
        {
            "id": character_id,
            "name": "Main Pack",
            "items": sorted(by_container.get(character_id, []), key=lambda i: i["name"]),
        }
    ]

    # A character can carry several identical packs — Black Breath has two both named "Pack" —
    # and two tabs reading "Pack" is a UI the player cannot navigate. Numbering only kicks in
    # when a name actually repeats, so the common single-Sack case stays clean.
    name_counts: dict[str, int] = {}

    for cname in container_names.values():
        name_counts[cname] = name_counts.get(cname, 0) + 1

    seen: dict[str, int] = {}

    for cid, cname in sorted(container_names.items(), key=lambda kv: (kv[1], kv[0])):
        label = cname

        if name_counts[cname] > 1:
            seen[cname] = seen.get(cname, 0) + 1
            label = f"{cname} {seen[cname]}"

        containers.append(
            {
                "id": cid,
                "name": label,
                "items": sorted(by_container.get(cid, []), key=lambda i: i["name"]),
            }
        )

    return {"equipped": equipped, "containers": containers}


# EquipMask (ACE.Entity.Enum.EquipMask) -> the eight paperdoll slots the mockup draws.
#
# Coarse on purpose: the client has thirty-odd equip bits and the paperdoll has eight tiles, so
# clothing and armour covering the same body area collapse onto one slot.
#
# ORDER IS THE WHOLE DESIGN, AND IT IS NOT THE OBVIOUS ONE. A garment's CurrentWieldedLocation
# is every area it covers, not one slot — a hooded robe sets HeadWear *and* ChestWear *and*
# AbdomenWear *and* the leg bits. So a head-first scan puts every robe in the helmet tile, which
# is exactly what the first version of this did to Black Breath's Pathwarden Robe. Torso first,
# head LAST: a pure helmet sets only HeadWear and still lands correctly, while anything that
# also covers a body wins the more informative slot.
_SLOTS = [
    (0x00100000 | 0x02000000, "weapon"),                            # MeleeWeapon, TwoHanded
    (0x00200000, "shield"),                                         # Shield
    (0x00400000 | 0x01000000, "wand"),                              # MissileWeapon, Held (casters)
    (0x00000002 | 0x00000004 | 0x00000200 | 0x00000400, "chest"),   # chest/abdomen wear + armor
    (0x00000040 | 0x00000080 | 0x00002000 | 0x00004000, "legs"),    # leg wear + armor
    (0x00000008 | 0x00000010 | 0x00000020 | 0x00000800 | 0x00001000, "hands"),  # arms + hands
    (0x00000100, "feet"),                                           # FootWear
    (0x00000001, "head"),                                           # HeadWear — last, see above
]


def _slot_name(wielded_location: int | None) -> str:
    """A paperdoll slot, or "other" for jewellery and cloaks the eight-tile doll cannot show.

    "other" is deliberately not forced into a tile. Necklaces, rings, bracelets, trinkets and
    cloaks are five more equip bits with nowhere to go, and dropping one on top of the armour
    the player actually cares about would be worse than listing it separately.
    """
    if not wielded_location:
        return "other"

    for mask, name in _SLOTS:
        if wielded_location & mask:
            return name

    return "other"


# ---------------------------------------------------------------------------------------------
# the two payloads
# ---------------------------------------------------------------------------------------------


def _identity(raw: dict) -> dict:
    char = raw["char"]
    ints = raw["ints"]

    return {
        "id": char["id"],
        "name": char["name"],
        "marker": _marker(raw["bools"]),
        "gender": curves.enum_label("gender", ints.get(INT_GENDER)),
        "heritage": curves.enum_label("heritage", ints.get(INT_HERITAGE)),
        "pkStatus": curves.enum_label("playerKillerStatus", ints.get(INT_PLAYER_KILLER_STATUS)),
        "level": ints.get(INT_LEVEL, 1) or 1,
        "totalXP": raw["int64s"].get(INT64_TOTAL_EXPERIENCE, 0) or 0,
    }


def build_public(raw: dict, dials: dict) -> dict:
    """The no-login payload: identity, level, skills, credits, titles. NOTHING ELSE.

    Every key here is written out by hand. That is the point — a field added to build_private()
    cannot appear on a public page unless somebody adds it to this literal too, on purpose.
    """
    identity = _identity(raw)
    skills = build_skills(raw)
    titles = build_titles(raw)

    total_xp = identity["totalXP"]
    _, to_next_level = curves.level_progress(total_xp, identity["level"])

    return {
        "id": identity["id"],
        "name": identity["name"],
        "marker": identity["marker"],
        "title": next((t["name"] for t in titles if t["active"]), None),
        "gender": identity["gender"],
        "heritage": identity["heritage"],
        "level": identity["level"],
        "totalXP": total_xp,
        "xpToNextLevel": to_next_level,
        # Group and rank only — no XP-into-rank detail, matching "skills + true ranks" in 123.
        "skills": [
            {
                "key": s["key"],
                "id": s["id"],
                "label": s["label"],
                "cat": s["cat"],
                "group": s["group"],
                "trueRank": s["trueRank"],
                "icon": s["icon"],
            }
            for s in skills
        ],
        "skillCredits": build_skill_credits(raw, skills),
        "titles": [{"name": t["name"], "active": t["active"]} for t in titles],
        "public": True,
    }


def build_private(cur, raw: dict, dials: dict, now: float) -> dict:
    """The full object, for a logged-in owner looking at their own character.

    ON `buffed`
    -----------
    Every `buffed` here equals `base`. Buffs are live enchantment state
    (`biota_properties_enchantment_registry`), and this sheet reads a snapshot that is up to
    `player_save_interval` (300s) old — so a "buffed" number taken from it would be a five-minute
    old spell duration presented as current, which is worse than not showing one. The field is
    populated rather than omitted because the mockup renders a delta ONLY when buffed differs
    from base, so equality degrades to exactly the right thing with no front-end change, and the
    field is there the day live buffs are worth adding.
    """
    identity = _identity(raw)
    char = raw["char"]

    skills = build_skills(raw)
    titles = build_titles(raw)

    total_xp = identity["totalXP"]
    _, to_next_level = curves.level_progress(total_xp, identity["level"])

    return {
        **identity,
        "title": next((t["name"] for t in titles if t["active"]), None),
        "xpToNextLevel": to_next_level,
        "unassignedXP": raw["int64s"].get(INT64_AVAILABLE_EXPERIENCE, 0) or 0,
        # last_Login_Timestamp is a Unix double. Handed over as a number rather than a formatted
        # string so the browser renders it in the reader's own timezone.
        "lastLogin": float(char["last_Login_Timestamp"] or 0) or None,
        "totalLogins": char["total_Logins"] or 0,
        "playtimeSeconds": raw["ints"].get(INT_AGE, 0) or 0,
        # Filled in by the app layer from the live status feed — the shard cannot know it.
        "online": False,
        "location": build_location(raw),
        "attributes": build_attributes(raw, dials),
        "vitals": build_vitals(raw),
        "skills": skills,
        "skillCredits": build_skill_credits(raw, skills),
        "titles": titles,
        "inventory": build_inventory(cur, char["id"]),
        "quests": build_quests(raw, now),
        "public": False,
    }
