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
import json
import os
import subprocess
import tempfile
from pathlib import Path

from . import db

DATA_DIR = Path(__file__).parent / "data"

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

BOOL_TOP_LAYER_PRIORITY = 123

DID_SETUP = 1
DID_CLOTHING_BASE = 7
DID_HAIR_PALETTE = 15
DID_EYES_PALETTE = 16
DID_SKIN_PALETTE = 17
DID_HEAD_OBJECT = 18

# The face. Each pair is an `old -> new` texture swap on the head part, which is how AC recolours
# and reshapes eyes, nose and mouth without a different head model.
DID_EYES_TEXTURE = 9
DID_NOSE_TEXTURE = 10
DID_MOUTH_TEXTURE = 11
DID_DEFAULT_EYES_TEXTURE = 12
DID_DEFAULT_NOSE_TEXTURE = 13
DID_DEFAULT_MOUTH_TEXTURE = 14

# CharacterOptions2 bits. Both live in the second word, and the shard column is
# `character.character_Options_2`.
OPT2_SHOW_HELM = 0x0010_0000
OPT2_SHOW_CLOAK = 0x0080_0000


def _equip_masks() -> dict[str, int]:
    """The EquipMask composites, READ FROM THE GENERATED ENUM rather than typed here.

    These were previously three hand-written hex literals. They happened to be right, but a
    hand-mapped id is exactly the thing that has silently broken this project before, and the
    exporter already emits the real enum to `data/enums.json` — so there is no reason to keep a
    second copy that can rot.
    """
    raw = json.loads((DATA_DIR / "enums.json").read_text(encoding="utf-8"))["equipMask"]

    # enums.json is value -> {name, label}; the composites are wanted by name.
    by_name = {entry["name"]: int(value) for value, entry in raw.items()}

    return by_name


_MASKS = _equip_masks()

EQUIP_CLOTHING = _MASKS["Clothing"]
EQUIP_ARMOR = _MASKS["Armor"]
EQUIP_CLOAK = _MASKS["Cloak"]
EQUIP_EXTREMITY = _MASKS["Extremity"]

# Only things that can cover the model contribute. ACE's own filter is
# `CurrentWieldedLocation & (Clothing | Armor | Cloak)`; a wand or a ring changes nothing visible
# on the body, so including them would just be work.
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

    # LEFT JOIN on the clothing base, not an inner one. An item without a ClothingBase still
    # changes the model — ACE falls back to using the item's own Setup as a pseudo clothing base
    # (Ursuin Guise, WCID 32155, is the stock example). An inner join dropped those silently, so
    # the player wore something the portrait did not.
    items = db.fetch_all(
        cur,
        """
        SELECT b.id,
               cb.value    AS clothing_base,
               setup.value AS setup,
               tmpl.value  AS palette_template,
               shade.value AS shade,
               prio.value  AS priority,
               loc.value   AS wielded,
               itype.value AS item_type,
               toplayer.value AS top_layer
        FROM biota b
        JOIN biota_properties_i_i_d wielder
          ON wielder.object_Id = b.id AND wielder.type = 3 AND wielder.value = %s
        LEFT JOIN biota_properties_d_i_d cb    ON cb.object_Id = b.id    AND cb.type = %s
        LEFT JOIN biota_properties_d_i_d setup ON setup.object_Id = b.id AND setup.type = %s
        LEFT JOIN biota_properties_int tmpl  ON tmpl.object_Id = b.id  AND tmpl.type = %s
        LEFT JOIN biota_properties_float shade ON shade.object_Id = b.id AND shade.type = %s
        LEFT JOIN biota_properties_int prio  ON prio.object_Id = b.id  AND prio.type = %s
        LEFT JOIN biota_properties_int loc   ON loc.object_Id = b.id   AND loc.type = %s
        LEFT JOIN biota_properties_int itype ON itype.object_Id = b.id AND itype.type = %s
        LEFT JOIN biota_properties_bool toplayer
          ON toplayer.object_Id = b.id AND toplayer.type = %s
        """,
        (character_id, DID_CLOTHING_BASE, DID_SETUP, INT_PALETTE_TEMPLATE, FLOAT_SHADE,
         INT_CLOTHING_PRIORITY, INT_CURRENT_WIELDED_LOCATION, INT_ITEM_TYPE,
         BOOL_TOP_LAYER_PRIORITY),
    )

    worn = []

    for row in items:
        wielded = int(row["wielded"] or 0)

        if not (wielded & COVERS_MODEL):
            continue

        # An item with neither a clothing base nor a setup cannot change the model at all.
        if not row["clothing_base"] and not row["setup"]:
            continue

        worn.append({
            "base": int(row["clothing_base"] or 0),
            "template": int(row["palette_template"] or 0),
            "shade": float(row["shade"] or 0),
            "priority": int(row["priority"] or 0),
            "wielded": wielded,
            "itemType": int(row["item_type"] or 0),
            # TRI-STATE, and it must stay that way: unset sorts BETWEEN explicit false and explicit
            # true in ACE's layering, so folding it into a bool re-orders armour.
            "topLayer": None if row["top_layer"] is None else bool(db.as_bool(row["top_layer"])),
            "setup": int(row["setup"] or 0),
        })

    # The character's own display options, and the hair texture pair - which lives on the character
    # row rather than the biota, unlike the other three face pairs.
    row = db.fetch_one(
        cur,
        "SELECT character_Options_2 AS o, default_Hair_Texture AS dhair, hair_Texture AS hair "
        "FROM `character` WHERE id = %s",
        (character_id,))

    options2 = int(row["o"]) if row and row["o"] is not None else (OPT2_SHOW_HELM | OPT2_SHOW_CLOAK)

    # Order matches AddBaseModelData: hair, eyes, nose, mouth. Each pair is emitted only when BOTH
    # halves exist, because a swap needs something to swap FROM - the server applies the same
    # `HasValue && HasValue` guard.
    face = []

    def _pair(old, new):
        if old and new:
            face.append((int(old), int(new)))

    if row:
        _pair(row["dhair"], row["hair"])

    _pair(dids.get(DID_DEFAULT_EYES_TEXTURE), dids.get(DID_EYES_TEXTURE))
    _pair(dids.get(DID_DEFAULT_NOSE_TEXTURE), dids.get(DID_NOSE_TEXTURE))
    _pair(dids.get(DID_DEFAULT_MOUTH_TEXTURE), dids.get(DID_MOUTH_TEXTURE))

    return {
        "heritage": int(heritage),
        "gender": int(gender),
        # The character's OWN setup, which the Barber can change away from the heritage default.
        # Every clothing lookup on the server is keyed on it.
        "setup": int(dids.get(DID_SETUP) or 0),
        "skin": int(dids.get(DID_SKIN_PALETTE) or 0),
        "hair": int(dids.get(DID_HAIR_PALETTE) or 0),
        "eyes": int(dids.get(DID_EYES_PALETTE) or 0),
        "head": int(dids.get(DID_HEAD_OBJECT) or 0),
        "showHelm": bool(options2 & OPT2_SHOW_HELM),
        "showCloak": bool(options2 & OPT2_SHOW_CLOAK),
        # Without these the model wears the head's DEFAULT face, so everyone sharing a heritage and
        # head shape shares one face. Found by diffing against the server's own ObjDesc (152).
        "face": face,
        # NOT sorted here any more. Layering order is the server's algorithm, not a column, and it
        # needs the dat to compute — so the exporter owns it (see ObjDescPort). Sorting by id only
        # to keep the signature stable for an unchanged outfit.
        "items": sorted(worn, key=lambda i: (i["base"], i["setup"], i["wielded"])),
    }


