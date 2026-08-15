"""Shadowgain 127 — what an item IS: coverage, equip slot, and its examine text.

Split out of payload.py because it grew past the point where it belonged inside an inventory
loop, and because all of it answers one question the rest of the payload does not: *what would
the game tell me if I examined this?*

THE SHARD ALREADY KNOWS ALL OF IT. Nothing here is new data — every value was sitting in
`biota_properties_*` while the page rendered a name and an icon. The armour coverage that fills
nine paperdoll slots is one integer (`CurrentWieldedLocation`) the equipped query was already
selecting and then reducing to a single coarse slot name.
"""

from __future__ import annotations

from . import curves

# --- property ids ----------------------------------------------------------------------------

INT_ITEM_TYPE = 1
INT_ENCUMBRANCE = 5
INT_VALID_LOCATIONS = 9
INT_CURRENT_WIELDED_LOCATION = 10
INT_STACK_SIZE = 12
INT_ITEM_USEABLE = 16
INT_VALUE = 19
INT_ARMOR_LEVEL = 28
INT_MATERIAL_TYPE = 131
INT_WORKMANSHIP = 105
INT_SPELLCRAFT = 106
INT_CURRENT_MANA = 107
INT_MAX_MANA = 108
INT_DIFFICULTY = 109
INT_GEM_COUNT = 265
INT_GEM_TYPE = 266
INT_WIELD_SKILLTYPE = 158
INT_WIELD_DIFFICULTY = 159

FLOAT_MANA_RATE = 5
FLOAT_ARMOR_MOD_SLASH = 13
FLOAT_ARMOR_MOD_PIERCE = 14
FLOAT_ARMOR_MOD_BLUDGEON = 15
FLOAT_ARMOR_MOD_COLD = 16
FLOAT_ARMOR_MOD_FIRE = 17
FLOAT_ARMOR_MOD_ACID = 18
FLOAT_ARMOR_MOD_ELECTRIC = 19
FLOAT_SALVAGE_WORKMANSHIP = 105

STRING_NAME = 1
STRING_USE = 14
STRING_SHORT_DESC = 15
STRING_LONG_DESC = 16

DID_ICON = 8
DID_ICON_OVERLAY = 50
DID_ICON_UNDERLAY = 52


# --- equip slots -------------------------------------------------------------------------------
#
# EquipMask -> the nine ARMOUR coverage areas the paperdoll draws, plus the discrete slots.
#
# Coverage is a SET, not a single slot. A hooded robe reports 0x7F01 — head, feet, chest, abdomen,
# both arms and both legs, eight areas at once — which is exactly why reducing it to one name lost
# the information the paperdoll needs. The front-end already consumes `coverage`; this produces it.
COVERAGE_MASKS = [
    ("head",       0x00000001),                          # HeadWear
    ("chest",      0x00000002 | 0x00000200),             # ChestWear | ChestArmor
    ("abdomen",    0x00000004 | 0x00000400),             # AbdomenWear | AbdomenArmor
    ("upperArms",  0x00000008 | 0x00000800),             # UpperArmWear | UpperArmArmor
    ("lowerArms",  0x00000010 | 0x00001000),             # LowerArmWear | LowerArmArmor
    ("hands",      0x00000020),                          # HandWear
    ("upperLegs",  0x00000040 | 0x00002000),             # UpperLegWear | UpperLegArmor
    ("lowerLegs",  0x00000080 | 0x00004000),             # LowerLegWear | LowerLegArmor
    ("feet",       0x00000100),                          # FootWear
]

# Everything that is NOT body coverage: worn or held in one discrete place.
DISCRETE_MASKS = [
    ("weapon",    0x00100000 | 0x02000000),   # MeleeWeapon, TwoHanded
    ("shield",    0x00200000),                # Shield
    ("wand",      0x00400000 | 0x01000000),   # MissileWeapon, Held
    ("ammo",      0x00800000),                # MissileAmmo
    ("cloak",     0x08000000),                # 127 #3 — its own slot, no longer in `other`
    ("aetheria1", 0x10000000),                # SigilOne    ) 127 #4 — the three Aetheria slots
    ("aetheria2", 0x20000000),                # SigilTwo    )
    ("aetheria3", 0x40000000),                # SigilThree  )
    ("neck",      0x00008000),
    ("wrist",     0x00010000 | 0x00020000),
    ("finger",    0x00040000 | 0x00080000),
    ("trinket",   0x04000000),
]

# The coarse tile the eight-slot doll used before coverage existed. Kept so `slot` keeps meaning
# what it meant — the front-end still uses it as a fallback — with coverage carrying the detail.
_COARSE = {
    "head": "head", "chest": "chest", "abdomen": "chest",
    "upperArms": "hands", "lowerArms": "hands", "hands": "hands",
    "upperLegs": "legs", "lowerLegs": "legs", "feet": "feet",
}


