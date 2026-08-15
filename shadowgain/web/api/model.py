"""Shadowgain 130 Stage 3 — the character's 3D model, assembled on demand.

WHY THIS RUNS SERVER-SIDE RATHER THAN BEING PRE-BAKED

Gear combinations are combinatorial: there is no set of models to generate ahead of time. So the
model is assembled per character, from the dats the droplet already has at `/opt/ACE/Dats`, by a
self-contained binary built from `shadowgain/exporter`. The browser renders the result; nothing
here rasterises anything, which is exactly why the 3D route was chosen over a static portrait
(Task.md 130).

CACHED BY APPEARANCE, NOT BY CLOCK

The rest of the sheet uses a 5-minute TTL because a character's numbers change constantly. A
character's *appearance* does not — it changes when they swap gear, and not otherwise. So the key
is a hash of everything that feeds the model: heritage, gender, the three palettes, the head, and
every worn item's clothing base, dye template and shade. Same signature, same bytes, no rebuild.

That also answers the question behind Part 2: the model updates when gear changes because the
signature changes, not because anything watches for it.
"""

from __future__ import annotations

import hashlib
import os
import subprocess
import tempfile
from pathlib import Path

from . import db

# Built by `dotnet publish -r linux-x64 --self-contained` from shadowgain/exporter and shipped by
# web-deploy.sh. Self-contained on purpose: `dotnet` exists inside the ACE container but not on
# the host, and this service has no business running anything in the game's container.
EXPORTER = os.environ.get("SG_WEB_EXPORTER", "/opt/shadowgain-web/bin/sg-datexport")
DAT_DIR = os.environ.get("SG_WEB_DAT_DIR", "/opt/ACE/Dats")

CACHE_DIR = Path(os.environ.get("SG_WEB_MODEL_CACHE", "/opt/shadowgain-web/models"))

# A build takes a couple of seconds; anything past this is a hung process, not a slow one.
BUILD_TIMEOUT = 60

# --- property ids -----------------------------------------------------------------------------

INT_GENDER = 113
INT_HERITAGE = 188
INT_PALETTE_TEMPLATE = 3
INT_CLOTHING_PRIORITY = 4
INT_CURRENT_WIELDED_LOCATION = 10
INT_ITEM_TYPE = 1

FLOAT_SHADE = 12

DID_CLOTHING_BASE = 7
DID_HAIR_PALETTE = 15
DID_EYES_PALETTE = 16
DID_SKIN_PALETTE = 17
DID_HEAD_OBJECT = 18

# Only things that can cover the model contribute. ACE's own filter is
# `CurrentWieldedLocation & (Clothing | Armor | Cloak)`; a wand or a ring changes nothing visible
# on the body, so including them would just be work.
EQUIP_CLOTHING = 0x8000_0000 | 0x0000_01FF   # Clothing composite
EQUIP_ARMOR = 0x0000_7F00
EQUIP_CLOAK = 0x0800_0000

COVERS_MODEL = EQUIP_CLOTHING | EQUIP_ARMOR | EQUIP_CLOAK


