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

import datetime
import functools
from pathlib import Path
from typing import Any

from . import curves, db, enchantments, items, names

DATA_DIR = Path(__file__).parent / "data"


@functools.lru_cache(maxsize=1)
def _icon_version() -> str:
    """A cache-busting stamp that changes whenever the icons are re-exported.

    Caddy serves /assets/* as `immutable` with a week's max-age, which is right for item icons —
    their filename IS the IconId, so a given URL's bytes never change. It is WRONG for the named
    ones: `/assets/icons/attribute/strength.png` is keyed by name, so re-pointing Strength at a
    different texture leaves every browser that has already visited holding the old picture for
    a week. That is exactly what happened when the drawn placeholders were replaced with the real
    dat icons — the deploy was correct and the page still showed the old tiles.

    So the URL carries the version instead of the header being weakened.

    THE STAMP IS THE NEWEST OF TWO FILES, and the second one was added after this went wrong a
    second time (131). It used to read icon-map.json alone — but icon-map.json is written by the
    exporter's TABLES pass, not its ICONS pass. An icons-only run therefore added ~12,000 files and
    moved nothing, so any browser that had already cached a 404 for a previously-missing icon went
    on showing the fallback tile. `icon-set.json` is written by the icons pass for exactly this
    reason, so either kind of export now moves the stamp.
    """
    newest = 0

    for name in ("icon-map.json", "icon-set.json"):
        try:
            newest = max(newest, (DATA_DIR / name).stat().st_mtime)
        except OSError:
            continue

    return str(int(newest))


def icon_url(path: str) -> str:
    return f"{path}?v={_icon_version()}"


def iso(timestamp: float | int | None) -> str | None:
    """Unix seconds -> an ISO 8601 UTC string, or None.

    CONTRACT 1 SAYS `lastLogin(ISO)`, AND THE FIRST BUILD OF THIS FILE IGNORED IT. Handing over
    the raw Unix double looked defensible in isolation — let the browser localise it — but the
    front-end was written against the contract and does `new Date(value)`, which reads a bare
    number as MILLISECONDS. Every timestamp landed in January 1970.

    The quest list was worse than wrong, it was fatal: it sorts with
    `(b.lastCompleted||'').localeCompare(...)`, and `localeCompare` does not exist on a Number.
    That threw inside the sort, so `renderQuests` died, so `mountSheet` died, so nothing after it
    rendered — the entire character sheet was blank.

    So every timestamp crossing this boundary goes through here. Seconds precision, `Z`, no
    offset: unambiguous to `new Date()` in every browser, and the shard has nothing finer worth
    reporting anyway.
    """
    if not timestamp:
        return None

    return datetime.datetime.fromtimestamp(
        float(timestamp), tz=datetime.timezone.utc
    ).strftime("%Y-%m-%dT%H:%M:%SZ")

# --- property ids we read (ACE.Entity.Enum.Properties) ---------------------------------------

INT_LEVEL = 25
INT_AVAILABLE_SKILL_CREDITS = 24
INT_GENDER = 113
INT_HERITAGE = 188
# 127 #5: this was 133, which is ShowableOnRadar. Black Breath's radar value happens to be 4,
# and 4 in the PlayerKillerStatus enum is "PK" - so a Non-Player Killer was labelled a killer on
# their own character sheet, from a property that has nothing to do with PK at all. The two enums
# overlapping on a plausible-looking value is what made it survive review.
INT_PLAYER_KILLER_STATUS = 134
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
# 127 #4: Aetheria are the same base gem with a coloured OVERLAY, so the overlay is what
# distinguishes red/blue/yellow.
DID_ICON_OVERLAY = 50

# 158 item 4: the slot an item sits in. ACE orders every container by it and even resyncs it
# (`Container.cs:176`), so the shard DOES store the arrangement the player made - the portal was
# sorting alphabetically over the top of it.
INT_PLACEMENT_POSITION = 53
# ItemsCapacity - what the client states as "Can hold up to (24) items."
INT_ITEMS_CAPACITY = 6
# The main pack is not a container row, so its size is not stored anywhere: 6 x 17 in the client.
MAIN_PACK_SLOTS = 102

IID_CONTAINER = 2
IID_WIELDER = 3

POSITION_LOCATION = 1