# --- foci ---------------------------------------------------------------------------------------
#
# ItemType.Misc + Usable.ContainedViewedRemote is the signature of a Focus, and it is EXACT: across
# the entire world database those two properties together select five weenies and nothing else —
# Artifice, Enchantment, Shadow, Strife and Verdancy, which is the complete set the AC wiki lists.
#
# Chris: "they legit use/occupy a slot", and the wiki agrees outright — *"A foci occupies an entire
# pack slot and cannot hold anything else."* So they belong in the pack bar beside the packs, not
# loose in the main grid.
#
# Chosen over the alternatives on purpose. The weenie ids (15268-15271, 43173) would have been a
# hard-coded list that silently misses anything added later; `class_Name LIKE 'pack%'` would work
# but leans on a naming convention and needs ace_world, which this service deliberately does not
# grant. This rule reads what the item IS.
ITEM_TYPE_MISC = 128
USABLE_CONTAINED_VIEWED_REMOTE = 56  # Contained | Viewed | Remote


def is_focus(ints: dict) -> bool:
    return (
        ints.get(INT_ITEM_TYPE) == ITEM_TYPE_MISC
        and ints.get(INT_ITEM_USEABLE) == USABLE_CONTAINED_VIEWED_REMOTE
    )


def coverage(wielded_location: int | None) -> list[str]:
    """Every armour area this item covers, in head-to-toe order."""
    if not wielded_location:
        return []

    return [key for key, mask in COVERAGE_MASKS if wielded_location & mask]


def slot_name(wielded_location: int | None) -> str:
    """The single coarse slot, for the eight-tile doll and for non-armour.

    Discrete slots win over coverage: a cloak sets Cloak and nothing else, but a robe sets head
    AND chest AND legs, so asking "is this a cloak/weapon/ring" first avoids a garment landing in
    a jewellery slot on a stray bit.
    """
    if not wielded_location:
        return "other"

    for key, mask in DISCRETE_MASKS:
        if wielded_location & mask:
            return key

    areas = coverage(wielded_location)

    if not areas:
        return "other"

    # Torso first, head last — a hooded robe sets HeadWear too, and the helmet tile is the least
    # informative place to put a robe. See the note in payload._SLOTS history.
    for preferred in ("chest", "abdomen", "upperLegs", "lowerLegs", "upperArms", "lowerArms",
                      "hands", "feet", "head"):
        if preferred in areas:
            return _COARSE[preferred]

    return "other"


# --- examine text ------------------------------------------------------------------------------

MATERIAL_NAMES = {
    1: "Ceramic", 2: "Porcelain", 4: "Linen", 5: "Satin", 6: "Silk", 7: "Velvet", 8: "Wool",
    10: "Agate", 11: "Amber", 12: "Amethyst", 13: "Aquamarine", 14: "Azurite", 15: "Black Garnet",
    16: "Black Opal", 17: "Bloodstone", 18: "Carnelian", 19: "Citrine", 20: "Diamond",
    21: "Emerald", 22: "Fire Opal", 23: "Green Garnet", 24: "Green Jade", 25: "Hematite",
    26: "Imperial Topaz", 27: "Jet", 28: "Lapis Lazuli", 29: "Lavender Jade", 30: "Malachite",
    31: "Moonstone", 32: "Onyx", 33: "Opal", 34: "Peridot", 35: "Red Garnet", 36: "Red Jade",
    37: "Rose Quartz", 38: "Ruby", 39: "Sapphire", 40: "Smoky Quartz", 41: "Sunstone",
    42: "Tiger Eye", 43: "Tourmaline", 44: "Turquoise", 45: "White Jade", 46: "White Quartz",
    47: "White Sapphire", 48: "Yellow Garnet", 49: "Yellow Topaz", 50: "Zircon", 51: "Ivory",
    52: "Leather", 53: "Armoredillo Hide", 54: "Gromnie Hide", 55: "Reed Shark Hide",
    56: "Brass", 57: "Bronze", 58: "Copper", 59: "Gold", 60: "Iron", 61: "Pyreal", 62: "Silver",
    63: "Steel", 64: "Alabaster", 65: "Granite", 66: "Marble", 67: "Obsidian", 68: "Sandstone",
    69: "Serpentine", 70: "Ebony", 71: "Mahogany", 72: "Oak", 73: "Pine", 74: "Teak",
}

# Workmanship is stored 1-10 and shown as a word in game.
WORKMANSHIP_NAMES = {
    1: "Poor", 2: "Rough", 3: "Crude", 4: "Low", 5: "Average",
    6: "Fine", 7: "Good", 8: "Excellent", 9: "Superb", 10: "Magnificent",
}

RESISTANCES = [
    ("Slashing", FLOAT_ARMOR_MOD_SLASH),
    ("Piercing", FLOAT_ARMOR_MOD_PIERCE),
    ("Bludgeoning", FLOAT_ARMOR_MOD_BLUDGEON),
    ("Cold", FLOAT_ARMOR_MOD_COLD),
    ("Fire", FLOAT_ARMOR_MOD_FIRE),
    ("Acid", FLOAT_ARMOR_MOD_ACID),
    ("Lightning", FLOAT_ARMOR_MOD_ELECTRIC),
]


def _fmt(n) -> str:
    return f"{int(n):,}"


