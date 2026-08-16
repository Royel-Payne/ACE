"""Shadowgain 126 — the front-end/back-end boundary, pinned.

    cd shadowgain/web && python -m api.test_contract

WHY THIS FILE EXISTS

124 shipped an API that was verified against the DATABASE in every respect and against the
FRONT-END in none — because the front-end did not exist yet. When it arrived (126) four things
did not line up, and three of them were the API's fault:

  * `lastLogin` was a Unix float; Contract 1 says `lastLogin(ISO)`. `new Date(1786742160)` reads
    a bare number as MILLISECONDS, so every timestamp rendered as January 1970.
  * `quests[].lastCompleted` was a number, and the front-end sorts it with `localeCompare` —
    which a Number does not have. It threw inside `Array.sort`, killing `renderQuests`, which
    killed `mountSheet`. The whole character sheet rendered blank.
  * `quests[].availableAt` was a number, so every cooldown read as "ready".
  * the login body's field name was never in any contract, so the two sides picked differently.

None of that is catchable by a test that only asks "does this match the shard". These cases ask
the other question: "is this the shape the page actually consumes".
"""

from __future__ import annotations

import re

from . import items, payload

ISO = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")


def test_iso_renders_a_js_parseable_utc_string():
    assert ISO.match(payload.iso(1786742160.2778635))
    assert payload.iso(1786742160) == "2026-08-14T21:16:00Z"


def test_iso_treats_absent_and_zero_alike():
    """The shard stores 0 for "never", not NULL, and 0 is not a real timestamp."""
    assert payload.iso(None) is None
    assert payload.iso(0) is None


def test_every_timestamp_the_page_touches_is_a_string():
    """The three fields the front-end feeds to `new Date()` or `localeCompare`.

    A number in any of them is the blank-sheet bug. Guarding the TYPE rather than a value is the
    point: the next timestamp field added here fails this test until it is converted too.
    """
    for value in (1786742160.27, 1786046666, 0, None):
        rendered = payload.iso(value)
        assert rendered is None or isinstance(rendered, str)
        assert not isinstance(rendered, (int, float))


def test_login_body_accepts_either_field_name():
    """Contract 1 never named this field, so both spellings must work.

    The failure this prevents is unusually cruel: pydantic rejects the body with a 422, and the
    front-end's handler renders any non-2xx as "Incorrect account or password" — so a player with
    the RIGHT password is told it is wrong.
    """
    from .app import LoginBody

    assert LoginBody(account="Royel", password="x").name == "Royel"
    assert LoginBody(accountName="Royel", password="x").name == "Royel"
    # Both present: the explicit, contract-shaped one wins.
    assert LoginBody(account="a", accountName="b", password="x").name == "b"
    # Whitespace is trimmed here, not in the handler, so every caller gets it.
    assert LoginBody(account="  Royel  ", password="x").name == "Royel"
    # Neither present is an empty name, which verify_credentials rejects as a 400.
    assert LoginBody(password="x").name == ""


def test_quests_sort_newest_first_on_strings():
    """The sort must work on the ISO strings the payload now emits, not on numbers."""
    rows = [
        {"quest_Name": "Old", "last_Time_Completed": 1786046666, "num_Times_Completed": 1},
        {"quest_Name": "New", "last_Time_Completed": 1786576887, "num_Times_Completed": 1},
        {"quest_Name": "Never", "last_Time_Completed": 0, "num_Times_Completed": 1},
    ]

    built = payload.build_quests({"quests": rows}, now=1786600000)

    assert [q["key"] for q in built] == ["New", "Old", "Never"]

    for q in built:
        assert q["lastCompleted"] is None or ISO.match(q["lastCompleted"])
        assert q["availableAt"] is None or ISO.match(q["availableAt"])


