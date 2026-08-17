"""Shadowgain 161 — the examine panel's LAYOUT, pinned against the client.

    cd shadowgain/web && python -m pytest api/test_panel_layout.py

WHY THIS IS SEPARATE FROM test_appraise.py

`test_appraise.py` pins the NUMBERS, because a value that disagrees with the game is a bug the
oracle can prove. This file pins the WORDS AND THE SPACING, which the oracle deliberately ignores
- `appraise-diff.py` compares values only, so every line below was invisible to it and had to come
from Chris reading a real panel in the client and screenshotting it.

That is the whole reason these regress silently: nothing else in the project can see them.

Every assertion here traces to one of four screenshots taken 2026-08-16 - a Black Opal Ring, a
Training/Academy Spadone pair, Silk Baggy Pants and a Hoary Mattekar Robe.
"""

from __future__ import annotations

from . import items


def _lines(**kw) -> list[str]:
    ints = kw.pop("ints", {})
    floats = kw.pop("floats", {})
    return items.build_detail(ints, floats, {}, [], **kw)["lines"]


# --- Covers ---------------------------------------------------------------------------------------


def test_covers_names_every_area_in_the_clients_order():
    """Hoary Mattekar Robe: `Covers Chest, Abdomen, Upper Arms, Lower Arms, Upper Legs, ...`

    ValidLocations 32512 (0x7F00). The order is EquipMask bit order, which puts the arms between
    the abdomen and the legs rather than anywhere anatomical.
    """
    lines = _lines(ints={items.INT_VALID_LOCATIONS: 32512})

    assert "Covers Chest, Abdomen, Upper Arms, Lower Arms, Upper Legs, Lower Legs, Feet" in lines


def test_covers_reads_valid_locations_not_the_current_slot():
    """So an item in a pack still describes itself - the client does."""
    lines = _lines(ints={items.INT_VALID_LOCATIONS: 196})   # abdomen + both legs

    assert "Covers Abdomen, Upper Legs, Lower Legs" in lines


def test_a_weapon_gets_no_covers_line():
    """Self-gating: a weapon's ValidLocations sets none of the coverage bits."""
    lines = _lines(ints={items.INT_VALID_LOCATIONS: 0x00100000})

    assert not any(ln.startswith("Covers") for ln in lines)


# --- Properties, and the sentence that is not one -------------------------------------------------


def test_dyeable_appears_in_the_properties_line():
    """Silk Baggy Pants read `Properties: Dyeable` and we had no Properties line at all."""
    lines = _lines(bools={items.BOOL_DYABLE: 1})

    assert "Properties: Dyeable" in lines


def test_cannot_be_sold_is_its_own_block():
    """Academy Spadone prints Properties and this sentence as two blocks with a blank between."""
    lines = _lines(bools={items.BOOL_IS_SELLABLE: 0, items.INT_BONDED: 0})
    text = "This item cannot be sold."

    assert text in lines


def test_a_sellable_item_says_nothing():
    assert "This item cannot be sold." not in _lines(bools={items.BOOL_IS_SELLABLE: 1})


def test_an_absent_sellable_flag_says_nothing():
    """The trap: `.get()` returns None for most items, and None is falsy.

    Reading it as "not sellable" would have put the sentence on nearly every item in the game.
    """
    assert "This item cannot be sold." not in _lines(bools={})


# --- spacing --------------------------------------------------------------------------------------


def test_activation_runs_straight_into_spellcraft():
    """Black Opal Ring - one block, no blank after the Activation sentence.

    Chris found this on jewelry, then on shirts and pants: every item carrying both halves.
    """
    lines = _lines(ints={items.INT_DIFFICULTY: 242, items.INT_SPELLCRAFT: 230})
    act = next(i for i, ln in enumerate(lines) if ln.startswith("Activation requires"))

    assert lines[act + 1] == "Spellcraft: 230."


def test_enchantments_heading_is_followed_by_a_blank_line():
    """...and `Spell Descriptions:` is NOT. The asymmetry is the client's."""
    lines = _lines(ench_item=[{"spell": 1486, "category": 160, "power": 250,
                               "start": 0.0, "type": 0, "key": 0, "value": 0.0}])
    head = lines.index("Enchantments:")

    assert lines[head + 1] == ""
    assert lines[head + 2].startswith("~ Impenetrability VI")


