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
MAPPING = [
    ("value", "propertiesInt", "Value", 0),
    ("burden", "propertiesInt", "EncumbranceVal", 0),
    ("armorLevel", "propertiesInt", "ArmorLevel", 0),
    ("workmanship", "propertiesInt", "ItemWorkmanship", 0),
    ("spellcraft", "propertiesInt", "ItemSpellcraft", 0),
    ("mana.current", "propertiesInt", "ItemCurMana", 0),
    ("mana.max", "propertiesInt", "ItemMaxMana", 0),
    ("activationDifficulty", "propertiesInt", "ItemDifficulty", 0),
    ("manaCost", "propertiesInt", "ItemManaCost", 0),
    ("damage", "propertiesInt", "Damage", 0),
    ("weaponSpeed", "propertiesInt", "WeaponTime", 0),
    ("cleaving", "propertiesInt", "Cleaving", 0),
    # MaterialType is compared as the NAME the portal renders, not the id - see _resolve().

    ("damageMod", "propertiesFloat", "DamageMod", 1e-6),
    ("attackMod", "propertiesFloat", "WeaponOffense", 1e-6),
    ("meleeDefenseMod", "propertiesFloat", "WeaponDefense", 1e-6),
    ("missileDefenseMod", "propertiesFloat", "WeaponMissileDefense", 1e-6),
    ("magicDefenseMod", "propertiesFloat", "WeaponMagicDefense", 1e-6),
    ("manaConversionMod", "propertiesFloat", "ManaConversionMod", 1e-6),
    ("elementalDamageMod", "propertiesFloat", "ElementalDamageMod", 1e-6),
    ("damageVariance", "propertiesFloat", "DamageVariance", 1e-6),
    ("criticalFrequency", "propertiesFloat", "CriticalFrequency", 1e-6),
    ("criticalMultiplier", "propertiesFloat", "CriticalMultiplier", 1e-6),

    # The bane buffs. These are multiplicative on the item's own registry and are the reason the
    # client says "Slashing: Unparalleled (700)" where the portal still prints a raw percentage.
    ("armorModSlash", "propertiesFloat", "ArmorModVsSlash", 1e-6),
    ("armorModPierce", "propertiesFloat", "ArmorModVsPierce", 1e-6),
    ("armorModBludgeon", "propertiesFloat", "ArmorModVsBludgeon", 1e-6),
    ("armorModCold", "propertiesFloat", "ArmorModVsCold", 1e-6),
    ("armorModFire", "propertiesFloat", "ArmorModVsFire", 1e-6),
    ("armorModAcid", "propertiesFloat", "ArmorModVsAcid", 1e-6),
    ("armorModElectric", "propertiesFloat", "ArmorModVsElectric", 1e-6),
    ("armorModNether", "propertiesFloat", "ArmorModVsNether", 1e-6),
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

    for key, bucket, prop, tol in MAPPING:
        server = (oracle.get(bucket) or {}).get(prop)
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