def test_cooldown_becomes_a_future_iso_string():
    """`availableAt` = lastCompleted + minDelta, and null once it has passed."""
    import json
    from pathlib import Path

    quests_file = Path(__file__).parent / "data" / "quests.json"
    table = json.loads(quests_file.read_text(encoding="utf-8"))

    # A real repeatable quest with a real cooldown, read from the generated table.
    key, meta = next(
        (k, v) for k, v in table.items() if v["minDelta"] > 3600 and v["maxSolves"] == -1
    )

    last = 1786046666
    rows = [{"quest_Name": key, "last_Time_Completed": last, "num_Times_Completed": 1}]

    # Still on cooldown -> a future ISO string.
    on_cd = payload.build_quests({"quests": rows}, now=last + 1)[0]
    assert ISO.match(on_cd["availableAt"]), f"{key} should still be on cooldown"

    # Cooldown elapsed -> null, which the front-end renders as "ready".
    ready = payload.build_quests({"quests": rows}, now=last + meta["minDelta"] + 1)[0]
    assert ready["availableAt"] is None


def test_a_vital_never_reports_more_current_than_max():
    """"Health 205/199" is a ratio that cannot exist, and the live page showed it.

    `base` is the UNBUFFED ceiling — all a saved snapshot can compute — while `current_Level` is
    whatever the character had at their last save, buffs included. So any buffed character broke
    the display.
    """
    raw = {
        "attributes": [{"type": 2, "init_Level": 10, "level_From_C_P": 161, "c_P_Spent": 0}],
        # health: base = 0 + 113 + round(171/2) = 199, but saved current is 205 (buffed)
        "vitals": [
            {"type": 1, "init_Level": 0, "level_From_C_P": 113, "c_P_Spent": 0, "current_Level": 205},
            {"type": 3, "init_Level": 0, "level_From_C_P": 147, "c_P_Spent": 0, "current_Level": 247},
        ],
    }

    for v in payload.build_vitals(raw, []):
        assert v["current"] <= v["max"], f"{v['key']}: {v['current']}/{v['max']} is impossible"

    health = next(v for v in payload.build_vitals(raw, []) if v["key"] == "health")

    # The unbuffed figure is still reported, unmodified, for anything that wants the truth.
    assert health["base"] == 199
    assert health["max"] == 205

    # An UNbuffed, damaged character must still show its real ceiling, not its current value.
    stamina = next(v for v in payload.build_vitals(raw, []) if v["key"] == "stamina")
    assert stamina["current"] == 247 and stamina["max"] == 318


def test_attributes_come_out_in_the_clients_panel_order():
    """Coordination is listed ABOVE Quickness in game, but is the HIGHER enum id.

    Sorting by id put two rows in the wrong place for anyone holding the page next to the
    character panel - which is the exact comparison this site invites.
    """
    raw = {
        "attributes": [
            {"type": t, "init_Level": 10, "level_From_C_P": t * 10, "c_P_Spent": 0}
            for t in (1, 2, 3, 4, 5, 6)
        ]
    }

    labels = [a["label"] for a in payload.build_attributes(raw, {
        "attributes_start_at_ten": True, "attribute_max_value": 290}, [])]

    assert labels == ["Strength", "Endurance", "Coordination", "Quickness", "Focus", "Self"], labels


def test_the_public_payload_says_nothing_about_presence():
    """Chris, 2026-08-14: presence belongs on the gated panel, not on a public page.

    `/api/public/character/{name}` needs no login and is linked from the honour roll, so an
    `online` field there publishes "is this player at their keyboard" for every character on the
    server, by name, to anyone. Asserted against the ENDPOINT's source rather than the builder,
    because the leak was a one-line stamp applied after build_public returned - a test of the
    builder alone would have passed while the endpoint leaked.
    """
    import inspect

    from . import app as sgapp

    source = inspect.getsource(sgapp.public_character)

    assert '"online"' not in source, "the public endpoint must not stamp presence"
    assert "online_names()" not in source, "the public endpoint must not read the presence feed"


