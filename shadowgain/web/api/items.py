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

import json
from pathlib import Path

from . import curves, enchantments

DATA_DIR = Path(__file__).resolve().parent / "data"

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
# `IsEnchantable => (ResistMagic ?? 0) < 9999` (WorldObject_Weapon.cs:532). An item at or above
# that cannot take an enchantment, so the WIELDER's aura must not be merged into its panel.
INT_RESIST_MAGIC = 36
# PropertyDataId.Spell - the spell an item casts when activated, kept apart from its spell book.
DID_SPELL = 28
# PropertyBool.Retained - the client lists it under "Properties:".
BOOL_RETAINED = 91
# PropertyDataId.ProcSpell - cast on strike, which the client also names under "Properties:".
DID_PROC_SPELL = 55
UNENCHANTABLE_RESIST_MAGIC = 9999
INT_WORKMANSHIP = 105
INT_SPELLCRAFT = 106
INT_CURRENT_MANA = 107
INT_MAX_MANA = 108
INT_DIFFICULTY = 109
# 137: these were 265 and 266, which are EquipmentSetId and PetClass. Nothing consumed them, so
# the mistake was invisible - a gem count would have rendered a set id had anything read them.
INT_GEM_COUNT = 177
INT_GEM_TYPE = 178

# --- weapons, and the general facts the examine panel was missing entirely (137) ---------------
INT_UI_EFFECTS = 18
INT_BONDED = 33
INT_DAMAGE = 44
INT_DAMAGE_TYPE = 45
INT_WEAPON_SKILL = 48
INT_WEAPON_TIME = 49
INT_ATTUNED = 114
INT_CLEAVING = 292
INT_MAX_STRUCTURE = 91
INT_STRUCTURE = 92
INT_ITEMS_CAPACITY = 6
INT_CONTAINERS_CAPACITY = 7

FLOAT_WEAPON_DEFENSE = 29
FLOAT_DAMAGE_VARIANCE = 22
FLOAT_WEAPON_OFFENSE = 62
FLOAT_DAMAGE_MOD = 63
FLOAT_CRITICAL_MULTIPLIER = 136
FLOAT_SLAYER_DAMAGE_BONUS = 138
FLOAT_MANA_CONVERSION_MOD = 144
FLOAT_CRITICAL_FREQUENCY = 147
FLOAT_WEAPON_MISSILE_DEFENSE = 149
FLOAT_WEAPON_MAGIC_DEFENSE = 150
FLOAT_ELEMENTAL_DAMAGE_MOD = 152
FLOAT_IGNORE_ARMOR = 155
FLOAT_ARMOR_MOD_NETHER = 165

# DamageType is a BITFIELD - a weapon can be Slashing/Piercing at once, and several are.
DAMAGE_TYPE_NAMES = [
    (0x001, "Slashing"), (0x002, "Piercing"), (0x004, "Bludgeoning"),
    (0x008, "Cold"), (0x010, "Fire"), (0x020, "Acid"), (0x040, "Electric"),
    (0x080, "Health"), (0x100, "Stamina"), (0x200, "Mana"), (0x400, "Nether"),
]

# UiEffects, the glow the client puts on an item. "Magical" is the one players look for.
UI_EFFECT_NAMES = [
    (0x0001, "Magical"), (0x0002, "Poisoned"), (0x0004, "Boosts Health"),
    (0x0008, "Boosts Mana"), (0x0010, "Boosts Stamina"), (0x0020, "Fire"),
    (0x0040, "Lightning"), (0x0080, "Frost"), (0x0100, "Acid"),
    (0x0200, "Bludgeoning"), (0x0400, "Slashing"),
]


def _flags(value: int | None, table) -> list[str]:
    return [name for bit, name in table if value and value & bit]


def _pct(mod: float | None, places: int = 0) -> str | None:
    """A multiplier around 1.0 rendered the way the game phrases it: a signed percentage."""
    if mod is None or abs(mod - 1.0) < 0.0005:
        return None

    return f"{'+' if mod > 1 else ''}{round((mod - 1) * 100, places):g}%"
# 138: these were 158 and 159, BOTH OFF BY ONE. 158 is WieldRequirements (how to read the other
# two), 159 is WieldSkillType, 160 is WieldDifficulty. Unlike the gem constants, these WERE being
# read - so every "Wield Requirement" line on the site quoted a requirement TYPE as a skill and a
# skill id as a difficulty. Wrong, and confidently formatted.
INT_WIELD_REQUIREMENTS = 158
INT_WIELD_SKILLTYPE = 159
INT_WIELD_DIFFICULTY = 160

# AC allows up to three stacked wield requirements on one item.
WIELD_SETS = ((158, 159, 160), (270, 271, 272), (273, 274, 275))

# --- what the rest of the loot types were missing (138) ----------------------------------------
INT_ARMOR_TYPE = 27
INT_AMMO_TYPE = 50
INT_WEAPON_RANGE = 60
INT_BOOSTER_ENUM = 89
INT_BOOST_VALUE = 90
INT_ITEM_ALLEGIANCE_RANK_LIMIT = 110
INT_ITEM_SKILL_LEVEL_LIMIT = 115
INT_ITEM_MANA_COST = 117
INT_SLAYER_CREATURE_TYPE = 166
INT_NUM_ITEMS_IN_MATERIAL = 170
INT_RESISTANCE_MODIFIER_TYPE = 263
INT_EQUIPMENT_SET_ID = 265
INT_REMAINING_LIFESPAN = 268
INT_UNIQUE = 279
INT_USE_REQUIRES_SKILL = 366
INT_USE_REQUIRES_SKILL_LEVEL = 367
INT_USE_REQUIRES_LEVEL = 369

# An item can carry up to five imbues.
INT_IMBUED_EFFECTS = (179, 303, 304, 305, 306)

FLOAT_MAXIMUM_VELOCITY = 26
FLOAT_RESISTANCE_MODIFIER = 157

# --- enum names, GENERATED from ACE's own enum files -------------------------------------------
#
# Not typed out by hand, and that is the point: hand-mapping ids has now produced two separate
# bugs in this file (the gem pair, and the wield triple above). These were parsed straight from
# ACE.Entity/Enum/*.cs, implicit values included.

WIELD_REQUIREMENT_NAMES = {
    1: "Skill",
    2: "Raw Skill",
    3: "Attrib",
    4: "Raw Attrib",
    5: "Secondary Attrib",
    6: "Raw Secondary Attrib",
    7: "Level",
    8: "Training",
    9: "Int Stat",
    10: "Bool Stat",
    11: "Creature Type",
    12: "Heritage Type",
}