# PropertyAttribute, IN THE ORDER THE CLIENT'S PANEL LISTS THEM.
#
# The enum is Strength(1), Endurance(2), Quickness(3), Coordination(4), Focus(5), Self(6) — but
# the character panel shows Coordination ABOVE Quickness, so sorting by id puts two rows in the
# wrong place for anyone comparing the page against the game. The enum's own comment warns about
# this pair specifically: "The order of quickness and coordination corresponds to the client".
ATTRIBUTE_IDS = [1, 2, 4, 3, 5, 6]
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
        # 136: live buffs, so attributes, vitals, skills and burden read as the client reads them.
        # Already filtered to the unexpired here, so every consumer downstream can just use it.
        "enchantments": enchantments.load(cur, character_id),
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


def _attribute_currents(attributes: list[dict], ench: list[dict]) -> dict[int, int]:
    """The same map, BUFFED — what the client actually feeds the formulas.

    136: `AttributeFormula.GetFormula` has a `current` flag defaulting to true, and CreatureSkill
    uses the current variant for its displayed value while using the base variant for Base. So a
    skill moves when a Strength buff lands, even with no skill buff of its own. Passing this map
    instead of the base one IS the current variant — the formula itself does not change.
    """
    return {
        aid: enchantments.attribute_current(base, ench, aid)
        for aid, base in _attribute_bases(attributes).items()
    }


def build_attributes(raw: dict, dials: dict, ench: list[dict]) -> list[dict]:
    out = []

    by_id = {row["type"]: row for row in raw["attributes"]}

    # Emitted in the client's panel order, not the enum's - see ATTRIBUTE_IDS.
    for attr_id in ATTRIBUTE_IDS:
        row = by_id.get(attr_id)

        if row is None:
            continue

        starting = row["init_Level"] or 0
        cp_spent = row["c_P_Spent"] or 0

        rank, into, to_next, max_ranks = curves.attribute_progress(
            cp_spent, dials["attributes_start_at_ten"], dials["attribute_max_value"]
        )

        base = starting + rank

        # 136: what the client shows. It prints the buffed number in green with the delta beneath,
        # so both travel: `value` stays the base the character owns, `buffed` is what is in force.
        buffed = enchantments.attribute_current(base, ench, attr_id)

        key = (curves.enum_label("attribute", attr_id) or f"attr{attr_id}").lower()

        out.append(
            {
                "key": key,
                "label": curves.enum_label("attribute", attr_id),
                "cat": "magic" if attr_id in (5, 6) else ("def" if attr_id == 2 else "phys"),
                "base": base,
                # 136: `buffed` used to be a placeholder equal to `base`, waiting for live buffs
                # to be worth reading. They are now read, so it carries the real figure.
                "buffed": buffed,
                "buff": buffed - base,
                "trueRank": rank,
                "maxRank": max_ranks,
                "startingValue": starting,
                "xpSpent": cp_spent,
                "xpIntoRank": into,
                "xpToNextRank": to_next,
                "icon": icon_url(f"/assets/icons/attribute/{key}.png"),
            }
        )

    return out