def test_the_public_payload_still_hides_everything_private():
    """Re-asserted here because 126 changed payload.py, and this is the one mistake in this
    service that could not be walked back."""
    import inspect

    source = inspect.getsource(payload.build_public)

    for forbidden in ("inventory", "location", "quests", "attributes", "vitals", "playtime"):
        assert f'"{forbidden}"' not in source, f"build_public may expose {forbidden}"


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


# --- 136 / 138: the two things that were silently wrong ----------------------------------------


def test_buffs_do_not_stack_within_a_spell_category():
    """The rule the whole buffed-value feature rests on.

    Black Breath carries +15 permanent and +35 timed on Strength, and the client shows +35, not
    +50. Enchantments group by spell category and only the strongest in each applies. Picking the
    top by LayerId instead of PowerLevel happens to give the same answer here, so the second case
    below deliberately makes them disagree.
    """
    from . import enchantments

    same_category = [
        {"category": 100, "power": 2, "start": -10, "type": enchantments.ATTRIBUTE
         | enchantments.ADDITIVE | enchantments.SINGLE_STAT, "key": 1, "value": 15.0},
        {"category": 100, "power": 6, "start": -5, "type": enchantments.ATTRIBUTE
         | enchantments.ADDITIVE | enchantments.SINGLE_STAT, "key": 1, "value": 35.0},
    ]

    assert enchantments.attribute_current(220, same_category, 1) == 255

    # Two DIFFERENT categories do sum.
    two_categories = [
        dict(same_category[0], category=100),
        dict(same_category[1], category=200),
    ]

    assert enchantments.attribute_current(220, two_categories, 1) == 270


def test_an_expired_enchantment_is_ignored_and_a_permanent_one_is_not():
    """StartTime ticks BACKWARDS toward -Duration; a negative Duration never expires."""
    from . import enchantments

    assert not enchantments.is_live(duration=100, start=-100)   # exactly spent
    assert not enchantments.is_live(duration=100, start=-150)   # long gone
    assert enchantments.is_live(duration=100, start=-50)        # still running
    assert enchantments.is_live(duration=-1, start=-99999)      # permanent


def test_wield_requirement_reads_the_requirement_type_not_a_skill():
    """138: these ids were all off by one and the line was confidently wrong.

    A Frost Long Sword on the shard stores WieldRequirements=2 (RawSkill), WieldSkillType=44
    (Heavy Weapons), WieldDifficulty=250. The old constants read 158 as the skill and 159 as the
    level, and rendered "Bow 44".
    """
    detail = items.build_detail({158: 2, 159: 44, 160: 250}, {}, {}, [])

    assert any("Heavy Weapons 250" in line for line in detail["lines"]), detail["lines"]
    assert not any("Bow" in line for line in detail["lines"]), detail["lines"]

    # WieldRequirement.Level (7) ignores the skill field entirely - the difficulty IS the level.
    level_only = items.build_detail({158: 7, 159: 1, 160: 150}, {}, {}, [])

    assert any("Level 150" in line for line in level_only["lines"]), level_only["lines"]


def test_a_weapon_reports_its_damage_and_a_robe_does_not():
    """137: the examine panel was templated off a robe and had no weapon handling at all."""
    weapon = items.build_detail(
        {44: 60, 45: 0x10}, {22: 0.25}, {}, [])          # 60 max damage, 25% variance, Fire

    # 158p: the damage TYPE joins this line, the way the client writes it - "Damage: 26.4 - 44,
    # Bludgeoning" - rather than sitting on a "Damage Type:" row the game does not have.
    assert "Damage: 45 - 60, Fire" in weapon["lines"], weapon["lines"]
    # No "Damage Type:" row any more - it is part of the Damage line above, as in the client.
    assert not any(l.startswith("Damage Type:") for l in weapon["lines"]), weapon["lines"]

    robe = items.build_detail({28: 200}, {}, {}, [])      # armour level only

    assert not any(line.startswith("Damage") for line in robe["lines"]), robe["lines"]
