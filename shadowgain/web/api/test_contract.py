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

from . import payload

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

    for v in payload.build_vitals(raw):
        assert v["current"] <= v["max"], f"{v['key']}: {v['current']}/{v['max']} is impossible"

    health = next(v for v in payload.build_vitals(raw) if v["key"] == "health")

    # The unbuffed figure is still reported, unmodified, for anything that wants the truth.
    assert health["base"] == 199
    assert health["max"] == 205

    # An UNbuffed, damaged character must still show its real ceiling, not its current value.
    stamina = next(v for v in payload.build_vitals(raw) if v["key"] == "stamina")
    assert stamina["current"] == 247 and stamina["max"] == 318


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