def build_vitals(raw: dict, ench: list[dict]) -> list[dict]:
    """The three vitals.

    004 holds these at the same fraction of their ceiling as the attribute that governs them —
    they earn nothing of their own — so Base is StartingValue + Ranks + formula(attributes),
    with the formula read from the dat (data/vitals.json). `current` is the stored current
    level, which is genuinely what the character had at their last save and so can sit below max.
    """
    formulas = curves.vital_formulas()
    bases = _attribute_bases(raw["attributes"])
    # A vital's ceiling follows its governing attribute, so a buffed attribute raises it even with
    # no vital buff at all - which is why the buffed max is computed from the buffed attributes.
    currents = _attribute_currents(raw["attributes"], ench)

    out = []

    for row in sorted(raw["vitals"], key=lambda r: r["type"]):
        vital_id = row["type"]

        if vital_id not in VITAL_KEYS:
            continue

        key, label, formula_key = VITAL_KEYS[vital_id]

        formula = formulas.get(formula_key, {})

        base = (
            (row["init_Level"] or 0)
            + (row["level_From_C_P"] or 0)
            + curves.apply_formula(formula, bases)
        )

        buffed_max = enchantments.vital_current_max(
            (row["init_Level"] or 0) + (row["level_From_C_P"] or 0)
            + curves.apply_formula(formula, currents),
            ench, vital_id,
        )

        # WHICH ATTRIBUTE THIS VITAL FOLLOWS, named rather than assumed.
        #
        # 004 holds vitals at the same fraction of their ceiling as the governing attribute, and
        # the in-game text says so explicitly - *"each is held at the same fraction of its ceiling
        # as the attribute that governs it, so it rises when that attribute does"*. A player who
        # does not know WHICH attribute reads that as "my Health is stuck". The pairing lives in
        # the dat (health follows Endurance, mana follows Self), so it is read from there rather
        # than hard-coded here, and it survives a dat that ever disagreed with the assumption.
        governed_by = formula.get("attr1")

        if governed_by in (None, "Undef"):
            governed_by = None

        current = row["current_Level"] or 0

        out.append(
            {
                "key": key,
                "label": label,
                "cat": {"health": "def", "stamina": "phys", "mana": "magic"}[key],
                # `max` IS NOT SIMPLY `base`, AND THE DIFFERENCE IS VISIBLE.
                #
                # `base` is the UNBUFFED ceiling. `current_Level` is whatever the character had at
                # their last save, buffs included — so a buffed character saves with current ABOVE
                # base, and the page once rendered "Health 205/199", a ratio that cannot exist.
                #
                # 136 makes the real ceiling computable: enchantments are read now, and a vital's
                # ceiling follows its governing attribute, so `buffed_max` accounts for both a
                # direct vital buff and an attribute buff underneath it. The max(...) that follows
                # is no longer an estimate standing in for the truth — it is a floor guarding
                # against a save whose current outran what we can reconstruct.
                "current": current,
                "max": max(buffed_max, current),
                "baseMax": base,
                "base": base,
                "buff": buffed_max - base,
                "ranks": row["level_From_C_P"] or 0,
                "xpSpent": row["c_P_Spent"] or 0,
                "governedBy": governed_by,
                "icon": icon_url(f"/assets/icons/vital/{key}.png"),
            }
        )

    return out


def build_skills(raw: dict, ench: list[dict]) -> list[dict]:
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
    currents = _attribute_currents(raw["attributes"], ench)
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
        buffed = base

        if meta.get("usableUntrained") or sac >= curves.SAC_TRAINED:
            base += curves.apply_formula(meta.get("formula", {}), bases)
            # The buffed figure feeds the formula BUFFED attributes, then applies the skill's own
            # enchantments on top - CreatureSkill.Current in that order.
            buffed += curves.apply_formula(meta.get("formula", {}), currents)

        buffed = enchantments.skill_current(buffed, ench, skill_id)

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
                "buffed": buffed,
                "buff": buffed - base,
                "trueRank": rank,
                "xpSpent": xp,
                "xpIntoRank": into,
                "xpToNextRank": to_next,
                # SpecializedCost includes the trained cost; the upgrade is what a trained skill
                # actually pays, and quoting the raw figure would overstate it.
                "specCost": spec_cost,
                "canSpecialize": can_specialize,
                "icon": icon_url(f"/assets/icons/skill/{skill_id}.png"),
            }
        )

    # ALPHABETICAL, because that is how the in-game skill panel orders each group — Melee
    # Defense, Summoning, Two Handed Combat. Sorted by rank first and by value second, both of
    # which were guesses at what "sorted" should mean; the game had already answered it. The
    # front-end groups and re-sorts anyway, so this is about the payload reading sensibly on its
    # own rather than about what the page shows.
    out.sort(key=lambda s: s["label"])

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
                # ISO, like every other timestamp here. The front-end sorts these with
                # localeCompare, which a Number does not have — see iso().
                "lastCompleted": iso(last),
                "availableAt": iso(available_at),
            }
        )

    # Sorted here rather than trusting the client to: most recently completed first.
    out.sort(key=lambda q: (q["lastCompleted"] or ""), reverse=True)

    return out


def _in_order(items: list[dict]) -> list[dict]:
    """Container order, the way ACE sends it: by PlacementPosition.

    Name is the tie-break only, for the case where placement is missing or duplicated — it keeps
    the result stable between refreshes rather than letting equal keys shuffle.
    """
    return sorted(items, key=lambda i: (i.get("placement") is None,
                                        i.get("placement") or 0,
                                        i.get("name") or ""))


