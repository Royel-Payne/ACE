"""Shadowgain 124 — the rank maths, pinned against production.

    cd shadowgain/web && python -m api.test_curves

WHAT THIS IS FOR

`curves.py` is a PORT of the server's rank functions (see its module docstring). A port is only
as good as its agreement with the original, and the original lives in another language in another
repo — so nothing in the type system or the test suite will notice if the two drift apart.

These cases are the agreement, frozen. Every skill and attribute figure below was read off LIVE's
`ace_shard` for Black Breath on 2026-08-14: the shard stores both the experience AND the rank the
server derived from it, so a stored rank is the server's own answer to the question this module
re-asks. All 14 skills and 6 attributes matched exactly on the day the port was written.

If one of these ever fails, the server's maths has moved and curves.py has to move with it. That
is the intended signal, not a flaky test.
"""

from __future__ import annotations

from . import curves

# The dials as LIVE actually has them (config_properties_*), not as the C# defaults have them.
# PropertyManager loads these rows OVER its compiled defaults, so the source is only a fallback.
LIVE_ATTRIBUTES_START_AT_TEN = True
LIVE_ATTRIBUTE_MAX_VALUE = 290

# (skillId, sac, storedRanks, pp) from ace_shard.biota_properties_skill, Black Breath 1342177293.
# `storedRanks` is level_From_P_P — what the SERVER computed from that same pp.
LIVE_SKILLS = [
    (6, 3, 190, 140954890),      # Melee Defense, specialized
    (41, 3, 183, 75114784),      # Two Handed Combat, specialized
    (54, 3, 175, 39084171),      # Summoning, specialized
    (36, 2, 192, 1318933812),    # Loyalty, trained
    (50, 2, 151, 57783397),      # Recklessness
    (16, 2, 141, 26607242),      # Mana Conversion
    (40, 2, 140, 25750502),      # Salvaging
    (27, 2, 138, 21411386),      # Assess Creature
    (52, 2, 131, 12620863),      # Dirty Fighting
    (20, 2, 130, 12399459),      # Deception
    (14, 2, 129, 10746333),      # Arcane Lore
    (31, 2, 125, 8361690),       # Creature Enchantment
    (7, 2, 36, 21912),           # Missile Defense
    (22, 2, 24, 9506),           # Jump
]

# (attributeId, storedRanks, cpSpent) from ace_shard.biota_properties_attribute.
LIVE_ATTRIBUTES = [
    (1, 210, 91403575),   # Strength
    (2, 161, 6823418),    # Endurance
    (3, 148, 3544027),    # Quickness   (3 is Quickness, NOT Coordination - client order)
    (4, 187, 27048698),   # Coordination
    (5, 198, 49251041),   # Focus
    (6, 177, 15660231),   # Self
]

# Frozen (093) skills: below Trained, XP held. The rank shown is what the held XP buys BACK on
# the trained table, which must equal the rank the shard still stores for them.
LIVE_PRUNED = [
    (44, 60, 95059),      # Heavy Weapons
    (46, 111, 2906843),   # Finesse Weapons
    (45, 0, 0),           # Light Weapons - never trained
]


def test_skill_ranks_match_the_server():
    for skill_id, sac, stored, pp in LIVE_SKILLS:
        xp = curves.true_experience_spent(pp, None)
        rank, _, _ = curves.skill_progress(sac, xp)

        assert rank == stored, f"skill {skill_id}: computed {rank}, shard says {stored}"


def test_attribute_ranks_match_the_server():
    for attr_id, stored, cp in LIVE_ATTRIBUTES:
        rank, _, _, _ = curves.attribute_progress(
            cp, LIVE_ATTRIBUTES_START_AT_TEN, LIVE_ATTRIBUTE_MAX_VALUE
        )

        assert rank == stored, f"attribute {attr_id}: computed {rank}, shard says {stored}"


def test_pruned_skills_report_the_rank_their_held_xp_buys_back():
    """093: pruning freezes rank and XP rather than destroying them."""
    for skill_id, stored, pp in LIVE_PRUNED:
        rank = curves.calc_skill_rank_uncapped(curves.SAC_TRAINED, pp)

        assert rank == stored, f"pruned skill {skill_id}: computed {rank}, shard says {stored}"


def test_the_dat_tables_are_the_ones_the_server_reads():
    """Shape assertions against figures quoted in the server's own comments.

    These are the numbers Player_Skills and Player_Attributes cite in prose. If the exported
    tables ever came from a different dat, this is what would catch it.
    """
    t = curves.tables()

    assert len(t.attribute) == 191, "attribute table should define 190 ranks (0..190)"
    assert len(t.trained_skill) == 209, "trained table tops out at rank 208"
    assert len(t.specialized_skill) == 227, "specialized table tops out at rank 226"
    assert len(t.level) == 276, "levels run to 275"

    # "the trained table's own final step is 306,860,483" - Player_Skills.GetOvercapCurve
    assert t.trained_skill[-1] - t.trained_skill[-2] == 306_860_483

    # "keeps the total cost of a maxed attribute exactly what retail charges (4,019,438,644)"
    assert t.attribute[-1] == 4_019_438_644