ARMOR_TYPE_NAMES = [
    (0x1, "Cloth"),
    (0x2, "Leather"),
    (0x4, "Studded Leather"),
    (0x8, "Scalemail"),
    (0x10, "Chainmail"),
    (0x20, "Metal"),
]

AMMO_TYPE_NAMES = {
    1: "Arrow",
    2: "Bolt",
    4: "Atlatl",
    8: "Arrow Crystal",
    16: "Bolt Crystal",
    32: "Atlatl Crystal",
    64: "Arrow Chorizite",
    128: "Bolt Chorizite",
    256: "Atlatl Chorizite",
}

IMBUED_EFFECT_NAMES = [
    (0x1, "Critical Strike"),
    (0x2, "Crippling Blow"),
    (0x4, "Armor Rending"),
    (0x8, "Slash Rending"),
    (0x10, "Pierce Rending"),
    (0x20, "Bludgeon Rending"),
    (0x40, "Acid Rending"),
    (0x80, "Cold Rending"),
    (0x100, "Electric Rending"),
    (0x200, "Fire Rending"),
    (0x400, "Melee Defense"),
    (0x800, "Missile Defense"),
    (0x1000, "Magic Defense"),
    (0x2000, "Spellbook"),
    (0x4000, "Nether Rending"),
    (0x20000000, "Ignore Some Magic Projectile Damage"),
    (0x40000000, "Always Critical"),
    (0x80000000, "Ignore All Armor"),
]

CREATURE_TYPE_NAMES = {
    1: "Olthoi",
    2: "Banderling",
    3: "Drudge",
    4: "Mosswart",
    5: "Lugian",
    6: "Tumerok",
    7: "Mite",
    8: "Tusker",
    9: "Phyntos Wasp",
    10: "Rat",
    11: "Auroch",
    12: "Cow",
    13: "Golem",
    14: "Undead",
    15: "Gromnie",
    16: "Reedshark",
    17: "Armoredillo",
    18: "Fae",
    19: "Virindi",
    20: "Wisp",
    21: "Knathtead",
    22: "Shadow",
    23: "Mattekar",
    24: "Mumiyah",
    25: "Rabbit",
    26: "Sclavus",
    27: "Shallows Shark",
    28: "Monouga",
    29: "Zefir",
    30: "Skeleton",
    31: "Human",
    32: "Shreth",
    33: "Chittick",
    34: "Moarsman",
    35: "Olthoi Larvae",
    36: "Slithis",
    37: "Deru",
    38: "Fire Elemental",
    39: "Snowman",
    41: "Bunny",
    42: "Lightning Elemental",
    43: "Rockslide",
    44: "Grievver",
    45: "Niffis",
    46: "Ursuin",
    47: "Crystal",
    48: "Hollow Minion",
    49: "Scarecrow",
    50: "Idol",
    51: "Empyrean",
    52: "Hopeslayer",
    53: "Doll",
    54: "Marionette",
    55: "Carenzi",
    56: "Siraluun",
    57: "Aun Tumerok",
    58: "Hea Tumerok",
    59: "Simulacrum",
    60: "Acid Elemental",
    61: "Frost Elemental",
    62: "Elemental",
    63: "Statue",
    64: "Wall",
    65: "Altered Human",
    66: "Device",
    67: "Harbinger",
    68: "Dark Sarcophagus",
    69: "Chicken",
    70: "Gotrok Lugian",
    71: "Margul",
    72: "Bleached Rabbit",
    73: "Nasty Rabbit",
    74: "Grimacing Rabbit",
    75: "Burun",
    76: "Target",
    77: "Ghost",
    78: "Fiun",
    79: "Eater",
    80: "Penguin",
    81: "Ruschk",
    82: "Thrungus",
    83: "Viamontian Knight",
    84: "Remoran",
    85: "Swarm",
    86: "Moar",
    87: "Enchanted Arms",
    88: "Sleech",
    89: "Mukkir",
    90: "Merwart",
    91: "Food",
    92: "Paradox Olthoi",
    93: "Harvest",
    94: "Energy",
    95: "Apparition",
    96: "Aerbax",
    97: "Touched",
    98: "Blighted Moarsman",
    99: "Gear Knight",
    100: "Gurog",
    101: "Anekshay",
}

def _equipment_set_names() -> dict[int, str]:
    """Set id -> the CLIENT's name, for every one that could be confirmed against acclient.exe.

    ACE's enum calls set 71 `CloakMeleeDefense` and this file used to render that as "Cloak Melee
    Defense". The client calls it "Weave of Melee Defense" - the same class of invention as the
    workmanship words, and Chris spotted it on his Silk Cloak.

    `tools/extract-equipment-sets.py` proposes the name each enum entry would have and keeps it only
    if that exact string is in the binary, so 93 of 141 are confirmed. The rest - Test, Unknown3,
    and identifiers that differ from their display form - fall back to the spaced identifier below,
    which is a guess and is marked as one rather than dressed up.
    """
    raw = json.loads((DATA_DIR / "equipment-sets.json").read_text(encoding="utf-8"))

    return {int(k): v for k, v in raw.items()}


EQUIPMENT_SET_NAMES = _equipment_set_names()



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

# --- the clothing layer, which the client gives its own two boxes ------------------------------
#
# 131. AC's inventory panel shows a shirt and a pants slot beside the paperdoll, separate from the
# armour it goes under. There is no EquipMask bit for "shirt" — the distinction is which LAYER an
# item occupies, and it is readable from two things the shard already stores:
#
#   * armour sets the *Armor* bits (0x7E00); the clothing layer sets only the *Wear* bits (0x1FF)
#   * ItemType says whether the piece is Armor or Clothing
#
# Measured on Black Breath: Poet's Shirt is type 4 (Clothing) at 0x0000001E — Chest|Abdomen|
# UpperArm|LowerArmWear, no armour bits. The Pathwarden Robe is ALSO type 4, but at 0x00007F01 it
# sets the whole armour block, so the ItemType test alone would wrongly file the robe as a shirt.
# Both conditions are needed.
ITEM_TYPE_CLOTHING = 4

ARMOR_BITS = 0x00007E00

CLOTHING_UPPER = 0x00000002 | 0x00000008 | 0x00000010   # ChestWear | UpperArmWear | LowerArmWear
CLOTHING_LOWER = 0x00000040 | 0x00000080                # UpperLegWear | LowerLegWear

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


