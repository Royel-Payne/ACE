#!/usr/bin/env python3
"""Shadowgain 158 — diff the portal's examine panel against the one the client is actually sent.

    In game (Developer):  sg-appraise Orb          -> sg-appraise-Orb-<guid>.json
    Here:                 python appraise-diff.py sg-appraise-Orb-<guid>.json <character-id>

WHY THIS EXISTS

`AppraiseInfo` does not hand the client stored values. It merges enchantments first — the item's
own and the wielder's, ADDED for some properties and MULTIPLIED for others — and only then sends
the result. my.shadowgain.com rebuilds that panel from the shard, which makes it a second
implementation of a calculation with several conventions in it.

Chasing the difference by eye worked, and kept working, which is the problem: a bonus printed as
-92%, armour 200 points light on every piece, a spell missing from the list, green highlighting on
items carrying no buff at all. Every one of those was found from a screenshot, one panel at a time.
This turns the comparison into something that either passes or does not, the same way
`objdesc-diff.py` did for the 3D model in 152.

THE COMPARISON IS RACY IN BOTH DIRECTIONS, AND BOTH HAVE BEEN MISTAKEN FOR BUGS

The oracle reads the LIVE WORLD; the portal reads the SHARD. Those are not the same instant, and
the gap runs both ways:

  * **The dump goes stale.** `sg-appraise` writes a snapshot; the portal is queried after. A
    character who re-buffs in between makes the portal look wrong. This produced nine convincing
    false findings on Black Breath - +200 ArmorLevel, every resistance pinned at 2.0 - against a
    ten-minute-old dump. **Pair each dump with its own diff, seconds apart.**

  * **The shard goes stale.** ACE holds biotas in memory and writes them on a timer, so a buff
    can be live in the world and absent from the database. Trees' Academy Spadone read 10 damage
    against the game's 22 with **no enchantment rows in the shard at all** - the portal was
    rendering the stored values correctly and had no way to know about the rest.

Neither is fixable here, and the second is not fixable at all: the portal cannot see what has not
been written. Before filing anything as a bug, check the registry rows in the shard. If they are
absent, the panel is right and the database is behind.

WHAT IT COMPARES, AND WHAT IT DELIBERATELY DOES NOT

Only the NUMBERS. Field order, wording and punctuation are the client's presentation and the portal
is not trying to be a pixel copy of it — but a value that disagrees is a bug every time. Each entry
below maps a key the portal emits in `detail` to the property AppraiseInfo carries.

Exit status is 0 when every mapped value agrees and 1 when any does not, so this can gate a deploy.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

# portal detail key -> (AppraiseInfo bucket, property name, tolerance)
#
# Floats carry a tolerance because the two sides arrive at them by different routes — the server
# accumulates in float, the portal in Python double — and 1e-6 is far below anything a panel shows.
# Each entry is (portal detail key, [(bucket, property), ...], tolerance, neutral).
#
# THE SOURCE LIST IS ORDERED AND THE FIRST NON-NULL WINS, because AppraiseInfo does not keep a
# property in one place. Weapon numbers are assembled into a `WeaponProfile` struct and are ABSENT
# from `propertiesInt`/`propertiesFloat`; resistances go into `ArmorProfile`. Pointing a weapon
# field at `propertiesInt` therefore compared a real portal value against `None` on every weapon in
# the game - which the old MAPPING did for Damage, WeaponTime and WeaponOffense.
#
# `weaponDefense` is the reason this is a LIST rather than a single corrected bucket: it appears in
# BOTH places, as float32 in the profile and float64 in propertiesFloat. Either is right; taking
# the profile first keeps a weapon compared against the struct the client actually reads.
#
# `neutral` is the value that means "no effect", which the portal omits rather than printing
# "+0%" - see the note on NEUTRAL below.
MAPPING = [
    ("value", [("propertiesInt", "Value")], 0, None),
    ("burden", [("propertiesInt", "EncumbranceVal")], 0, None),
    ("armorLevel", [("propertiesInt", "ArmorLevel")], 0, None),
    ("workmanship", [("propertiesInt", "ItemWorkmanship")], 0, None),
    ("spellcraft", [("propertiesInt", "ItemSpellcraft")], 0, None),
    ("mana.max", [("propertiesInt", "ItemMaxMana")], 0, None),
    ("activationDifficulty", [("propertiesInt", "ItemDifficulty")], 0, None),
    ("manaCost", [("propertiesInt", "ItemManaCost")], 0, None),
    ("cleaving", [("propertiesInt", "Cleaving")], 0, None),
    # MaterialType is compared as the NAME the portal renders, not the id.
    #
    # ItemCurMana IS DELIBERATELY NOT HERE. A worn magic item burns mana continuously, so the
    # oracle is stamped at the moment `sg-appraise` ran and the portal reads the shard seconds or
    # minutes later. Across 107 items it produced 48 "disagreements", every one of them the game
    # reading HIGHER than the web by an amount proportional to the pool - i.e. the clock, not a
    # bug. A check that fails on 45% of items for a reason nobody will ever fix is a check that
    # trains you to skim the output, which is the one thing an oracle must not do.

    ("damage.max", [("weaponProfile", "damage"), ("propertiesInt", "Damage")], 0, None),
    ("damage.variance", [("weaponProfile", "damageVariance"),
                         ("propertiesFloat", "DamageVariance")], 1e-6, None),
    ("weaponSpeed", [("weaponProfile", "weaponTime"), ("propertiesInt", "WeaponTime")], 0, None),
    ("damageMod", [("weaponProfile", "damageMod"), ("propertiesFloat", "DamageMod")], 1e-6, 1.0),
    ("attackMod", [("weaponProfile", "weaponOffense"),
                   ("propertiesFloat", "WeaponOffense")], 1e-6, 1.0),
    ("meleeDefenseMod", [("weaponProfile", "weaponDefense"),
                         ("propertiesFloat", "WeaponDefense")], 1e-6, 1.0),
    ("missileDefenseMod", [("propertiesFloat", "WeaponMissileDefense")], 1e-6, 1.0),
    ("magicDefenseMod", [("propertiesFloat", "WeaponMagicDefense")], 1e-6, 1.0),
    ("manaConversionMod", [("propertiesFloat", "ManaConversionMod")], 1e-6, None),
    ("elementalDamageMod", [("propertiesFloat", "ElementalDamageMod")], 1e-6, 1.0),
    ("criticalFrequency", [("propertiesFloat", "CriticalFrequency")], 1e-6, None),
    ("criticalMultiplier", [("propertiesFloat", "CriticalMultiplier")], 1e-6, None),

    # The bane buffs, and the reason the client says "Slashing: Unparalleled (700)" where the
    # portal prints a percentage.
    #
    # THESE ARE NOT IN `propertiesFloat`, AND POINTING THEM THERE MADE THE CHECK WORSE THAN
    # USELESS. AppraiseInfo carries resistances in its own `ArmorProfile` struct, so every one of
    # these read `None` on the game side. Before the dump carried `armorProfile` at all that was a
    # silent pass - None vs None, skipped, counted as agreement. Once the dump gained the struct
    # but the mapping did not, it flipped to eight false FAILURES on every armour piece, which is
    # the same bug wearing the opposite sign. A mapping that cannot see its property is not a
    # weaker test, it is a test that reports a number it never looked at.
    #
    # The names differ too: ArmorProfile calls electric resistance `lightning`.
    ("armorModSlash", [("armorProfile", "slashing")], 1e-6, 1.0),
    ("armorModPierce", [("armorProfile", "piercing")], 1e-6, 1.0),
    ("armorModBludgeon", [("armorProfile", "bludgeoning")], 1e-6, 1.0),
    ("armorModCold", [("armorProfile", "cold")], 1e-6, 1.0),
    ("armorModFire", [("armorProfile", "fire")], 1e-6, 1.0),
    ("armorModAcid", [("armorProfile", "acid")], 1e-6, 1.0),
    ("armorModElectric", [("armorProfile", "lightning")], 1e-6, 1.0),
    ("armorModNether", [("armorProfile", "nether")], 1e-6, 1.0),
]


def _portal_detail(character_id: int, item_id: int) -> dict | None:
    """Build the portal's panel for one item, through the real code path."""
    from api import app, db, payload  # noqa: E402  (path is set above)

    with db.shard() as cur:
        raw = payload.load_character(cur, character_id)

        if raw is None:
            return None

        import time

        data = payload.build_private(cur, raw, app.live_dials(), time.time())

    inv = data.get("inventory") or {}
    everything = list(inv.get("equipped") or [])

    for cont in inv.get("containers") or []:
        everything += cont.get("items") or []

    for item in everything:
        if int(item.get("id") or 0) == item_id:
            return item

    return None


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        print(__doc__.strip().splitlines()[0])
        print("\nusage: appraise-diff.py <sg-appraise-*.json> <character-id>")
        return 2

    oracle = json.loads(Path(argv[1]).read_text(encoding="utf-8"))
    character_id = int(argv[2])
    item_id = int(oracle["id"])

    item = _portal_detail(character_id, item_id)

    if item is None:
        print(f"!! item {item_id} is not in character {character_id}'s payload")
        return 1

    detail = item.get("detail") or {}

    print(f"item : {oracle['item']}  (guid {item_id})")
    print(f"       wielded={oracle.get('wielded')} enchantable={oracle.get('enchantable')}")
    print(f"portal: {item.get('name')}")
    print()

    problems = 0
    checked = 0

    for key, sources, tol, neutral in MAPPING:
        # First bucket that carries the property wins - see the note on MAPPING.
        server = None
        prop = sources[0][1]

        for bucket, name in sources:
            found = (oracle.get(bucket) or {}).get(name)

            if found is not None:
                server, prop = found, name
                break

        web = detail

        for part in key.split("."):
            web = (web or {}).get(part) if isinstance(web, dict) else None

        if server is None and web is None:
            continue

        # ZERO AND ABSENT ARE THE SAME THING HERE. The portal omits a value of 0 rather than
        # printing "Value: 0", and AppraiseInfo sends the 0. That is a presentation choice on both
        # sides, not a disagreement about the number.
        if (server or 0) == 0 and web is None:
            continue

        # ...and the same for a modifier the portal leaves out because it has no effect.
        # Resistances and weapon mods are multipliers around 1.0, and AppraiseInfo sends all of
        # them whether or not they do anything. The portal prints a line only when there is
        # something to say, so an unenchanted breastplate would otherwise report two or three
        # phantom disagreements for "Bludgeoning: no change".
        if web is None and neutral is not None and server is not None \
                and abs(float(server) - neutral) <= max(tol, 1e-9):
            continue

        checked += 1

        if server is None or web is None:
            side = "GAME only" if web is None else "WEB only"
            print(f"  {prop:<22} {side:<10} game={server!r} web={web!r}")
            problems += 1
            continue

        if abs(float(server) - float(web)) > tol:
            print(f"  {prop:<22} DIFFERS    game={server!r} web={web!r}")
            problems += 1

    # The spell list, which is its own class of bug: the cast-on-use spell is a DataId rather than a
    # spell-book row, so a naive port drops it.
    # SpellBook carries TWO lists in one. AppraiseInfo appends the item's ACTIVE ENCHANTMENTS to
    # the same array as its innate spells, ORed with 0x80000000 to distinguish them
    # (AppraiseInfo.cs:502) - so comparing the raw array against the portal's spell list reports a
    # difference that is really the enchantment block. Split them and check each against the field
    # that actually holds it.
    ENCHANTMENT_MASK = 0x8000_0000

    raw = oracle.get("spellBook") or []
    game_spells = sorted(s for s in raw if not s & ENCHANTMENT_MASK)
    game_ench = sorted(s & ~ENCHANTMENT_MASK for s in raw if s & ENCHANTMENT_MASK)

    web_spells = sorted(s["id"] for s in (detail.get("spells") or []))
    web_ench = sorted(e["id"] for e in (detail.get("enchantments") or []))

    if game_spells != web_spells:
        print(f"  {'SpellBook':<22} DIFFERS    game={game_spells} web={web_spells}")
        problems += 1

    if game_ench != web_ench:
        print(f"  {'Enchantments':<22} DIFFERS    game={game_ench} web={web_ench}")
        problems += 1

    checked += 2

    print()

    if problems == 0:
        print(f"IDENTICAL over {checked} compared values — the panel is the one the client is sent.")
        return 0

    print(f"{problems} disagreement(s) over {checked} compared values.")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