def build_detail(ints: dict, floats: dict, strings: dict, spells: list[int]) -> dict:
    """The examine panel, as structured data plus ready-made lines.

    Both shapes on purpose: `lines` is what the tooltip renders today with no parsing, and the
    named fields are there so the front-end can lay it out properly later without the backend
    changing again. Every entry is omitted when absent rather than sent as null — an item with no
    armour level should show no armour row, not "Armor: —".
    """
    detail: dict = {}
    lines: list[str] = []

    def add(label: str, value):
        if value is None:
            return
        lines.append(f"{label}: {value}")

    # --- identity ---------------------------------------------------------------------------
    material = MATERIAL_NAMES.get(ints.get(INT_MATERIAL_TYPE))
    workmanship = ints.get(INT_WORKMANSHIP)

    if material:
        detail["material"] = material

    if workmanship:
        detail["workmanship"] = workmanship
        detail["workmanshipName"] = WORKMANSHIP_NAMES.get(int(workmanship), str(workmanship))

    # --- the physical facts ------------------------------------------------------------------
    if (value := ints.get(INT_VALUE)):
        detail["value"] = value
        add("Value", f"{_fmt(value)} pyreal")

    if (burden := ints.get(INT_ENCUMBRANCE)):
        detail["burden"] = burden
        add("Burden", _fmt(burden))

    if material or workmanship:
        add("Material", " ".join(x for x in (detail.get("workmanshipName"), material) if x))

    if (armor := ints.get(INT_ARMOR_LEVEL)):
        detail["armorLevel"] = armor
        add("Armor Level", _fmt(armor))

    # --- protection ---------------------------------------------------------------------------
    #
    # Stored as multipliers around 1.0. Reported as the game reports them — a percentage away
    # from neutral — because "0.8" means nothing to a reader and "Slashing +20%" does.
    mods = {}

    for label, prop in RESISTANCES:
        mod = floats.get(prop)

        if mod is None or abs(mod - 1.0) < 0.005:
            continue

        mods[label] = round(mod, 3)

    if mods:
        detail["resistances"] = mods
        add("Protection", ", ".join(
            f"{k} {'+' if v < 1 else '-'}{abs(round((1 - v) * 100))}%" for k, v in mods.items()))

    # --- magic ---------------------------------------------------------------------------------
    if (spellcraft := ints.get(INT_SPELLCRAFT)):
        detail["spellcraft"] = spellcraft
        add("Spellcraft", _fmt(spellcraft))

    cur_mana, max_mana = ints.get(INT_CURRENT_MANA), ints.get(INT_MAX_MANA)

    if max_mana:
        detail["mana"] = {"current": cur_mana or 0, "max": max_mana}
        add("Mana", f"{_fmt(cur_mana or 0)} / {_fmt(max_mana)}")

    if (rate := floats.get(FLOAT_MANA_RATE)):
        # Stored per-second and negative (it drains). The game phrases it as a period.
        seconds = abs(1.0 / rate) if rate else 0
        detail["manaRateSeconds"] = round(seconds, 1)
        add("Mana Burn", f"1 point every {seconds:.0f}s")

    if (difficulty := ints.get(INT_DIFFICULTY)):
        detail["activationDifficulty"] = difficulty
        add("Difficulty", _fmt(difficulty))

    if (wield_diff := ints.get(INT_WIELD_DIFFICULTY)):
        skill = curves.enum_label("skill", ints.get(INT_WIELD_SKILLTYPE))
        detail["wieldRequirement"] = {"skill": skill, "level": wield_diff}
        add("Wield Requirement", f"{skill} {_fmt(wield_diff)}" if skill else _fmt(wield_diff))

    # --- spells ---------------------------------------------------------------------------------
    if spells:
        table = curves.spell_table()
        named = []

        for spell_id in spells:
            meta = table.get(spell_id)
            named.append({"id": spell_id, "name": (meta or {}).get("name") or f"Spell {spell_id}"})

        detail["spells"] = named
        lines.append("Spells: " + ", ".join(s["name"] for s in named))

    # --- flavour ---------------------------------------------------------------------------------
    for key, prop in (("use", STRING_USE), ("shortDesc", STRING_SHORT_DESC),
                      ("longDesc", STRING_LONG_DESC)):
        text = strings.get(prop)

        if text:
            detail[key] = text

    # The description reads as prose and belongs at the end, after the numbers.
    for key in ("longDesc", "use"):
        if detail.get(key):
            lines.append(detail[key])

    detail["lines"] = lines

    return detail


def display_name(base_name: str, ints: dict, floats: dict) -> str:
    """The name a player would recognise.

    127 #6: salvage arrives as "Salvage (6)" because that is literally the stored name — the
    material is a separate property the client merges in on display. So "Salvage (6)" becomes
    "Steel Salvage (6)", which is what the bag actually contains.
    """
    material = MATERIAL_NAMES.get(ints.get(INT_MATERIAL_TYPE))

    if material and base_name.startswith("Salvage"):
        return f"{material} {base_name}"

    return base_name
