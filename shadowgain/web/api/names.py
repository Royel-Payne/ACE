"""Shadowgain 124 — turning a raw shard position into somewhere a player recognises.

The shard stores `obj_Cell_Id` (a packed landblock + cell) and three floats. That is precise and
completely unreadable. The banner wants "Holtburg · 42.0N, 33.5E".

Two halves, from two different places:

  * COORDINATES are computed, exactly as the server computes them for `@loc` —
    PositionExtensions.ToGlobal + GetMapCoords + GetMapCoordStr. There is nothing to look up and
    no table to go stale.
  * PLACE NAMES are looked up in data/landblocks.json, built by tools/build-name-tables.sh from
    ace_world (points_of_interest first, portal destinations second). Coverage is 1,742 of the
    ~2,300 landblocks that have anything in them, so a miss is normal and expected — which is why
    the coordinate half never depends on the name half.

The two are computed independently and the caller gets whichever exist. A dungeon has a name and
no coordinates (the client itself refuses to give map coordinates indoors); an empty hillside has
coordinates and no name. Neither case is an error.
"""

from __future__ import annotations

import json
from functools import lru_cache
from pathlib import Path

DATA_DIR = Path(__file__).parent / "data"

# Position.BlockLength — one landblock is 192 metres on a side.
BLOCK_LENGTH = 192

# GetMapCoords: 1 map unit = 240 metres, and Dereth runs -102..+102 across.
MAP_UNIT_METRES = 240
MAP_HALF_WIDTH = 102


@lru_cache(maxsize=1)
def landblocks() -> dict[str, dict]:
    path = DATA_DIR / "landblocks.json"

    if not path.exists():
        # A missing table degrades to coordinates everywhere rather than taking the site down.
        # Regenerate with tools/build-name-tables.sh.
        return {}

    return json.loads(path.read_text(encoding="utf-8"))


@lru_cache(maxsize=1)
def quests() -> dict[str, dict]:
    path = DATA_DIR / "quests.json"

    if not path.exists():
        return {}

    return json.loads(path.read_text(encoding="utf-8"))


def is_indoors(obj_cell_id: int) -> bool:
    """LandblockId.Indoors — `(Raw & 0xFFFF) >= 0x100`.

    Below 0x100 the cell is an outdoor terrain cell; at or above it, an interior cell of a
    building or dungeon. This is the same test the server uses to decide whether map coordinates
    exist at all.
    """
    return (obj_cell_id & 0xFFFF) >= 0x100


def landblock_hex(obj_cell_id: int) -> str:
    """The 4-hex-digit landblock, which is how landblocks.json is keyed."""
    return f"{(obj_cell_id >> 16) & 0xFFFF:04X}"


def map_coords(obj_cell_id: int, origin_x: float, origin_y: float) -> tuple[float, float] | None:
    """(east/west, north/south) in map units, or None indoors.

    PositionExtensions.GetMapCoords returns null indoors and so does this — the client has no map
    coordinate for a dungeon cell, and inventing one would put a player somewhere they are not.
    """
    if is_indoors(obj_cell_id):
        return None

    landblock_x = (obj_cell_id >> 24) & 0xFF
    landblock_y = (obj_cell_id >> 16) & 0xFF

    global_x = landblock_x * BLOCK_LENGTH + (origin_x or 0.0)
    global_y = landblock_y * BLOCK_LENGTH + (origin_y or 0.0)

    return (
        global_x / MAP_UNIT_METRES - MAP_HALF_WIDTH,
        global_y / MAP_UNIT_METRES - MAP_HALF_WIDTH,
    )


def coord_string(obj_cell_id: int, origin_x: float, origin_y: float) -> str | None:
    """"42.0N, 33.5E" — byte for byte what GetMapCoordStr produces.

    The `- 0.05` is not a rounding tweak of ours: it is in the server's own formatter, and
    dropping it would shift every coordinate on the site half a tenth away from what the same
    player reads off `@loc` in game.
    """
    coords = map_coords(obj_cell_id, origin_x, origin_y)

    if coords is None:
        return None

    east_west, north_south = coords

    ns = "N" if north_south >= 0 else "S"
    ew = "E" if east_west >= 0 else "W"

    return f"{abs(north_south) - 0.05:.1f}{ns}, {abs(east_west) - 0.05:.1f}{ew}"


def describe_position(obj_cell_id: int, origin_x: float, origin_y: float) -> dict:
    """The `location` object in the contract: `{place, coords}`, plus what the front-end can use.

    `place` falls back through name -> "Underground" -> "Dereth" so the banner always has a word
    in it. "Underground" rather than "Unknown" for a nameless dungeon: it says the one true thing
    the cell id guarantees, and reads as information instead of as a failure.
    """
    indoors = is_indoors(obj_cell_id)

    entry = landblocks().get(landblock_hex(obj_cell_id))

    if entry:
        place = entry["name"]
    elif indoors:
        place = "Underground"
    else:
        place = "Dereth"

    return {
        "place": place,
        "coords": coord_string(obj_cell_id, origin_x, origin_y),
        "landblock": landblock_hex(obj_cell_id),
        "indoors": indoors,
        # True when the name came from the table rather than from the fallback, so the front-end
        # can style a real place name differently from a shrug if it wants to.
        "named": bool(entry),
    }