# --- wording --------------------------------------------------------------------------------------


def test_value_carries_no_unit():
    """`Value: 9,290`, not "9,290 pyreal" - checked against four screenshots."""
    assert "Value: 9,290" in _lines(ints={items.INT_VALUE: 9290})


def test_spellcraft_and_mana_end_in_a_full_stop():
    """The client writes that whole block as sentences."""
    lines = _lines(ints={items.INT_SPELLCRAFT: 274, items.INT_MAX_MANA: 981,
                         items.INT_CURRENT_MANA: 981})

    assert "Spellcraft: 274." in lines
    assert "Mana: 981 / 981." in lines


def test_resistances_are_in_the_clients_order_and_wording():
    """Fire BEFORE Cold, and the seventh one is Electric.

    "Lightning" is ArmorProfile's field name. The player never sees that word.
    """
    labels = [label for label, _ in items.RESISTANCES]

    assert labels == ["Slashing", "Piercing", "Bludgeoning", "Fire", "Cold",
                      "Acid", "Electric", "Nether"]


# --- 161b. ammunition and ranged weapons ---------------------------------------------------------


def _dmg_aura(value: int) -> list[dict]:
    return [{"spell": 1, "category": 1, "power": 1, "start": 0.0,
             "type": 0x4 | 0x8000 | 0x1000, "key": 44, "value": value}]


def test_ammunition_does_not_take_the_wielders_damage_aura():
    """Misadventure's Blunt Arrow read 21 against the game's 9 - Blood Drinker on HER.

    `show_ammo_buff` is false on LIVE, so the aura stops at the bow.
    """
    arrow = items.build_detail({items.INT_DAMAGE: 9}, {}, {}, [], ench=_dmg_aura(12),
                               weenie_type=items.AMMUNITION_WEENIE_TYPE, dials={})

    assert arrow["damage"]["max"] == 9


def test_the_ammo_dial_lets_the_aura_through():
    arrow = items.build_detail({items.INT_DAMAGE: 9}, {}, {}, [], ench=_dmg_aura(12),
                               weenie_type=items.AMMUNITION_WEENIE_TYPE,
                               dials={"show_ammo_buff": True})

    assert arrow["damage"]["max"] == 21


def test_ammunition_keeps_its_own_damage_enchantment():
    """Only the reflected aura is dropped - a buff cast on the arrow itself still counts."""
    arrow = items.build_detail({items.INT_DAMAGE: 9}, {}, {}, [], ench_item=_dmg_aura(4),
                               weenie_type=items.AMMUNITION_WEENIE_TYPE, dials={})

    assert arrow["damage"]["max"] == 13


def test_ammunition_offense_and_defense_are_flat_neutral():
    """`if (weapon is Ammunition) return 1.0f;` in both getters - so neither line is drawn."""
    arrow = items.build_detail({}, {items.FLOAT_WEAPON_OFFENSE: 1.4,
                                    items.FLOAT_WEAPON_DEFENSE: 1.4}, {}, [],
                               weenie_type=items.AMMUNITION_WEENIE_TYPE)

    assert "attackMod" not in arrow
    assert "meleeDefenseMod" not in arrow


def test_a_ranged_weapon_gets_no_attack_enchantment():
    """`!weapon.IsRanged` gates BOTH halves of GetWeaponOffense, so Heart Seeker does nothing.

    Ported from the source; no bow has been equipped during an oracle sweep yet.
    """
    offense = [{"spell": 1, "category": 1, "power": 1, "start": 0.0,
                "type": 0x8 | 0x8000 | 0x1000, "key": items.FLOAT_WEAPON_OFFENSE, "value": 0.1}]
    bow = items.build_detail({items.INT_DEFAULT_COMBAT_STYLE: items.COMBAT_STYLE_BOW},
                             {items.FLOAT_WEAPON_OFFENSE: 1.05}, {}, [], ench=offense,
                             weenie_type=next(iter(sorted(items.WEAPON_WEENIE_TYPES))))

    assert round(bow["attackMod"], 6) == 1.05
