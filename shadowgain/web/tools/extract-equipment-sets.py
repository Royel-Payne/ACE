#!/usr/bin/env python3
"""Shadowgain 158 — extract the client's equipment-set names into api/data/equipment-sets.json.

    python extract-equipment-sets.py "C:/Games/Turbine/Asheron's Call/acclient.exe"

WHY

The shard stores an EquipmentSetId and nothing else. ACE's enum names it `CloakMeleeDefense`, and
items.py rendered that as "Cloak Melee Defense" - but the CLIENT calls it "Weave of Melee Defense".
Same class of gap as workmanship: the server sends a number, the client supplies the words, and a
name derived from the enum identifier is a plausible-looking invention.

The portal's hand-written table was also wrong in smaller ways - "Carraidas Benediction" for the
client's "Carraida's Benediction".

HOW

The names are one contiguous null-padded run in acclient.exe, stored HIGHEST ID FIRST, exactly like
the workmanship adjectives. The run is found by ANCHOR - the lowest-numbered entry - and read
backwards, never by a hard-coded address.

`Set: ` sits inside the address range but is the LABEL, not a member, so it is skipped explicitly
rather than shifting every id after it by one. That is the same trap that made the material table
off-by-one: an intruder in the middle of a list nobody re-counted.

The extraction ASSERTS two known ids before writing - 4 and 71, from the client itself - so a
patched client or a misread run fails loudly instead of producing a plausible wrong table.
"""

from __future__ import annotations

import json
import mmap
import re
import sys
from pathlib import Path

# Two points the client itself gives us, used as a self-check rather than as data.
EXPECTED = {4: "Carraida's Benediction", 71: "Weave of Melee Defense"}

ENUM = Path(__file__).resolve().parents[3] / "Source" / "ACE.Entity" / "Enum" / "EquipmentSet.cs"
DATA_DIR = Path(__file__).resolve().parent.parent / "api" / "data"


def _spaced(name: str) -> str:
    return re.sub(r"(?<!^)(?=[A-Z])", " ", name)


def _client_strings(exe: Path) -> set[str]:
    """Every printable string in the binary. Used to CONFIRM a name, never to order one."""
    with open(exe, "rb") as fh:
        mm = mmap.mmap(fh.fileno(), 0, access=mmap.ACCESS_READ)

        try:
            return {m.group().decode("latin-1") for m in re.finditer(rb"[ -~]{3,}", mm[:])}
        finally:
            mm.close()


def extract(exe: Path) -> tuple[dict[int, str], list[str]]:
    """id -> the client's name, matched by MEANING rather than by position.

    The first attempt read the names as one contiguous run indexed from the lowest id, the way the
    workmanship adjectives are. That is wrong here and the self-check caught it: id 71 came out as
    "Weave of Light Weapons". ACE's enum runs 0-140 with no gaps, but the binary only carries names
    for the sets that HAVE one, so position in memory is not position in the enum.

    So each enum entry proposes the name it would have - `CloakMeleeDefense` -> "Weave of Melee
    Defense", `NobleRelic` -> "Noble Relic" - and the proposal is kept ONLY if that exact string is
    present in the client. A name that cannot be confirmed is reported, not written.
    """
    strings = _client_strings(exe)
    src = ENUM.read_text(encoding="utf-8")

    table: dict[int, str] = {}
    unconfirmed: list[str] = []

    for m in re.finditer(r"^\s+(\w+)\s*=\s*(\d+)", src, re.M):
        name, set_id = m.group(1), int(m.group(2))

        if name in ("Undef", "None"):
            continue

        # Cloaks are "Weave of X" in the client; everything else is its identifier, spaced.
        candidates = [f"Weave of {_spaced(name[5:])}" if name.startswith("Cloak") else _spaced(name)]

        # A few carry an apostrophe a C# identifier cannot: Crafter's, Carraida's Benediction.
        # Tried only when the plain form fails, so genuine plurals ("Empyrean Rings") match first.
        candidates.append(re.sub(r"(\w)s\b", r"\1's", candidates[0]))

        for c in candidates:
            if c in strings:
                table[set_id] = c
                break
        else:
            unconfirmed.append(f"{set_id} {name}")

    return table, unconfirmed


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print(__doc__.strip().splitlines()[0])
        print("\nusage: extract-equipment-sets.py <path to acclient.exe>")
        return 2

    exe = Path(argv[1])

    if not exe.is_file():
        print(f"!! not a file: {exe}")
        return 1

    table, unconfirmed = extract(exe)

    for want_id, want_name in EXPECTED.items():
        got = table.get(want_id)

        if got != want_name:
            print(f"!! id {want_id} came out as {got!r}, expected {want_name!r}")
            print("   The run was misread or the client changed. Refusing to write.")
            return 1

    out = DATA_DIR / "equipment-sets.json"
    out.write_text(json.dumps({str(k): v for k, v in sorted(table.items())}, indent=2) + "\n",
                   encoding="utf-8")

    print(f"wrote {out}  ({len(table)} sets confirmed against the client)")
    print(f"   4: {table[4]}")
    print(f"  71: {table[71]}")

    if unconfirmed:
        # Reported rather than invented. These keep whatever items.py already had.
        print(f"  {len(unconfirmed)} set(s) have no confirmable client name: "
              + ", ".join(unconfirmed[:8]) + ("..." if len(unconfirmed) > 8 else ""))

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