def build_inventory(cur, character_id: int, strength: int, dials: dict,
                    ench: list[dict] | None = None) -> dict:
    """The paperdoll and the packs, with every item carrying its own examine text.

    Two queries instead of one join-per-property. The first finds the objects; the second pulls
    EVERY int/float/string/did/spell row for exactly those ids in one pass. That is deliberate:
    127 asks for the full examine panel, which touches ~20 properties across four tables, and
    LEFT JOINing each one would have turned a readable query into twenty joins and a row per
    combination. Fetching the property rows wholesale and grouping them in Python is both faster
    and the only version anyone will be able to change later.
    """
    rows = db.fetch_all(
        cur,
        """
        SELECT b.id, b.weenie_Type,
               container.value AS container_id,
               wielder.value   AS wielder_id
        FROM biota b
        LEFT JOIN biota_properties_i_i_d container ON container.object_Id = b.id AND container.type = %s
        LEFT JOIN biota_properties_i_i_d wielder   ON wielder.object_Id = b.id   AND wielder.type = %s
        WHERE container.value = %s OR wielder.value = %s
           OR container.value IN (
                SELECT object_Id FROM biota_properties_i_i_d
                WHERE type = %s AND value = %s
              )
        """,
        (IID_CONTAINER, IID_WIELDER, character_id, character_id, IID_CONTAINER, character_id),
    )

    if not rows:
        return {"equipped": [], "containers": [], "burden": None}

    ids = [r["id"] for r in rows]
    marks = ",".join(["%s"] * len(ids))

    def props(table: str, value_col: str = "value") -> dict[int, dict]:
        out: dict[int, dict] = {}

        for r in db.fetch_all(
            cur,
            f"SELECT object_Id, type, {value_col} AS v FROM {table} WHERE object_Id IN ({marks})",
            tuple(ids),
        ):
            out.setdefault(r["object_Id"], {})[r["type"]] = r["v"]

        return out

    ints = props("biota_properties_int")
    floats = props("biota_properties_float")
    strings = props("biota_properties_string")
    dids = props("biota_properties_d_i_d")
    # 158: item BOOLS were never loaded - only the character's - which is why "Properties: Retained"
    # could not be rendered no matter what the panel did with it. Retained is PropertyBool 91.
    bools = props("biota_properties_bool")
    # 158: item INT64s were never loaded either - ItemTotalXp and ItemBaseXp live here, which is why
    # a cloak's "Item Level: 1 / 3" and "Item XP:" lines could not be rendered at all.
    int64s = props("biota_properties_int64")

    spells: dict[int, list[int]] = {}

    for r in db.fetch_all(
        cur,
        f"SELECT object_Id, spell FROM biota_properties_spell_book WHERE object_Id IN ({marks})",
        tuple(ids),
    ):
        spells.setdefault(r["object_Id"], []).append(r["spell"])

    equipped: list[dict] = []
    by_container: dict[int, list[dict]] = {}
    container_names: dict[int, tuple[str, str]] = {}
    foci: list[dict] = []
    # An item's own buffs live on the ITEM, and AppraiseInfo reads them alongside the wielder's.
    # One bulk query rather than one per row.
    item_ench = enchantments.load_many(cur, ids)

    total_burden = 0

    for row in rows:
        oid = row["id"]
        i, f, st, d = ints.get(oid, {}), floats.get(oid, {}), strings.get(oid, {}), dids.get(oid, {})

        icon_id = d.get(DID_ICON)
        stack = int(i.get(INT_STACK_SIZE) or 1)
        # 158: the wielder's enchantments go in, because AppraiseInfo merges them BEFORE the
        # client is sent a number - a stored WeaponDefense of 1.17 displays as +32.0% while a
        # +0.15 aura is up. Only WIELDED items are affected; nothing in a pack is enchanted by
        # the character carrying it.
        wielded = row.get("wielder_id") is not None
        detail = items.build_detail(i, f, st, spells.get(oid, []),
                                    ench if wielded else None, item_ench.get(oid), d,
                                    bools.get(oid, {}), int64s.get(oid, {}),
                                    row.get("weenie_Type"), dials)

        item = {
            "id": oid,
            # Ordering, not decoration - see INT_PLACEMENT_POSITION.
            "placement": i.get(INT_PLACEMENT_POSITION),
            "name": items.display_name(st.get(STRING_NAME) or "(unnamed)", i, f),
            "iconId": icon_id,
            "icon": icon_url(
                f"/assets/icons/item/{icon_id}.png" if icon_id
                else "/assets/icons/placeholder.png"
            ),
            "stack": stack,
            "cat": "util",
            # BOTH SHAPES, and both are load-bearing. The front-end renders `it.desc` (array or
            # newline string) and shows "Full item description coming soon." for anything without
            # it, so `desc` is what actually reaches the page. `detail` carries the same facts
            # named and typed, so a later layout can position armour level or spells properly
            # instead of parsing sentences back apart.
            "detail": detail,
            "desc": detail["lines"],
            # Which of those lines are genuinely above base, so the page colours what the game
            # colours. Previously the front-end guessed from the label and was wrong on every
            # unbuffed weapon.
            "descBuffed": detail.get("buffedLines") or [],
        }

        # 127 #4: Aetheria are identified by an icon OVERLAY rather than a different icon, so the
        # overlay id has to travel or all three sigils render as the same base gem.
        if (overlay := d.get(DID_ICON_OVERLAY)):
            item["iconOverlayId"] = overlay
            item["iconOverlay"] = icon_url(f"/assets/icons/item/{overlay}.png")

        # NOT multiplied by the stack. EncumbranceVal is ALREADY the total for the object as it
        # stands - 1,000 Prismatic Tapers store 6,000, and StackUnitEncumbrance (type 13) holds
        # the 6 per taper. Multiplying by the stack double-counts by the stack size, which put
        # Black Breath at 49,520% burden before this was checked against a real stack.
        total_burden += int(i.get(items.INT_ENCUMBRANCE) or 0)

        if row["weenie_Type"] == 21 and row["container_id"] == character_id:
            container_names[oid] = (item["name"], item["icon"], item.get("placement"))

        # A focus occupies a pack slot and holds nothing (AC wiki, Spell Components#Foci), so it
        # belongs in the pack BAR rather than loose in the main grid where it looked like cargo.
        elif items.is_focus(i) and row["container_id"] == character_id:
            foci.append({**item, "focus": True})
            continue

        if row["wielder_id"] == character_id:
            wielded = i.get(INT_CURRENT_WIELDED_LOCATION)

            equipped.append(
                {
                    **item,
                    # ItemType goes in so the clothing layer (shirt/pants) separates from the
                    # armour it sits under - 131. Without it a shirt just reports `chest`.
                    "slot": items.slot_name(wielded, i.get(INT_ITEM_TYPE)),
                    # 127 #2: EVERY area this covers, not just one. A robe reports eight.
                    "coverage": items.coverage(wielded),
                    "wieldedLocation": wielded,
                }
            )
            continue

        parent = row["container_id"]

        if parent is not None:
            by_container.setdefault(parent, []).append(item)

    focus_ids = {f["id"] for f in foci}

    # `_in_order`, not by name. ACE sends every container ordered by PlacementPosition, so
    # alphabetical was the portal inventing an arrangement over the one the player made.
    containers = [
        {
            "id": character_id,
            "name": "Main Pack",
            "icon": icon_url("/assets/icons/ui/mainpack.png"),
            # Packs and foci are EXCLUDED. ACE keeps two sequences - `!UseBackpackSlot` and
            # `UseBackpackSlot` - each numbered from 0 (Container.cs:176), and the client draws
            # the second as the PACKS column, not as contents. Listing them here put every pack
            # in the grid AND in the strip, and interleaved two independent 0..N sequences, which
            # is why placements read 0,0,1,1,6,6 instead of ascending once.
            "items": _in_order([it for it in by_container.get(character_id, [])
                                if it["id"] not in container_names and it["id"] not in focus_ids]),
            "slots": MAIN_PACK_SLOTS,
        }
    ]

    # A character can carry several identical packs - Black Breath has two both named "Pack" -
    # and two tabs reading "Pack" is a UI the player cannot navigate. Numbering only kicks in
    # when a name actually repeats, so the common single-Sack case stays clean.
    name_counts: dict[str, int] = {}

    for cname, _, _ in container_names.values():
        name_counts[cname] = name_counts.get(cname, 0) + 1

    seen: dict[str, int] = {}

    # ORDERED BY PlacementPosition, not by name. ACE runs a second sequence for `UseBackpackSlot`
    # items - `sidPackItems ... OrderBy(wo => wo.PlacementPosition)` (Container.cs:179) - so the
    # pack BAR has an arrangement the player chose, exactly as the grid does. Sorting it
    # alphabetically put "Pack" before "Sack" regardless of where they actually sit.
    #
    # The numbering below still uses the name, so a player with two "Pack"s sees "Pack 1"/"Pack 2"
    # in the order they carry them rather than in an order invented here.
    def _pack_key(kv):
        _cid, (_cname, _cicon, pos) = kv
        return (pos is None, pos or 0, _cid)

    for cid, (cname, cicon, _pos) in sorted(container_names.items(), key=_pack_key):
        label = cname

        if name_counts[cname] > 1:
            seen[cname] = seen.get(cname, 0) + 1
            label = f"{cname} {seen[cname]}"

        containers.append(
            {
                "id": cid,
                "name": label,
                "icon": cicon,
                "items": _in_order(by_container.get(cid, [])),
                # Drawn to the pack's OWN size, which is what the client states on it.
                "slots": (ints.get(cid, {}) or {}).get(INT_ITEMS_CAPACITY),
            }
        )

    burden = _burden(total_burden, strength, dials)

    # Foci come after the packs, and in PlacementPosition order like everything else.
    #
    # The note that used to sit here said the shard "stores no order for that, so any order here is
    # our choice rather than theirs". That was wrong, and it is worth recording as wrong: the order
    # is in PropertyInt 53 and ACE resyncs it on every load. Believing it absent is what left both
    # the grid and this bar sorted alphabetically.
    for focus in _in_order(foci):
        containers.append(
            {
                "id": focus["id"],
                "name": focus["name"],
                "icon": focus["icon"],
                "focus": True,
                # Empty by definition, not by accident.
                "items": [],
                "note": "A focus fills a pack slot and holds nothing.",
            }
        )

    return {
        "equipped": equipped,
        "containers": containers,
        # `burden` is the PERCENT AS A NUMBER, because that is what the front-end consumes -
        # it renders `burden + '%'` straight into the meter, and handing it the detail object
        # printed "Burden [object Object]%" on the live page. The detail travels alongside for
        # anything that wants the actual figures.
        "burden": burden["percent"],
        "burdenDetail": burden,
    }