def slot_name(wielded_location: int | None, item_type: int | None = None) -> str:
    """The single coarse slot, for the eight-tile doll and for non-armour.

    Discrete slots win over coverage: a cloak sets Cloak and nothing else, but a robe sets head
    AND chest AND legs, so asking "is this a cloak/weapon/ring" first avoids a garment landing in
    a jewellery slot on a stray bit.

    `item_type` is optional only so existing callers and tests keep working; without it a shirt
    reports as `chest` exactly as it did before, which is a coarser answer rather than a wrong one.
    """
    if not wielded_location:
        return "other"

    for key, mask in DISCRETE_MASKS:
        if wielded_location & mask:
            return key

    # The clothing layer, before coverage collapses it into a body area — see ARMOR_BITS above.
    if item_type == ITEM_TYPE_CLOTHING and not wielded_location & ARMOR_BITS:
        if wielded_location & CLOTHING_UPPER:
            return "shirt"

        if wielded_location & CLOTHING_LOWER:
            return "pants"

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

def _material_names() -> dict[int, str]:
    """Material id -> name, READ FROM THE GENERATED ENUM rather than typed here.

    This was a hand-written table of 72 entries and it was WRONG from id 56 upward. ACE's
    MaterialType enum carries CATEGORY markers - Metal (56), Stone (65), Wood (72) - that a human
    transcribing a list of materials naturally skips, and skipping them shifts every following
    name by one. A Gold Orb (60) came out as Iron; 20 names were wrong and 5 were missing.

    Nothing about that was visible. A wrong material name is still a material name, so it reads as
    data rather than as a bug, which is why it survived from 127 to 158. The same failure mode as
    the hand-mapped ids in 152 - and the same fix: the exporter already reflects the real enum
    into data/enums.json, so there is no second copy to rot.
    """
    raw = json.loads((DATA_DIR / "enums.json").read_text(encoding="utf-8"))["materialType"]

    # `label` is the spaced form ("Black Opal"); `name` is the C# identifier ("BlackOpal").
    return {int(value): entry["label"] for value, entry in raw.items()}


MATERIAL_NAMES = _material_names()

# Workmanship is stored 1-10 and shown as a word in game.
def _workmanship_names() -> dict[int, str]:
    """Workmanship value -> the client's adjective, GENERATED not typed.

    This was hand-written as 9 -> "Superb" and 6 -> "Fine". The client says "Incomparable" and
    "Nearly flawless". Unlike almost everything else the portal shows there is no server-side
    source for these words - the shard stores a bare number and the CLIENT supplies the adjective -
    so they come from `tools/extract-workmanship.py`, which pulls them out of acclient.exe by
    anchor and asserts the run before writing. See that file for why the dats were ruled out.
    """
    raw = json.loads((DATA_DIR / "workmanship.json").read_text(encoding="utf-8"))

    return {int(k): v for k, v in raw.items()}


WORKMANSHIP_NAMES = _workmanship_names()

# The client's word for a resistance, and the thresholds it uses.
#
# THE WORDS ARE THE CLIENT'S, the thresholds are DERIVED - and the difference matters, so it is
# written down rather than blurred. The five strings live in acclient.exe beside the client's own
# "  (%.0f)" format for the parenthesised figure, and there is no sixth: nothing below
# "Below Average" exists, so it is the floor and there is no unknown region at the bottom.
#
# The BOUNDARIES are not a table anywhere in the binary - they are inline constants in comparison
# code - so they are derived from observation. Seven independent points, all consistent with one
# 0.4 ladder, and no point contradicts it:
#
#     0.7 Below Average      1.2 Above Average      2.0 Unparalleled
#     0.9 Average            1.6 Excellent
#     1.0 Average            1.7 Excellent
#
# 0.7/0.9/1.0/1.2 come from the community wiki's UNBUFFED Hoary Mattekar Robe, which prints word
# and value together against a known armour level; 1.6/1.7/2.0 from Chris's own in-game panels. A
# stray 1.6 double sits in the binary a few bytes from these strings, which is weak corroboration
# for that step being real rather than fitted.
#
# This is the one thing here that is inferred rather than read, so: if a future item shows a word
# that disagrees, the ladder is wrong and this comment holds every point it was built from.
RESISTANCE_WORDS = [
    (2.0, "Unparalleled"),
    (1.6, "Excellent"),
    (1.2, "Above Average"),
    (0.8, "Average"),
    (float("-inf"), "Below Average"),
]


def resistance_word(mod: float) -> str:
    for floor, word in RESISTANCE_WORDS:
        if mod >= floor:
            return word

    return RESISTANCE_WORDS[-1][1]


RESISTANCES = [
    ("Slashing", FLOAT_ARMOR_MOD_SLASH),
    ("Piercing", FLOAT_ARMOR_MOD_PIERCE),
    ("Bludgeoning", FLOAT_ARMOR_MOD_BLUDGEON),
    ("Cold", FLOAT_ARMOR_MOD_COLD),
    ("Fire", FLOAT_ARMOR_MOD_FIRE),
    ("Acid", FLOAT_ARMOR_MOD_ACID),
    ("Lightning", FLOAT_ARMOR_MOD_ELECTRIC),
    ("Nether", FLOAT_ARMOR_MOD_NETHER),
]


def _fmt(n) -> str:
    return f"{int(n):,}"