def _signature(appearance: dict) -> str:
    """A stable hash of everything that changes how the character LOOKS.

    EVERY input the exporter is handed belongs here. The 152 fields — setup, the helm and cloak
    options, item type, wielded slot and top-layer flag — all change the rendered result, so
    leaving any of them out would serve a stale model to a character who had visibly changed.
    Adding them invalidates the existing cache once, which is the intended effect.
    """
    parts = [
        appearance["heritage"], appearance["gender"], appearance["setup"],
        appearance["skin"], appearance["hair"], appearance["eyes"], appearance["head"],
        appearance["showHelm"], appearance["showCloak"],
    ]

    for old, new in appearance["face"]:
        parts += [old, new]

    for item in appearance["items"]:
        parts += [
            item["base"], item["template"], round(item["shade"], 4), item["priority"],
            item["wielded"], item["itemType"], item["topLayer"], item["setup"],
        ]

    return hashlib.sha256("|".join(str(p) for p in parts).encode()).hexdigest()[:16]


def _item_arg(item: dict) -> str:
    """The exporter's eight-field item spec.

    `topLayer` is empty for unset, "1"/"0" otherwise — the tri-state survives the wire because
    collapsing it would silently re-order armour.
    """
    top = "" if item["topLayer"] is None else ("1" if item["topLayer"] else "0")

    return (f"{item['base']}:{item['template']}:{item['shade']}:{item['priority']}"
            f":{item['itemType']}:{item['wielded']}:{top}:{item['setup']}")


def _build(appearance: dict, out_dir: Path) -> None:
    args = [
        EXPORTER,
        "--dat", DAT_DIR,
        "--out", str(out_dir),
        "--heritage", str(appearance["heritage"]),
        "--gender", str(appearance["gender"]),
    ]

    if appearance["setup"]:
        args += ["--setup", str(appearance["setup"])]

    for flag, key in (("--skin", "skin"), ("--hair", "hair"), ("--eyes", "eyes"), ("--head", "head")):
        if appearance[key]:
            args += [flag, str(appearance[key])]

    # Only the OFF cases are passed; the exporter defaults both to on, matching
    # CharacterOptions2.Default.
    if not appearance["showHelm"]:
        args += ["--no-helm"]

    if not appearance["showCloak"]:
        args += ["--no-cloak"]

    for old, new in appearance["face"]:
        args += ["--head-tex", f"{old}:{new}"]

    for item in appearance["items"]:
        args += ["--item", _item_arg(item)]

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