def _burden(carried: int, strength: int, dials: dict) -> dict:
    """127 #7: burden, matching the number the CLIENT shows.

    `carried` is the plain sum of every item's EncumbranceVal - NOT multiplied by stack size,
    because that property is already the total for the object as it stands (1,000 Prismatic
    Tapers store 6,000; StackUnitEncumbrance holds the 6 per taper). Containers are not
    double-counted either: a pack stores only its own 15.

    Capacity mirrors `Player.GetEncumbranceCapacity()` - `150 * Strength` - because that is the
    figure the server sends the client as PropertyInt.EncumbranceCapacity, and the page's job is
    to agree with what the player sees in game.

    ONE THING THIS IS KNOWINGLY APPROXIMATE ABOUT:
    `AugmentationIncreasedCarryingCapacity` would raise capacity by 30 x Strength per
    augmentation, and is not read. It errs the safe way - reporting MORE burden than the player
    has - for a number nobody should act on from a web page.

    The other approximation is gone: 136 reads live enchantments, so `strength` arriving here is
    Strength.CURRENT and the figure now agrees with the client.

    NOTE FOR THE SERVER, not applied here: 009's `burden_capacity_floor` is added inside
    EncumbranceSystem.EncumbranceCapacity, which drives the physics/movement penalty, while
    Player.GetEncumbranceCapacity has its own copy of the formula WITHOUT it. So the floor
    currently moves the penalty but not the capacity the client displays, which is not what 009's
    own comment says it intended. Flagged in Task.md 127 rather than papered over here.
    """
    capacity = 150 * max(0, strength)

    return {
        "carried": carried,
        "capacity": capacity,
        "percent": round(100 * carried / capacity, 1) if capacity > 0 else None,
        # True when the figure is unbuffed base Strength, so the front-end can hedge if it wants.
        "approximate": True,
    }


