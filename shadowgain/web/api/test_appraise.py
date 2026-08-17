"""Shadowgain 160 — the four disagreements the appraise oracle found, pinned.

    cd shadowgain/web && python -m pytest api/test_appraise.py

WHY THIS FILE EXISTS

`tools/appraise-diff.py` compares our examine panel against the `AppraiseInfo` the client is
actually sent. Run across 101 equipped items belonging to the nine characters online on
2026-08-16, it found 20 items disagreeing in four distinct ways. Every one of them had been live
for weeks, and none was visible without the oracle.

The oracle is the real check and it needs a running world, nine logged-in players and a console.
These tests are the part of it that can run anywhere, so a regression is caught by `pytest`
rather than by the next sweep - or by a player.

Each test names the item it was found on, because "damage on clothing" is abstract and "Steel
Toed Boots said Damage: 25 and the game said nothing" is not.
"""

from __future__ import annotations

from . import enchantments, items

MELEE_WEAPON = next(iter(sorted(items.WEAPON_WEENIE_TYPES)))


def _ench(spell: int, category: int, power: int, start: float = 0.0,
          type_: int = 0, key: int = 0, value: float = 0.0) -> dict:
    """One registry row, in the shape `enchantments.load_many` produces."""
    return {"spell": spell, "category": category, "power": power, "start": start,
            "type": type_, "key": key, "value": value}


# --- A. weapon fields on clothing ---------------------------------------------------------------


def test_clothing_with_a_damage_property_is_not_a_weapon():
    """Steel Toed Boots (25), Opal Gauntlets (20), Sandals (17) - all stored Damage, none a weapon.

    Boots and gauntlets carry a Damage for kick and punch attacks. AppraiseInfo excludes clothing
    from its weapon block explicitly, so the client never shows it and neither should we.
    """
    boots = {items.INT_DAMAGE: 25}

    assert not items.is_weaponlike(items.CLOTHING_WEENIE_TYPE, boots)


def test_a_real_weapon_is_still_a_weapon():
    assert items.is_weaponlike(MELEE_WEAPON, {items.INT_DAMAGE: 18})


def test_a_weapon_weenie_type_needs_no_damage_property():
    """A Caster stores no Damage and still gets the block - it is in ACE's type list outright."""
    assert items.is_weaponlike(MELEE_WEAPON, {})


def test_a_non_clothing_item_with_damage_is_a_weapon():
    """The first half of ACE's gate: `wo.Damage != null && !(wo is Clothing)`."""
    assert items.is_weaponlike(items.CLOTHING_WEENIE_TYPE + 1000, {items.INT_DAMAGE: 18})


def test_the_damage_line_is_dropped_for_clothing_end_to_end():
    """Through `build_detail`, not just the predicate - the bug was in the wiring, once."""
    detail = items.build_detail({items.INT_DAMAGE: 25}, {}, {}, [],
                                weenie_type=items.CLOTHING_WEENIE_TYPE)

    assert "damage" not in detail

    armed = items.build_detail({items.INT_DAMAGE: 25}, {}, {}, [], weenie_type=MELEE_WEAPON)

    assert armed["damage"]["max"] == 25


# --- D. dormant enchantments listed as active ----------------------------------------------------


def test_only_the_strongest_enchantment_in_a_category_survives():
    """Kill's Pathwarden Helm carried Impenetrability I and II; the game lists only II.

    Both rows are live and both are in the registry - they share spell category 160, so the
    weaker is dormant until the stronger expires. Printed together they read as +70 on an item
    the panel says is +50.
    """
    rows = [_ench(51, category=160, power=1), _ench(1482, category=160, power=50)]

    assert [e["spell"] for e in enchantments.top_layer_all(rows)] == [1482]


def test_separate_categories_both_survive():
    """The rule suppresses WITHIN a category, never across - armour keeps Impenetrability and
    all seven banes at once."""
    rows = [_ench(1486, category=160, power=250), _ench(1498, category=161, power=250)]

    assert sorted(e["spell"] for e in enchantments.top_layer_all(rows)) == [1486, 1498]


def test_the_later_cast_wins_a_tie_on_power():
    rows = [_ench(1, category=5, power=50, start=-10.0),
            _ench(2, category=5, power=50, start=-1.0)]

    assert [e["spell"] for e in enchantments.top_layer_all(rows)] == [2]


def test_cooldown_rows_are_not_enchantments():
    rows = [_ench(enchantments.SPELL_CATEGORY_COOLDOWN + 1, category=900, power=1)]

    assert enchantments.top_layer_all(rows) == []


