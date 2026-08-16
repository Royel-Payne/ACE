#!/usr/bin/env python3
"""Shadowgain 158 — extract the client's workmanship adjectives into api/data/workmanship.json.

    python extract-workmanship.py "C:/Games/Turbine/Asheron's Call/acclient.exe"

WHY THIS EXISTS AT ALL

The shard stores workmanship as a bare number. The word beside it - "Incomparable (9)" - is
produced entirely by the CLIENT, so unlike almost everything else the portal shows, there is no
server-side source for it:

  * not in ACE source     grepped; the server sends the int and nothing else
  * not in the dats       probed every string table in client_local_English.dat with
                          `sg-datexport --find-string`, which DOES find character titles, so the
                          probe works and the answer is genuinely no
  * in acclient.exe       a contiguous null-padded run of ten strings

items.py previously carried a hand-typed table that said 9 was "Superb" and 6 was "Fine". Both are
wrong. That is the same failure as the material table in 158a, and the reason this is a generator
and not a corrected literal: transcribing a list by hand is precisely what put the wrong list
there.

HOW THE RUN IS FOUND

By ANCHOR, never by offset. `Priceless` is searched for and the following strings are read in
order; a hard-coded address would be wrong the moment the client is patched, and would fail
silently by reading whatever else lives there. The extraction asserts it found exactly the ten
expected entries, so a layout change stops this rather than writing a plausible wrong table.

The run is stored HIGHEST FIRST, so it is reversed on the way out: index 10 is Priceless, 1 is
Poorly crafted.

The bottom four are PREFIXES - they carry a trailing space or hyphen ("Exquisitely ", "Well-")
because the client appends "crafted". Anything ending in a space or hyphen therefore gets
"crafted" appended, which is the rule the data itself states rather than one imposed on it.

VERIFIED against two in-game screenshots: workmanship 9 renders "Incomparable", 6 renders
"Nearly flawless". Both match.
"""

from __future__ import annotations

import json
import mmap
import re
import sys
from pathlib import Path

ANCHOR = b"Priceless"

# Highest first, exactly as the binary stores them. Used as an assertion, not as the output: if the
# client is patched and this run changes, the extraction must FAIL rather than emit a wrong table.
EXPECTED = [
    "Priceless",
    "Incomparable",
    "Utterly flawless",
    "Flawless",
    "Nearly flawless",
    "Magnificent",
    "Exquisitely ",
    "Finely ",
    "Well-",
    "Poorly ",
]

DATA_DIR = Path(__file__).resolve().parent.parent / "api" / "data"


def extract(exe: Path) -> list[str]:
    with open(exe, "rb") as fh:
        mm = mmap.mmap(fh.fileno(), 0, access=mmap.ACCESS_READ)

        try:
            at = mm.find(ANCHOR)

            if at == -1:
                raise SystemExit(f"!! {ANCHOR.decode()!r} not found in {exe} - wrong file?")

            # Generous window: the run is ~120 bytes, and over-reading is harmless because the
            # result is checked against EXPECTED rather than trusted by length.
            window = mm[at:at + 256]
        finally:
            mm.close()

    found = [p.decode("latin-1") for p in window.split(b"\x00") if p]

    return found[:len(EXPECTED)]


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print(__doc__.strip().splitlines()[0])
        print("\nusage: extract-workmanship.py <path to acclient.exe>")
        return 2

    exe = Path(argv[1])

    if not exe.is_file():
        print(f"!! not a file: {exe}")
        return 1

    found = extract(exe)

    if found != EXPECTED:
        print("!! the run in acclient.exe is not the one this script was written against.")
        print(f"   expected: {EXPECTED}")
        print(f"   found   : {found}")
        print("   Refusing to write a table that may be wrong. Re-derive it before changing this.")
        return 1

    # Reverse: the binary is highest-first, workmanship counts up from 1.
    ascending = list(reversed(found))

    table = {}

    for i, word in enumerate(ascending, start=1):
        # A trailing space or hyphen means the client appends "crafted" - "Well-" -> "Well-crafted".
        table[str(i)] = f"{word}crafted" if re.search(r"[ -]$", word) else word

    out = DATA_DIR / "workmanship.json"
    out.write_text(json.dumps(table, indent=2) + "\n", encoding="utf-8")

    print(f"wrote {out}")

    for k, v in table.items():
        print(f"  {k:>2} -> {v}")

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
