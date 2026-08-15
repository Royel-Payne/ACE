#!/usr/bin/env python3
"""Shadowgain 152 — diff the web model's appearance against the one the game client is sent.

    In game (Developer):   sg-objdesc Black Breath        -> sg-objdesc-Black_Breath.json
    From the exporter:     sg-datexport ... --objdesc-json exporter.json
    Here:                  python objdesc-diff.py sg-objdesc-Black_Breath.json exporter.json

WHY THIS EXISTS

my.shadowgain.com does not ask the game server what a character looks like — it cannot, because
`CalculateObjDesc` walks EquippedObjects, which only exists for a character loaded in memory, and
the sheet has to work for offline characters too. So the exporter computes the same thing from the
same dats, independently.

"Independently" is the risk. Two implementations of one calculation agree until they quietly do
not, and an appearance bug does not throw — it just renders a slightly wrong person. This turns
that into a test.

WHAT IS NORMALISED, AND WHY IT HAS TO BE

  units       The server carries palette ranges in EIGHTHS of a colour index; the client
              multiplies by 8. The exporter works in absolute indices and divides by 8 on the way
              out. Both sides therefore arrive here in eighths. Comparing raw would flag every
              range and hide the real one.

  part/texture changes
              The server emits an APPEND-ONLY change list, so the same part can appear more than
              once with the last write winning. The exporter keeps only the winner. Both are
              reduced to final state here: what matters is the character that gets drawn, not the
              bookkeeping that got there.

Exit status is 0 when the two agree and 1 when they do not, so this can gate a deploy.
"""

from __future__ import annotations

import json
import sys
from collections import Counter
from pathlib import Path


def _final_parts(entries: list[dict]) -> dict[int, int]:
    """Last write wins, which is how the client applies them."""
    out: dict[int, int] = {}

    for e in entries:
        out[int(e["index"])] = int(e["animationId"])

    return out


def _final_textures(entries: list[dict]) -> dict[tuple[int, int], int]:
    out: dict[tuple[int, int], int] = {}

    for e in entries:
        out[(int(e["partIndex"]), int(e["oldTexture"]))] = int(e["newTexture"])

    return out


PALETTE_TYPE = 0x0400_0000


def _palette_id(raw: int) -> int:
    """Normalise a sub-palette id to its full dat id.

    `CalculateObjDesc` stores clothing sub-palettes as `(ushort)itemPalSet.GetPaletteID(shade)` —
    truncated to 16 bits, because the wire format writes them with `WritePackedDwordOfKnownType(...,
    0x4000000)` and the client puts the type byte back. The base-model palettes (skin, hair, eyes)
    are NOT truncated; they go in as full DIDs.

    So one ObjDesc can carry both forms, and comparing raw makes every dyed garment look wrong while
    the three that matter look right. Anything missing the type prefix gets it back here.
    """
    return raw if raw >= 0x0100_0000 else raw | PALETTE_TYPE


def _palettes(entries: list[dict]) -> Counter:
    """A multiset: the same range applied twice is not the same as applied once."""
    return Counter(
        (_palette_id(int(e["subPaletteId"])), int(e["offset"]), int(e["length"])) for e in entries
    )


def _report(label: str, server, web, fmt) -> int:
    """Print the symmetric difference. Returns the number of disagreements."""
    if isinstance(server, Counter):
        only_server = server - web
        only_web = web - server

        for key, n in sorted(only_server.items()):
            print(f"  {label}: only in GAME  x{n}  {fmt(key)}")

        for key, n in sorted(only_web.items()):
            print(f"  {label}: only in WEB   x{n}  {fmt(key)}")

        return sum(only_server.values()) + sum(only_web.values())

    problems = 0

    for key in sorted(set(server) | set(web)):
        a, b = server.get(key), web.get(key)

        if a == b:
            continue

        problems += 1

        if a is None:
            print(f"  {label}: only in WEB   {fmt(key)} -> 0x{b:08X}")
        elif b is None:
            print(f"  {label}: only in GAME  {fmt(key)} -> 0x{a:08X}")
        else:
            print(f"  {label}: DIFFERS       {fmt(key)}  game=0x{a:08X}  web=0x{b:08X}")

    return problems


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        print(__doc__.strip().splitlines()[0])
        print("\nusage: objdesc-diff.py <sg-objdesc-NAME.json> <exporter.json>")
        return 2

    game = json.loads(Path(argv[1]).read_text(encoding="utf-8"))
    web = json.loads(Path(argv[2]).read_text(encoding="utf-8"))

    print(f"game: {game.get('character', '?')}  setup 0x{int(game.get('setupTableId', 0)):08X}"
          f"  helm={game.get('showHelm')}  cloak={game.get('showCloak')}")
    print(f"web :  setup 0x{int(web.get('setupTableId', 0)):08X}"
          f"  helm={web.get('showHelm')}  cloak={web.get('showCloak')}")
    print()

    problems = 0

    # The inputs first. A setup or an option that differs explains every downstream mismatch, and
    # reporting those first stops a hundred lines of noise from burying the one real cause.
    for field in ("setupTableId", "showHelm", "showCloak"):
        if field in game and field in web and game[field] != web[field]:
            print(f"  INPUT: {field} differs — game={game[field]} web={web[field]}")
            problems += 1

    if int(game.get("paletteId", 0)) != int(web.get("paletteId", 0)):
        print(f"  INPUT: base palette differs — game=0x{int(game['paletteId']):08X} "
              f"web=0x{int(web['paletteId']):08X}")
        problems += 1

    problems += _report(
        "part", _final_parts(game["animPartChanges"]), _final_parts(web["animPartChanges"]),
        lambda k: f"part[{k:>2}]")

    problems += _report(
        "texture", _final_textures(game["textureChanges"]), _final_textures(web["textureChanges"]),
        lambda k: f"part[{k[0]:>2}] old=0x{k[1]:08X}")

    problems += _report(
        "palette", _palettes(game["subPalettes"]), _palettes(web["subPalettes"]),
        lambda k: f"0x{k[0]:08X} over [{k[1] * 8}..{(k[1] + k[2]) * 8})")

    print()

    if problems == 0:
        print("IDENTICAL — the web model is the character the client draws.")
        return 0

    print(f"{problems} disagreement(s).")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