def test_the_panel_lists_only_the_surviving_layer():
    detail = items.build_detail(
        {}, {}, {}, [],
        ench_item=[_ench(51, category=160, power=1), _ench(1482, category=160, power=50)])

    assert [e["id"] for e in detail["enchantments"]] == [1482]


def test_the_panel_drops_spells_from_another_school():
    """`GetEnchantments(MagicSchool.ItemEnchantment)` filters by school before the client sees it.

    Spell 2 is Strength Self I - Creature Enchantment. It can sit in an item's registry and is
    not part of the item's examine text.
    """
    detail = items.build_detail({}, {}, {}, [], ench_item=[_ench(2, category=1, power=1)])

    assert "enchantments" not in detail


# --- B / C. wielder auras that were never merged -------------------------------------------------


def test_mana_conversion_reads_the_wielder_aura_property():
    """Black Breath's Orb stored 0.08 and the game sent 0.128.

    The item's own buff lands on ManaConversionMod; the wielder's Hermetic Link lands on the
    WeaponAuraManaConv twin. Reading only the first returned exactly 1.0, so merging the
    wielder's enchantments looked like it worked and changed nothing.
    """
    aura = [_ench(1, category=1, power=1,
                  type_=enchantments.FLOAT | enchantments.MULTIPLICATIVE | enchantments.SINGLE_STAT,
                  key=enchantments.FLOAT_WEAPON_AURA_MANA_CONV, value=1.6)]

    assert enchantments.mana_conv_mod(aura) == 1.6


def test_attack_mod_reads_both_offense_properties():
    """Adramelech's Flaming Nodachi read 1.14 against the game's 1.31 - Heart Seeker on him."""
    on_item = [_ench(1, category=1, power=1,
                     type_=enchantments.FLOAT | enchantments.ADDITIVE | enchantments.SINGLE_STAT,
                     key=enchantments.FLOAT_WEAPON_OFFENSE, value=0.1)]
    on_wielder = [_ench(2, category=2, power=1,
                        type_=enchantments.FLOAT | enchantments.ADDITIVE | enchantments.SINGLE_STAT,
                        key=enchantments.FLOAT_WEAPON_AURA_OFFENSE, value=0.07)]

    assert round(enchantments.attack_mod(on_item), 6) == 0.1
    assert round(enchantments.attack_mod(on_wielder), 6) == 0.07
    assert round(enchantments.attack_mod(on_item + on_wielder), 6) == 0.17


# --- 160b. ammunition, found by the post-deploy sweep ---------------------------------------------


def _defense_aura(value: float) -> list[dict]:
    return [_ench(1, category=1, power=1,
                  type_=enchantments.FLOAT | enchantments.ADDITIVE | enchantments.SINGLE_STAT,
                  key=enchantments.FLOAT_WEAPON_DEFENSE, value=value)]


def test_ammunition_ignores_the_wielders_defense_aura():
    """Misadventure's Blunt Arrow: the game sent 1, we sent 1.13.

    `AppraiseInfo.cs:415` excludes Ammunition from the WeaponDefense merge outright - not just
    the aura half - so an arrow shows its stored number whatever its wielder is running.

    The arrow stores exactly 1.0, which is neutral, and a neutral modifier gets NO LINE rather
    than "+0%". So the fix is visible here as the absence of the field: before it, the aura pushed
    1.0 to 1.13 and the line appeared.
    """
    arrow = items.build_detail({}, {items.FLOAT_WEAPON_DEFENSE: 1.0}, {}, [],
                               ench=_defense_aura(0.13),
                               weenie_type=items.AMMUNITION_WEENIE_TYPE)

    assert "meleeDefenseMod" not in arrow


def test_ammunition_still_reports_its_own_stored_defense():
    """The exclusion drops the AURA, not the arrow's own number - a non-neutral one still shows."""
    arrow = items.build_detail({}, {items.FLOAT_WEAPON_DEFENSE: 1.05}, {}, [],
                               ench=_defense_aura(0.13),
                               weenie_type=items.AMMUNITION_WEENIE_TYPE)

    assert round(arrow["meleeDefenseMod"], 6) == 1.05


def test_a_non_ammunition_weapon_still_takes_the_aura():
    """The exclusion is specific to ammunition - this is the 158 behaviour, still wanted."""
    sword = items.build_detail({}, {items.FLOAT_WEAPON_DEFENSE: 1.0}, {}, [],
                               ench=_defense_aura(0.13),
                               weenie_type=MELEE_WEAPON)

    assert round(sword["meleeDefenseMod"], 6) == 1.13