def test_the_overcap_curve_is_the_tables_own_shape():
    """109b: past the table, cost continues at the table's own ratio - no seam, nothing to tune."""
    last_step, ratio = curves.overcap_curve(curves.SAC_TRAINED)

    assert last_step == 306_860_483.0
    # "the trained table is geometric to six decimal places, so any window gives 1.078750"
    # (measured 1.07874888..., so the source comment is rounding to 6dp loosely - 1e-5 is tight
    # enough to catch a table swap and loose enough not to fail on that rounding.)
    assert abs(ratio - 1.078750) < 0.00001

    _, spec_ratio = curves.overcap_curve(curves.SAC_SPECIALIZED)
    # "the specialized table's tail is noisier ... ~1.0866"
    assert abs(spec_ratio - 1.0866) < 0.001


def test_rank_keeps_climbing_past_the_table_and_gets_dearer():
    """The bug 005 shipped and 109b fixed: rank 209 must cost MORE than 208, not 300x less."""
    table = curves.tables().trained_skill
    top_rank = len(table) - 1
    top_xp = table[top_rank]

    assert curves.calc_skill_rank_uncapped(curves.SAC_TRAINED, top_xp) == top_rank

    cost_208 = table[top_rank] - table[top_rank - 1]
    cost_209 = curves.calc_skill_xp_for_rank(curves.SAC_TRAINED, top_rank + 1) - top_xp

    assert cost_209 > cost_208, "the first rank past the table got CHEAPER - that was the 005 bug"

    # And it keeps climbing, rather than hitting a second wall.
    assert curves.calc_skill_rank_uncapped(curves.SAC_TRAINED, top_xp * 10) > top_rank + 20


def test_forward_and_inverse_agree_exactly():
    """109 replaced a closed-form inverse with a binary search because the two disagreed on
    1,741 of the first 5,000 ranks past the wall - showing "0 more needed" for a rank that had
    not ticked over. They must round-trip."""
    for rank in (100, 208, 209, 250, 400, 900):
        xp = curves.calc_skill_xp_for_rank(curves.SAC_TRAINED, rank)

        if xp is None:
            continue

        assert curves.calc_skill_rank_uncapped(curves.SAC_TRAINED, xp) == rank, f"rank {rank}"
        # One less experience must NOT be enough.
        assert curves.calc_skill_rank_uncapped(curves.SAC_TRAINED, xp - 1) == rank - 1, f"rank {rank}"


def test_attribute_ceiling_is_280_ranks_not_the_dat_table_190():
    """013 stretches the 190-entry dat table across 280 ranks so a start-10 attribute can reach
    attribute_max_value. 104: reading the ceiling off the TABLE reported "max" 90 ranks early."""
    max_ranks = curves.attribute_max_ranks(True, 290)

    assert max_ranks == 280

    # Monotonic: every rank must cost more than the one before, or the curve inverts somewhere.
    previous = -1

    for rank in range(1, max_ranks + 1):
        cost = curves.attribute_rank_cost(rank, max_ranks)
        assert cost > previous, f"attribute rank {rank} costs no more than {rank - 1}"
        previous = cost

    # And a maxed attribute costs exactly what retail charges.
    assert curves.attribute_rank_cost(max_ranks, max_ranks) == 4_019_438_644


def test_vital_formulas_come_from_the_dat():
    """004: vitals follow their governing attribute rather than earning separately."""
    formulas = curves.vital_formulas()

    assert formulas["maxHealth"]["attr1"] == "Endurance"
    assert formulas["maxHealth"]["divisor"] == 2        # health is Endurance / 2
    assert formulas["maxStamina"]["attr1"] == "Endurance"
    assert formulas["maxStamina"]["divisor"] == 1
    assert formulas["maxMana"]["attr1"] == "Self"

    # Black Breath: Endurance base 171 -> health contribution round(85.5) = 86 AWAY FROM ZERO.
    # Python's own round() is banker's and would give 86 here by luck but 84 for 84.5 against
    # ACE's 85 - which is why _round_away exists.
    assert curves.apply_formula(formulas["maxHealth"], {2: 171}) == 86
    assert curves.apply_formula(formulas["maxHealth"], {2: 169}) == 85     # 84.5 -> 85, not 84


def test_enum_labels_survive_an_unknown_id():
    """A heritage or title added upstream must not 500 the whole page."""
    assert curves.enum_label("heritage", 10) == "Penumbraen"
    assert curves.enum_label("gender", 2) == "Female"
    assert curves.enum_label("playerKillerStatus", 4) == "PK"
    assert curves.enum_label("heritage", 9999) == "heritage 9999"
    assert curves.enum_label("heritage", None) is None


if __name__ == "__main__":
    passed = failed = 0

    for name, fn in sorted(globals().items()):
        if not name.startswith("test_") or not callable(fn):
            continue

        try:
            fn()
            print(f"  PASS  {name}")
            passed += 1
        except Exception as ex:  # noqa: BLE001
            print(f"  FAIL  {name}: {ex}")
            failed += 1

    print(f"\n{passed} passed, {failed} failed")

    raise SystemExit(1 if failed else 0)