# ---------------------------------------------------------------------------------------------
# the two payloads
# ---------------------------------------------------------------------------------------------


# The game spells these out; the enum does not. "NPK" on a character sheet is jargon, and the
# front-end colours on the value, so it needs to be able to tell a real killer from everyone else.
PK_STATUS_NAMES = {
    0: None,                    # Undef - nothing worth saying
    1: "Protected",
    2: "Non-Player Killer",
    4: "Player Killer",
    8: "Unprotected",
    0x40: "Player Killer Lite",
}


def _strength(raw: dict, ench: list[dict]) -> int:
    """The character's Strength as the SERVER sees it, which is what burden capacity uses.

    136: this used to return base Strength, and `Player.GetEncumbranceCapacity()` uses
    Strength.**Current**. Black Breath is buffed +35, so the page divided by 33,000 where the game
    divided by 38,250 and reported 54% against the client's 46% — near enough to 100 between them
    that it read as an inverted number rather than an unbuffed one.
    """
    for row in raw["attributes"]:
        if row["type"] == 1:
            base = (row["init_Level"] or 0) + (row["level_From_C_P"] or 0)
            return enchantments.attribute_current(base, ench, 1)

    return 0


def _pk_status(value: int | None) -> str | None:
    if value is None:
        return None

    if value in PK_STATUS_NAMES:
        return PK_STATUS_NAMES[value]

    return curves.enum_label("playerKillerStatus", value)