def build_detail(ints: dict, floats: dict, strings: dict, spells: list[int],
                 ench: list[dict] | None = None, ench_item: list[dict] | None = None,
                 dids: dict | None = None, bools: dict | None = None) -> dict:
    """The examine panel, as structured data plus ready-made lines.

    Both shapes on purpose: `lines` is what the tooltip renders today with no parsing, and the
    named fields are there so the front-end can lay it out properly later without the backend
    changing again. Every entry is omitted when absent rather than sent as null — an item with no
    armour level should show no armour row, not "Armor: —".
    """
    detail: dict = {}
    lines: list[str] = []

    # 158: which lines are ACTUALLY enhanced. The front-end used to colour by label regex - any
    # line starting "Armor Level"/"Damage"/"Melee Defense" went green whether or not a buff was on
    # it - so an untouched weapon in a pack advertised a bonus it did not have. In game the colour
    # means "above base", so it has to be driven by the arithmetic, not by the word.
    buffed: list[int] = []

    def add(label: str, value, is_buffed: bool = False):
        if value is None:
            return

        if is_buffed:
            buffed.append(len(lines))

        lines.append(f"{label}: {value}")

    def gap():
        """A blank line between groups, the way the client spaces this panel.

        Rendered by the front-end as a spacer div. Guarded against doubling and against leading
        blanks, so a group that turns out to be empty - most items have no enchantments - does not
        leave a hole where the client has none.
        """
        if lines and lines[-1] != "":
            lines.append("")

    # --- identity ---------------------------------------------------------------------------
    # The WIELDER's aura only reaches an item that can actually be enchanted. ACE gates it on
    # `wo.Wielder != null && wo.IsEnchantable`, and an unenchantable item (ResistMagic >= 9999 -
    # quest pieces, mostly) shows its BASE number in game no matter what the wielder is running.
    # The item's OWN enchantments are not gated: if it somehow has them, they already applied.
    enchantable = int(ints.get(INT_RESIST_MAGIC) or 0) < UNENCHANTABLE_RESIST_MAGIC
    wielder_ench = list(ench) if (ench and enchantable) else []

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

    # The client prints `Workmanship: Incomparable (9)` on its own line and does NOT print a
    # "Material:" line at all - the material is part of the item's NAME ("Gold Orb", "Ivory
    # Tetsubo", "Black Opal Ring"). The portal used to fuse the two into `Material: Superb Gold`,
    # which is a line the game never shows, built from an adjective that was also wrong.
    if workmanship:
        add("Workmanship", f"{detail['workmanshipName']} ({int(workmanship)})")

    # `PropertiesInt[ArmorLevel] += wo.EnchantmentManager.GetArmorMod()` (AppraiseInfo.cs:408).
    # Impenetrability VI is cast ONTO the armour, so it sits in the item's own registry and the
    # wielder's is not consulted. Black Breath's robe stores 150 and the client shows 350.
    #
    # The bonus is computed BEFORE the emptiness test, because CLOTHING has no armour of its own -
    # her Silk Baggy Pants store nothing and the client still shows "Armor Level: 200", all of it
    # from the enchantment. Testing the stored value first dropped the line entirely on exactly
    # the items where it is most worth seeing.
    gap()

    armor_base = ints.get(INT_ARMOR_LEVEL) or 0
    armor_bonus = enchantments.armor_mod(ench_item or [])

    if armor_base or armor_bonus:
        detail["armorLevel"] = armor_base + armor_bonus
        detail["armorLevelBase"] = armor_base
        add("Armor Level", _fmt(armor_base + armor_bonus), is_buffed=armor_bonus != 0)

    # --- protection ---------------------------------------------------------------------------
    #
    # Stored as multipliers around 1.0. Reported as the game reports them — a percentage away
    # from neutral — because "0.8" means nothing to a reader and "Slashing +20%" does.
    # 158: THE BANES ARE MERGED IN HERE, and they are ADDITIVE.
    #
    # `EnchantmentManager.GetArmorModVsType` uses `Float | SingleStat | Additive` and SUMS - it does
    # not multiply, which is what I assumed until the 158h oracle was pointed at it. The bane's
    # level is carried in the value, so Bane IV contributing +0.75 and Bane VI contributing more
    # needs no special handling; reading the stored number is the whole of it.
    #
    # Verified against the server for every armour piece Black Breath wears, e.g. her Pathwarden
    # Robe: stored slashing 0.8 + 0.75 = 1.55, bludgeoning 1.0 + 0.75 = 1.75, and nether 1.0
    # unchanged because nothing casts a Nether bane. AppraiseInfo agrees on all eight.
    #
    # These come from the ITEM's own registry, like ArmorLevel: banes are cast onto the armour.
    mods = {}
    mod_keys = {}
    mod_buffed = {}

    for label, prop in RESISTANCES:
        base = floats.get(prop)
        bonus = enchantments.additive(ench_item or [], enchantments.FLOAT, prop) if ench_item else 0.0

        # A MISSING RESISTANCE IS 1.0, NOT ABSENT - `armor.GetProperty(type) ?? 1.0f`
        # (ArmorProfile.cs). Most armour stores no ArmorModVsNether at all, and the client still
        # prints "Nether: Average (350)" for it. Treating absent as "no row" left a hole in the
        # middle of the list on exactly the damage type nothing ever buffs.
        is_armour = bool(ints.get(INT_ARMOR_LEVEL) or armor_bonus)
        mod = base if base is not None else (1.0 if (bonus or is_armour) else None)

        if mod is None:
            continue

        mod += bonus

        # CLAMPED TO +/-2.0, which ACE calls the "resistance clamp"
        # (`Math.Clamp(effectiveRL, -2.0f, 2.0f)`, ArmorProfile.cs). Only one of Black Breath's six
        # equipped items is affected - her Pathwarden Gauntlets sit at 1.3 + 0.75 = 2.05 and the
        # server reports 2.0 - so this is a cap that bites rarely and silently, and would have gone
        # on being wrong indefinitely without something comparing every value on every item.
        mod = max(-2.0, min(2.0, mod))

        # A mod of exactly 1.0 is still worth printing on ARMOUR, because the line reports the
        # effective armour level rather than a deviation - the client shows Black Breath's robe as
        # "Nether: Average (350)", the same 350 as its armour level, and dropping it left a gap
        # where the game has a row. On clothing with no armour there is nothing to multiply, so a
        # neutral multiplier really does say nothing and is still skipped.
        if abs(mod - 1.0) < 0.005 and not is_armour:
            continue

        mods[label] = round(mod, 3)
        mod_keys[label] = round(mod, 6)
        mod_buffed[label] = bonus != 0

    if mods:
        detail["resistances"] = mods

        # Named per damage type as well, so the oracle diff can check each one. The client keeps
        # these in a separate ArmorProfile rather than in its property dictionaries, and comparing
        # a single joined string against that would prove nothing.
        for label, val in mod_keys.items():
            detail["armorMod" + label.replace("Bludgeoning", "Bludgeon").replace("Slashing", "Slash")
                                    .replace("Piercing", "Pierce").replace("Lightning", "Electric")] = val
        # THE EFFECTIVE ARMOUR LEVEL PER DAMAGE TYPE, which is what the client shows and what the
        # number actually means: `AL x mod`. ACE calls it the "effective RL" (ArmorProfile.cs).
        #
        # The percentage this replaced was only ever coherent for mods BELOW 1. Once the banes were
        # merged in, values above 1 arrived and rendered as their own opposite - Black Breath's robe
        # sits at the 2.0 clamp, the best protection obtainable, and the line read "Slashing -100%".
        # That shipped for a few minutes and is the reason this is a per-type figure now.
        #
        # Confirmed against the client on her robe: armour level 350, slashing mod 2.0, and the game
        # prints "Slashing: Unparalleled (700)". 350 x 2.0 = 700.
        #
        # The descriptor word ("Unparalleled") is NOT reproduced. It comes from a threshold table
        # compiled into acclient.exe, not from anything the server sends, and inventing thresholds
        # that merely look right is how the workmanship table came to be wrong.
        armor_for_res = detail.get("armorLevel") or 0

        if armor_for_res:
            detail["protection"] = {k: round(armor_for_res * v) for k, v in mod_keys.items()}

            for k in mods:
                # The client greens each resistance a bane raised and leaves the rest plain - which
                # is why its Nether row is the only one in black on Black Breath's robe.
                #
                # "Word (value)" is the client's own shape, down to the two spaces before the
                # bracket in its format string.
                add(k, f"{resistance_word(mod_keys[k])}  ({_fmt(round(armor_for_res * mod_keys[k]))})",
                    is_buffed=mod_buffed.get(k, False))
        # NOTHING for an item with no armour level. A cloak carries ArmorModVs floats but no
        # ArmorLevel, and the client prints no resistance rows for one at all - there is nothing to
        # multiply, so the figures describe nothing. The portal used to emit "Protection: Slashing
        # x0.8, ..." here, which is a row the game does not have built from numbers it does not use.

    gap()

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
        # `Mana Cost: 1 point per %d seconds.` - again the client's format string. "Mana Burn"
        # is a phrase the game does not use anywhere.
        add("Mana Cost", f"1 point per {seconds:.0f} seconds.")

    if (difficulty := ints.get(INT_DIFFICULTY)):
        # Held, not printed: it is half of the "Activation requires" sentence assembled below.
        detail["activationDifficulty"] = difficulty

    gap()

    # --- requirements (138) ---------------------------------------------------------------------
    #
    # WieldRequirements says how to READ the other two. Skill/RawSkill make WieldSkillType a skill
    # id; Attrib and the Secondary variants make it an attribute; Level ignores it entirely and
    # WieldDifficulty is a character level. The old code assumed "skill" always and printed the
    # requirement TYPE where the skill belonged.
    reqs = []

    for req_prop, skill_prop, diff_prop in WIELD_SETS:
        kind = ints.get(req_prop)
        difficulty = ints.get(diff_prop)

        if not difficulty:
            continue

        if kind in (1, 2):                       # Skill / RawSkill
            what = curves.enum_label("skill", ints.get(skill_prop))
        elif kind in (3, 4):                     # Attrib / RawAttrib
            what = curves.enum_label("attribute", ints.get(skill_prop))
        elif kind in (5, 6):                     # SecondaryAttrib / Raw
            what = curves.enum_label("attribute2nd", ints.get(skill_prop))
        elif kind == 7:                          # Level - the difficulty IS the level
            what = "Level"
        elif kind == 11:
            what = CREATURE_TYPE_NAMES.get(ints.get(skill_prop))
        else:
            what = WIELD_REQUIREMENT_NAMES.get(kind)

        reqs.append({"type": WIELD_REQUIREMENT_NAMES.get(kind), "of": what, "level": difficulty})
        # `Wield requires %s %d` is the client's format, and it reads as a SENTENCE rather than a
        # labelled row - "Wield requires base Two Handed Combat 325", "Wield requires level 90".
        # "base" appears for the raw-skill kinds, which is what distinguishes a requirement on your
        # unbuffed skill from one on your current.
        base = "base " if kind in (2, 4, 6) else ""

        # "Wield requires level 90" - lowercase, where a SKILL keeps its capitals ("base Two Handed
        # Combat 325"). The client distinguishes the two and so does this.
        if what == "Level":
            what = "level"
        lines.append(f"Wield requires {base}{what} {_fmt(difficulty)}" if what
                     else f"Wield requires {_fmt(difficulty)}")

    if reqs:
        detail["wieldRequirements"] = reqs
        # Kept for anything already reading the old singular field.
        detail["wieldRequirement"] = {"skill": reqs[0]["of"], "level": reqs[0]["level"]}

    if (lvl := ints.get(INT_USE_REQUIRES_LEVEL)):
        detail["useRequiresLevel"] = lvl
        add("Requires Level", _fmt(lvl))

    use_skill = curves.enum_label("skill", ints.get(INT_USE_REQUIRES_SKILL))
    use_level = ints.get(INT_USE_REQUIRES_SKILL_LEVEL)

    if use_skill and use_level:
        detail["useRequiresSkill"] = {"skill": use_skill, "level": use_level}
        add("Requires Skill", f"{use_skill} {_fmt(use_level)}")

    if (rank := ints.get(INT_ITEM_ALLEGIANCE_RANK_LIMIT)):
        detail["allegianceRankLimit"] = rank
        add("Requires Allegiance Rank", _fmt(rank))

    # `Activation requires Arcane Lore: 129, Two Handed Combat: 253` - ONE sentence, where the
    # portal printed "Difficulty: 129" and "Activation Skill Level: 253" as two labelled rows and
    # named neither of the skills involved. The Arcane Lore figure is the item's ItemDifficulty,
    # and the second clause names the item's own required skill.
    #
    # `Activation requires ` and `Arcane Lore: %d` are both the client's strings.
    skill_limit = ints.get(INT_ITEM_SKILL_LEVEL_LIMIT)
    act_skill = curves.enum_label("skill", ints.get(INT_USE_REQUIRES_SKILL)) or detail.get("weaponSkill")
    act_parts = []

    if detail.get("activationDifficulty"):
        act_parts.append(f"Arcane Lore: {_fmt(detail['activationDifficulty'])}")

    if skill_limit:
        detail["activationSkillLevel"] = skill_limit
        act_parts.append(f"{act_skill}: {_fmt(skill_limit)}" if act_skill else _fmt(skill_limit))

    if act_parts:
        lines.append("Activation requires " + ", ".join(act_parts))

    if (mana_cost := ints.get(INT_ITEM_MANA_COST)):
        detail["manaCost"] = mana_cost
        add("Mana Cost", _fmt(mana_cost))

    gap()

    # --- weapons (137) --------------------------------------------------------------------------
    #
    # THE EXAMINE PANEL HAD NO WEAPON HANDLING AT ALL. It was templated off a robe, so a sword
    # reported its value, burden and material and then stopped - no damage, no speed, no skill.
    # Chris found it by clicking a weapon and getting a near-empty window.
    #
    # Everything below is omitted when absent, so a robe gains nothing and a caster shows only the
    # caster-ish half of it. That is why there is no "is this a weapon" test: the properties
    # decide, which also means a weapon type nobody anticipated still renders whatever it carries.
    # DAMAGE CARRIES ENCHANTMENTS, exactly as WeaponDefense does - `baseDamage + damageBonus +
    # auraDamageBonus` (WeaponProfile.GetDamage). We merged one and not the other, so a Blood
    # Drinker weapon read at its stored numbers while the game showed twenty points more.
    #
    # The aura half is gated on IsEnchantable; the ITEM's own bonus is not - that asymmetry is
    # ACE's, not a simplification here.
    if (damage := ints.get(INT_DAMAGE)):
        variance = floats.get(FLOAT_DAMAGE_VARIANCE)

        dmg_bonus = enchantments.damage_bonus(ench_item or [])

        if wielder_ench:
            dmg_bonus += enchantments.damage_bonus(wielder_ench)

        damage = max(0, damage + dmg_bonus)
        detail["damage"] = {"max": damage, "variance": variance}

        # ONE DECIMAL on the minimum, and the damage TYPE on the same line - `Damage: 26.4 - 44,
        # Bludgeoning`. The client derives the minimum from the variance rather than storing it,
        # and rounding it to a whole number lost the .4 it prints.
        types = _flags(ints.get(INT_DAMAGE_TYPE), DAMAGE_TYPE_NAMES)
        suffix = f", {', '.join(types)}" if types else ""

        if types:
            detail["damageTypes"] = types

        if variance:
            low = damage * (1 - variance)
            # `:g`, not a fixed decimal count. The client prints the natural value - Chris's Cestus
            # reads "25.38 - 54" and his Tetsubo "26.4 - 44" - so two decimals, one, or none as the
            # arithmetic falls out. A fixed ".1f" would have written "45.0" where the game says 45.
            add("Damage", f"{low:g} - {_fmt(damage)}{suffix}", is_buffed=dmg_bonus != 0)
        else:
            add("Damage", f"{_fmt(damage)}{suffix}", is_buffed=dmg_bonus != 0)

    if (skill := curves.enum_label("skill", ints.get(INT_WEAPON_SKILL))):
        detail["weaponSkill"] = skill
        # The client's label is "Skill", not "Attack Skill".
        add("Skill", skill)

    # SPEED IS BUFFED TOO, and negative means faster: `baseSpeed + speedMod`, floored at 0
    # (WeaponProfile.GetWeaponSpeed). Black Breath's Swift Killer carries -60, which takes this
    # Tetsubo from 45 to 0 - the client reads "Very Fast (0)" where we printed 45.
    if (speed := ints.get(INT_WEAPON_TIME)) is not None:
        spd_mod = enchantments.speed_mod(ench_item or [])

        if wielder_ench:
            spd_mod += enchantments.speed_mod(wielder_ench)

        speed = max(0, speed + spd_mod)

        if speed or spd_mod:
            detail["weaponSpeed"] = speed
            # The descriptor word ("Very Fast") is NOT added: the four strings are in acclient.exe
            # but only three points are known - 0 Very Fast, 15 Fast, 45 Average - which does not
            # pin the boundaries. Same rule as the resistance ladder: measured or not at all.
            add("Speed", _fmt(speed), is_buffed=spd_mod != 0)

    # MULTIPLIERS around 1.0 - 1.13 is "+13%". Confirmed against ACE, which defaults each of these
    # to 1.0 when absent (e.g. `weapon.ElementalDamageMod ?? 1.0f`).
    for label, prop, key in (
        ("Damage Bonus", FLOAT_DAMAGE_MOD, "damageMod"),
        ("Attack Bonus", FLOAT_WEAPON_OFFENSE, "attackMod"),
        ("Melee Defense Bonus", FLOAT_WEAPON_DEFENSE, "meleeDefenseMod"),
        ("Missile Defense Bonus", FLOAT_WEAPON_MISSILE_DEFENSE, "missileDefenseMod"),
        ("Magic Defense Bonus", FLOAT_WEAPON_MAGIC_DEFENSE, "magicDefenseMod"),
        ("Elemental Damage", FLOAT_ELEMENTAL_DAMAGE_MOD, "elementalDamageMod"),
        ("Slayer Bonus", FLOAT_SLAYER_DAMAGE_BONUS, "slayerDamageBonus"),
        ("Ignores Armor", FLOAT_IGNORE_ARMOR, "ignoreArmor"),
    ):
        raw = floats.get(prop)

        # 158: AppraiseInfo adds the ITEM's and the WIELDER's defense mods before the client sees
        # the number, so the stored 1.17 renders in game as +32.0% while a +0.15 aura is up. Only
        # WeaponDefense gets this - it is the one AppraiseInfo modifies in that block.
        # ACE adds them SEPARATELY - `defenseMod + auraDefenseMod` - rather than pooling the two
        # registries. That matters: each side layers its own spells first, so a concatenated list
        # could pick a top layer across two objects that never competed.
        bonus = 0.0

        if prop == FLOAT_WEAPON_DEFENSE and raw is not None and (wielder_ench or ench_item):
            bonus = enchantments.defense_mod(ench_item or []) + enchantments.defense_mod(wielder_ench)
            raw += bonus

        if (text := _pct(raw)) is not None:
            detail[key] = raw
            add(label, text, is_buffed=bonus != 0)

    # 158: A FRACTION, NOT A MULTIPLIER, and it sat in the list above being read as one.
    #
    # ACE defaults it to ZERO, in both the combat path and the tinkering recipe:
    #
    #     var baseMod = (float)(weapon.ManaConversionMod ?? 0.0f);   // WorldObject_Weapon.cs:247
    #     return 1.0f + baseMod * enchantmentMod;                    //  ...          .cs:259
    #
    # so 0.08 means +8%. Running it through the multiplier formula gave (0.08 - 1) = **-92%** on
    # the live site - a bonus rendered as a near-total penalty, on an item whose whole purpose is
    # that bonus. It is the one entry in that list ACE does not default to 1.0, and it had been
    # grouped with the ones that are.
    if (mana_conv := floats.get(FLOAT_MANA_CONVERSION_MOD)):
        # Multiplicative here, not additive - hermetic link / void scale the base bonus rather than
        # adding to it, and ACE notes they "are only effective if there is a base mod".
        # `wielderManaConvMod * weaponManaConvMod` (ResistMask.cs:127) - multiplied, not added,
        # and computed per registry for the same layering reason as above.
        mc_mod = 1.0

        if wielder_ench or ench_item:
            mc_mod = (enchantments.mana_conv_mod(ench_item or [])
                      * enchantments.mana_conv_mod(wielder_ench))
            mana_conv *= mc_mod

        detail["manaConversionMod"] = mana_conv
        add("Mana Conversion Bonus", f"{'+' if mana_conv > 0 else ''}{round(mana_conv * 100, 1):g}%",
            is_buffed=mc_mod != 1.0)

    if (crit := floats.get(FLOAT_CRITICAL_FREQUENCY)):
        detail["criticalFrequency"] = crit
        add("Critical Chance", f"{round(crit * 100, 1):g}%")

    if (critmul := floats.get(FLOAT_CRITICAL_MULTIPLIER)):
        detail["criticalMultiplier"] = critmul
        add("Critical Damage", f"x{round(critmul, 2):g}")

    if (cleave := ints.get(INT_CLEAVING)):
        detail["cleaving"] = cleave
        # `Cleave: %d enemies in front arc.` - the client's own format string, found in
        # acclient.exe. "Cleaving: 2 targets" was our phrasing for both halves of the line.
        add("Cleave", f"{cleave} enemies in front arc.")

    gap()

    # --- missile weapons, which had nothing of their own --------------------------------------
    if (rng := ints.get(INT_WEAPON_RANGE)):
        detail["weaponRange"] = rng
        add("Range", _fmt(rng))

    if (ammo := AMMO_TYPE_NAMES.get(ints.get(INT_AMMO_TYPE))):
        detail["ammoType"] = ammo
        add("Ammo Type", ammo)

    if (vel := floats.get(FLOAT_MAXIMUM_VELOCITY)):
        detail["maximumVelocity"] = vel
        add("Velocity", f"{round(vel, 1):g}")

    # A slayer bonus is meaningless without saying what it slays; the bonus was already shown.
    if (slays := CREATURE_TYPE_NAMES.get(ints.get(INT_SLAYER_CREATURE_TYPE))):
        detail["slays"] = slays
        add("Slays", slays)

    # Resistance rending - the type is a DamageType, the modifier a multiplier.
    rend_type = _flags(ints.get(INT_RESISTANCE_MODIFIER_TYPE), DAMAGE_TYPE_NAMES)
    rend = floats.get(FLOAT_RESISTANCE_MODIFIER)

    if rend_type and rend:
        detail["resistanceRending"] = {"types": rend_type, "modifier": rend}
        add("Resistance Rending", f"{', '.join(rend_type)} x{round(rend, 2):g}")

    imbues = []

    for prop in INT_IMBUED_EFFECTS:
        imbues += _flags(ints.get(prop), IMBUED_EFFECT_NAMES)

    if imbues:
        detail["imbues"] = sorted(set(imbues))
        add("Imbued", ", ".join(sorted(set(imbues))))

    gap()

    # --- armour and clothing -------------------------------------------------------------------
    if (armor_types := _flags(ints.get(INT_ARMOR_TYPE), ARMOR_TYPE_NAMES)):
        detail["armorType"] = armor_types
        add("Armor Type", ", ".join(armor_types))

    set_id = ints.get(INT_EQUIPMENT_SET_ID)

    if (equip_set := EQUIPMENT_SET_NAMES.get(set_id)):
        detail["equipmentSet"] = equip_set
        add("Set", equip_set)

    # NO gap: this block emits nothing for armour or weapons, and a spacer in front of a block
    # that produces no lines still lands - separating "Armor Type" from "Aura", which belong
    # together. gap() cannot know a block will be empty, so blocks that are usually empty do not
    # get one.
    # --- food, potions and salvage --------------------------------------------------------------
    boost_vital = curves.enum_label("attribute2nd", ints.get(INT_BOOSTER_ENUM))
    boost = ints.get(INT_BOOST_VALUE)

    if boost_vital and boost:
        detail["restores"] = {"vital": boost_vital, "amount": boost}
        add("Restores", f"{_fmt(boost)} {boost_vital}")

    if (units := ints.get(INT_NUM_ITEMS_IN_MATERIAL)):
        detail["salvageUnits"] = units
        add("Salvage Units", _fmt(units))

    if (life := ints.get(INT_REMAINING_LIFESPAN)):
        detail["remainingLifespan"] = life
        add("Remaining Lifespan", f"{_fmt(life)}s")

    if ints.get(INT_UNIQUE):
        detail["unique"] = True
        lines.append("Unique")

    # NO gap here on purpose: "Armor Type: Cloth" and "Aura: Magical" are both one-line facts about
    # the item itself, and separating them produced two one-line islands where the client has a
    # single small block. A spacer is only worth a line when it divides groups, not entries.
    # --- general facts that apply to anything ---------------------------------------------------
    if (effects := _flags(ints.get(INT_UI_EFFECTS), UI_EFFECT_NAMES)):
        detail["uiEffects"] = effects
        # NOT PRINTED. "Aura" appears nowhere in acclient.exe - the client conveys UiEffects as
        # the ICON's glow, not as a line of text, and this row was ours entirely. The value is kept
        # in `detail` for anything that wants it; it just stops pretending the game says it.
        pass

    # Structure is uses remaining - tinkering tools, keys, spell components all carry it, and a
    # player wants to know how many are left far more than they want most of the rest of this.
    structure, max_structure = ints.get(INT_STRUCTURE), ints.get(INT_MAX_STRUCTURE)

    if max_structure:
        detail["structure"] = {"current": structure or 0, "max": max_structure}
        add("Uses", f"{_fmt(structure or 0)} / {_fmt(max_structure)}")

    if (gem_count := ints.get(INT_GEM_COUNT)):
        gem = MATERIAL_NAMES.get(ints.get(INT_GEM_TYPE))
        detail["gems"] = {"count": gem_count, "type": gem}
        # NOT ITS OWN ROW. The client folds this into the closing sentence - ", set with 3 pieces
        # of White Jade" - using the strings `, set with ` and `pieces of `. Held here and appended
        # to the flavour line below.
        detail["gemText"] = (f"{gem_count} pieces of {gem}" if gem_count != 1 else f"1 {gem}") if gem else None

    items_cap, cont_cap = ints.get(INT_ITEMS_CAPACITY), ints.get(INT_CONTAINERS_CAPACITY)

    if items_cap or cont_cap:
        detail["capacity"] = {"items": items_cap or 0, "containers": cont_cap or 0}
        add("Capacity", f"{items_cap or 0} items, {cont_cap or 0} packs")

    # Attuned cannot be given away, Bonded cannot be dropped. Both change what a player can do
    # with the item, so both are worth a line.
    # --- properties ------------------------------------------------------------------------------
    #
    # The client's "Properties:" row, which the portal did not have at all. Its word list lives in
    # acclient.exe beside the imbue names - Retained, Unenchantable, Magic Absorbing, Phantasmal -
    # and the ones that apply here are Retained and Unenchantable.
    #
    # Retained is PropertyBool 91, and item BOOLS were never loaded until now: the panel could not
    # have shown this however it was written.
    props_list = []

    if (bools or {}).get(BOOL_RETAINED):
        detail["retained"] = True
        props_list.append("Retained")

    if int(ints.get(INT_RESIST_MAGIC) or 0) >= UNENCHANTABLE_RESIST_MAGIC:
        detail["unenchantable"] = True
        props_list.append("Unenchantable")

    # `Cast on Strike` - the client's own string, and it follows from having a ProcSpell.
    if (dids or {}).get(DID_PROC_SPELL):
        props_list.append("Cast on Strike")

    if ints.get(INT_ATTUNED):
        detail["attuned"] = True
        props_list.append("Attuned")

    if ints.get(INT_BONDED):
        detail["bonded"] = True
        props_list.append("Bonded")

    if props_list:
        gap()
        add("Properties", ", ".join(props_list))

    gap()

    # --- spells ---------------------------------------------------------------------------------
    #
    # THE CAST-ON-USE SPELL IS NOT IN THE SPELL BOOK. An item's `PropertyDataId.Spell` (28) is the
    # one it casts when activated, and the client lists it FIRST, ahead of the book. The Gold Orb's
    # book holds four rows while the game shows five - the missing one, "Mana Boost Other VI", is
    # the orb's whole purpose and was the only spell a player would actually cast from it.
    spell_ids = []

    if dids and (cast := dids.get(DID_SPELL)):
        spell_ids.append(int(cast))

    # ProcSpell (DataId 55) - the spell an item casts ON STRIKE rather than on use. AppraiseInfo
    # adds it to the same SpellBook list, and without it Chris's Silk Cloak listed no spells at all
    # while the client showed "Eye of the Storm".
    if dids and (proc := dids.get(DID_PROC_SPELL)):
        detail["procSpell"] = int(proc)

        if int(proc) not in spell_ids:
            spell_ids.append(int(proc))

    spell_ids += [s for s in (spells or []) if s not in spell_ids]

    if spell_ids:
        table = curves.spell_table()
        named = []

        for spell_id in spell_ids:
            meta = table.get(spell_id) or {}
            named.append({
                "id": spell_id,
                "name": meta.get("name") or f"Spell {spell_id}",
                "desc": meta.get("desc"),
            })

        detail["spells"] = named
        lines.append("Spells: " + ", ".join(s["name"] for s in named))

        # The client prints these under their own heading, one per spell, prefixed with "~". They
        # are most of what its panel actually says - the portal listed the names and stopped.
        described = [s for s in named if s.get("desc")]

        if described:
            # The client keeps the NAMES and the DESCRIPTIONS as two blocks, not one - see any
            # weapon panel, where the requirements sit between them.
            gap()
            lines.append("Spell Descriptions:")
            lines.extend(f"~ {s['name']}: {s['desc']}" for s in described)

    # --- enchantments currently ON the item ------------------------------------------------------
    #
    # The client's own "Enchantments:" block, and the last thing missing from this panel. Found via
    # the 158h oracle rather than by eye: AppraiseInfo puts the item's ACTIVE enchantments into the
    # same SpellBook list as its innate spells, ORed with 0x80000000 to tell them apart
    # (AppraiseInfo.cs:502). Diffing against it showed eight ids we were not rendering at all -
    # Impenetrability and the seven banes, which is every buff on every piece of armour.
    #
    # These are the SOURCE of the numbers merged above: Impenetrability is the +200 armour, the
    # banes are the resistance mods. Listing them is what lets a player see WHY a value is enhanced
    # rather than just that it is.
    if ench_item:
        gap()
        table = curves.spell_table()
        seen_ids: set[int] = set()
        active = []

        for e in ench_item:
            sid = int(e.get("spell") or 0)

            # One line per spell, not per row: a spell that modifies several stats writes a row per
            # stat, and the client names the spell once.
            if not sid or sid in seen_ids:
                continue

            seen_ids.add(sid)
            meta = table.get(sid) or {}
            active.append({"id": sid, "name": meta.get("name") or f"Spell {sid}", "desc": meta.get("desc")})

        if active:
            detail["enchantments"] = active
            lines.append("Enchantments:")
            lines.extend(f"~ {a['name']}: {a['desc']}" if a["desc"] else f"~ {a['name']}" for a in active)

    gap()

    # --- flavour ---------------------------------------------------------------------------------
    for key, prop in (("use", STRING_USE), ("shortDesc", STRING_SHORT_DESC),
                      ("longDesc", STRING_LONG_DESC)):
        text = strings.get(prop)

        if text:
            detail[key] = text

    # The description reads as prose and belongs at the end, after the numbers.
    for key in ("longDesc", "use"):
        if detail.get(key):
            text = detail[key]

            # ", set with 3 pieces of White Jade" - the client's closing clause, on the same line
            # as the name rather than as a "Gems:" row above it.
            if key == "longDesc" and detail.get("gemText"):
                text = f"{text}, set with {detail['gemText']}"

            lines.append(text)

    while lines and lines[-1] == "":
        lines.pop()

    detail["lines"] = lines
    # Indices into `lines`, so the front-end colours what the game colours and nothing else.
    detail["buffedLines"] = buffed

    return detail