def _appearance(cur, character_id: int) -> dict | None:
    """Everything the exporter needs, in one place."""
    ints = {
        r["type"]: r["v"]
        for r in db.fetch_all(
            cur, "SELECT type, value AS v FROM biota_properties_int WHERE object_Id = %s",
            (character_id,))
    }

    dids = {
        r["type"]: r["v"]
        for r in db.fetch_all(
            cur, "SELECT type, value AS v FROM biota_properties_d_i_d WHERE object_Id = %s",
            (character_id,))
    }

    heritage = ints.get(INT_HERITAGE)
    gender = ints.get(INT_GENDER)

    if not heritage or not gender:
        return None

    items = db.fetch_all(
        cur,
        """
        SELECT b.id,
               cb.value    AS clothing_base,
               tmpl.value  AS palette_template,
               shade.value AS shade,
               prio.value  AS priority,
               loc.value   AS wielded
        FROM biota b
        JOIN biota_properties_i_i_d wielder
          ON wielder.object_Id = b.id AND wielder.type = 3 AND wielder.value = %s
        JOIN biota_properties_d_i_d cb
          ON cb.object_Id = b.id AND cb.type = %s
        LEFT JOIN biota_properties_int tmpl  ON tmpl.object_Id = b.id  AND tmpl.type = %s
        LEFT JOIN biota_properties_float shade ON shade.object_Id = b.id AND shade.type = %s
        LEFT JOIN biota_properties_int prio  ON prio.object_Id = b.id  AND prio.type = %s
        LEFT JOIN biota_properties_int loc   ON loc.object_Id = b.id   AND loc.type = %s
        """,
        (character_id, DID_CLOTHING_BASE, INT_PALETTE_TEMPLATE, FLOAT_SHADE,
         INT_CLOTHING_PRIORITY, INT_CURRENT_WIELDED_LOCATION),
    )

    worn = []

    for row in items:
        wielded = int(row["wielded"] or 0)

        if not (wielded & COVERS_MODEL):
            continue

        worn.append({
            "base": int(row["clothing_base"]),
            "template": int(row["palette_template"] or 0),
            "shade": float(row["shade"] or 0),
            "priority": int(row["priority"] or 0),
        })

    return {
        "heritage": int(heritage),
        "gender": int(gender),
        "skin": int(dids.get(DID_SKIN_PALETTE) or 0),
        "hair": int(dids.get(DID_HAIR_PALETTE) or 0),
        "eyes": int(dids.get(DID_EYES_PALETTE) or 0),
        "head": int(dids.get(DID_HEAD_OBJECT) or 0),
        "items": sorted(worn, key=lambda i: (i["priority"], i["base"])),
    }


def _signature(appearance: dict) -> str:
    """A stable hash of everything that changes how the character LOOKS."""
    parts = [
        appearance["heritage"], appearance["gender"],
        appearance["skin"], appearance["hair"], appearance["eyes"], appearance["head"],
    ]

    for item in appearance["items"]:
        parts += [item["base"], item["template"], round(item["shade"], 4), item["priority"]]

    return hashlib.sha256("|".join(str(p) for p in parts).encode()).hexdigest()[:16]


def _build(appearance: dict, out_dir: Path) -> None:
    args = [
        EXPORTER,
        "--dat", DAT_DIR,
        "--out", str(out_dir),
        "--heritage", str(appearance["heritage"]),
        "--gender", str(appearance["gender"]),
    ]

    for flag, key in (("--skin", "skin"), ("--hair", "hair"), ("--eyes", "eyes"), ("--head", "head")):
        if appearance[key]:
            args += [flag, str(appearance[key])]

    for item in appearance["items"]:
        args += ["--item", f"{item['base']}:{item['template']}:{item['shade']}:{item['priority']}"]

    result = subprocess.run(args, capture_output=True, timeout=BUILD_TIMEOUT, text=True)

    if result.returncode != 0:
        raise RuntimeError(f"exporter failed ({result.returncode}): {result.stderr[-400:]}")


def model_path(character_id: int) -> tuple[Path, str] | None:
    """Return `(path, signature)` for this character's .glb, building it if needed.

    Returns None when the character has no usable appearance data, which is a real state for a
    biota that is not a player.
    """
    with db.shard() as cur:
        appearance = _appearance(cur, character_id)

    if appearance is None:
        return None

    signature = _signature(appearance)

    CACHE_DIR.mkdir(parents=True, exist_ok=True)

    cached = CACHE_DIR / f"{signature}.glb"

    if cached.exists() and cached.stat().st_size > 0:
        return cached, signature

    # Built into a temp directory and moved into place, so a reader can never open a half-written
    # file and a failed build leaves no empty cache entry behind to be served forever.
    with tempfile.TemporaryDirectory(dir=CACHE_DIR) as tmp:
        _build(appearance, Path(tmp))

        produced = Path(tmp) / "character.glb"

        if not produced.exists():
            raise RuntimeError("exporter produced no model")

        produced.replace(cached)

    return cached, signature