def _identity(raw: dict) -> dict:
    char = raw["char"]
    ints = raw["ints"]

    return {
        "id": char["id"],
        "name": char["name"],
        "marker": _marker(raw["bools"]),
        "gender": curves.enum_label("gender", ints.get(INT_GENDER)),
        "heritage": curves.enum_label("heritage", ints.get(INT_HERITAGE)),
        "pkStatus": _pk_status(ints.get(INT_PLAYER_KILLER_STATUS)),
        "level": ints.get(INT_LEVEL, 1) or 1,
        "totalXP": raw["int64s"].get(INT64_TOTAL_EXPERIENCE, 0) or 0,
    }


def build_public(raw: dict, dials: dict) -> dict:
    """The no-login payload: identity, level, skills, credits, titles. NOTHING ELSE.

    Every key here is written out by hand. That is the point — a field added to build_private()
    cannot appear on a public page unless somebody adds it to this literal too, on purpose.
    """
    identity = _identity(raw)
    ench = raw.get("enchantments") or []
    skills = build_skills(raw, ench)
    titles = build_titles(raw)

    total_xp = identity["totalXP"]
    into_level, to_next_level = curves.level_progress(total_xp, identity["level"])

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
        # 141: the in-game panels show a LEVEL progress bar above both stat tabs, so the portal
        # needs the other half of the pair to draw one honestly rather than guess a width.
        "xpIntoLevel": into_level,
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

    ON `buffed` (rewritten by 136)
    ------------------------------
    `buffed` used to equal `base` always. The reasoning was that enchantments are live state and
    this sheet reads a snapshot up to `player_save_interval` (300s) old, so a buffed number here
    could be a stale spell presented as current.

    That was the wrong call, and the cost showed up as a bug report: the page said 54% burden
    where the game said 46%, which read as an inverted number and was only an unbuffed one. The
    same gap ran through every attribute, vital and skill — the page was quietly disagreeing with
    the client everywhere, and silence about it was worse than the staleness it was avoiding.

    Buffs are now read and applied. The staleness concern was real but small: a spell that lapsed
    within the save window reads as still active for a few minutes, which is a far better error
    than being wrong about every number on the page at all times.
    """
    identity = _identity(raw)
    char = raw["char"]

    ench = raw.get("enchantments") or []
    skills = build_skills(raw, ench)
    titles = build_titles(raw)

    total_xp = identity["totalXP"]
    into_level, to_next_level = curves.level_progress(total_xp, identity["level"])

    return {
        **identity,
        "title": next((t["name"] for t in titles if t["active"]), None),
        "xpToNextLevel": to_next_level,
        # 141: the in-game panels show a LEVEL progress bar above both stat tabs, so the portal
        # needs the other half of the pair to draw one honestly rather than guess a width.
        "xpIntoLevel": into_level,
        "unassignedXP": raw["int64s"].get(INT64_AVAILABLE_EXPERIENCE, 0) or 0,
        # ISO 8601 UTC, per Contract 1's `lastLogin(ISO)` — see iso() for what emitting the raw
        # Unix double instead cost.
        "lastLogin": iso(char["last_Login_Timestamp"]),
        "totalLogins": char["total_Logins"] or 0,
        "playtimeSeconds": raw["ints"].get(INT_AGE, 0) or 0,
        # Filled in by the app layer from the live status feed — the shard cannot know it.
        "online": False,
        "location": build_location(raw),
        "attributes": build_attributes(raw, dials, ench),
        "vitals": build_vitals(raw, ench),
        "skills": skills,
        "skillCredits": build_skill_credits(raw, skills),
        "titles": titles,
        "inventory": build_inventory(cur, char["id"], _strength(raw, ench), dials, ench),
        "quests": build_quests(raw, now),
        "public": False,
    }