def display_name(base_name: str, ints: dict, floats: dict) -> str:
    """The name a player would recognise.

    127 #6: salvage arrives as "Salvage (6)" because that is literally the stored name — the
    material is a separate property the client merges in on display. So "Salvage (6)" becomes
    "Steel Salvage (6)", which is what the bag actually contains.

    158: the same merge applies to EVERY item carrying a material, not just salvage. The client
    titles them "Gold Orb", "Ivory Tetsubo", "Black Opal Ring" while the shard stores "Orb",
    "Tetsubo", "Ring" - so the portal was showing a plainer name than the game for every piece of
    loot a player owns.

    Prefixing on "material is present" is not a guess about which items qualify; it is what the
    data already separates. Loot-generated items carry MaterialType and retail/quest ones do not -
    Black Breath's Ring, Orb, Cloak and Poet's Shirt all have it, while Pathwarden Gauntlets and
    the Hoary Mattekar Robe have NULL. So the items that get a prefix are exactly the ones the
    client prefixes, with no list to maintain.
    """
    material = MATERIAL_NAMES.get(ints.get(INT_MATERIAL_TYPE))

    if not material:
        return base_name

    # Already merged in by whoever stored it - do not produce "Gold Gold Orb".
    if base_name.startswith(material):
        return base_name

    return f"{material} {base_name}"
