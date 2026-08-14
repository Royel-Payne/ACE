"""Shadowgain 124 - turn the raw TSV dumps from build-name-tables.sh into the API's JSON tables.

Split out of the shell script rather than done with awk because both tables need real
priority/dedup logic, and the moment that lands in awk nobody can read it again.
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

# Portal names are written for a player standing in front of a portal, not for a location label.
# Stripping the scaffolding turns "Holtburg Portal" into "Holtburg" and "Portal to Arwic" into
# "Arwic", which is what belongs in the banner. Anything that reduces to nothing keeps its
# original name - "Surface" is a real, if vague, answer and better than an empty string.
PORTAL_NOISE = [
    (re.compile(r"^Portal\s+to\s+", re.I), ""),
    (re.compile(r"\s+Portal$", re.I), ""),
    (re.compile(r"^Gateway\s+to\s+", re.I), ""),
    (re.compile(r"\s+Gateway$", re.I), ""),
    (re.compile(r"^Entrance\s+to\s+", re.I), ""),
    (re.compile(r"\s+Entrance$", re.I), ""),
]

# Names that say nothing about WHERE you are. A landblock labelled only "Surface" or "Exit" is
# worse than no label, because the API's fallback (coordinates) is genuinely informative and
# these would suppress it.
USELESS = {"surface", "exit", "portal", "gateway", "entrance", "return", "town network"}


def clean_portal_name(raw: str) -> str | None:
    name = raw.strip()

    for pattern, repl in PORTAL_NOISE:
        stripped = pattern.sub(repl, name).strip()

        if stripped:
            name = stripped

    if not name or name.lower() in USELESS:
        return None

    return name


def build_landblocks(tsv: Path) -> dict:
    # Two passes' worth of rows arrive in one file, tagged 'poi' or 'portal'. POI wins outright;
    # among portals the SHORTEST name wins, because a landblock reached by several portals
    # collects names like "Hebian-To" and "Hebian-To Marketplace Portal", and the shorter one is
    # reliably the place rather than the doorway.
    out: dict[str, dict] = {}

    for line in tsv.read_text(encoding="utf-8", errors="replace").splitlines():
        parts = line.split("\t")

        if len(parts) < 4:
            continue

        block, indoor, source, name = parts[0], parts[1] == "1", parts[2], parts[3]

        if source == "poi":
            cleaned = name.strip() or None
        else:
            cleaned = clean_portal_name(name)

        if not cleaned:
            continue

        existing = out.get(block)

        if existing is None:
            out[block] = {"name": cleaned, "indoor": indoor, "source": source}
            continue

        if existing["source"] == "poi":
            continue  # a POI name is canonical; nothing outranks it

        if source == "poi" or len(cleaned) < len(existing["name"]):
            out[block] = {"name": cleaned, "indoor": indoor, "source": source}

    return dict(sorted(out.items()))


def build_quests(tsv: Path) -> dict:
    out: dict[str, dict] = {}

    for line in tsv.read_text(encoding="utf-8", errors="replace").splitlines():
        parts = line.split("\t")

        if len(parts) < 4:
            continue

        key, min_delta, max_solves, message = parts[0], parts[1], parts[2], parts[3]

        # `message` is the human line the server itself shows; when a quest has none, the key is
        # the only name there is, so it is spaced out rather than left in CamelCase.
        label = message.strip() or spaced(key)

        out[key] = {
            "name": label,
            # min_Delta is the cooldown in seconds; 0 means repeatable immediately.
            "minDelta": int(min_delta or 0),
            # max_Solves -1 means unlimited. Kept as-is rather than translated to null so the
            # front-end sees the same sentinel the world DB uses.
            "maxSolves": int(max_solves or 0),
        }

    return dict(sorted(out.items()))


def spaced(key: str) -> str:
    """"HoltburgAfrinCorn1204" -> "Holtburg Afrin Corn 1204". A last resort, not a nice name."""
    step = re.sub(r"(?<=[a-z0-9])(?=[A-Z])", " ", key)
    step = re.sub(r"(?<=[A-Za-z])(?=\d)", " ", step)
    return re.sub(r"[_\-]+", " ", step).strip()


def main() -> int:
    out_dir = Path(sys.argv[1])

    landblocks = build_landblocks(out_dir / ".landblocks.tsv")
    quests = build_quests(out_dir / ".quests.tsv")

    (out_dir / "landblocks.json").write_text(
        json.dumps(landblocks, indent=1, ensure_ascii=False), encoding="utf-8"
    )
    (out_dir / "quests.json").write_text(
        json.dumps(quests, indent=1, ensure_ascii=False), encoding="utf-8"
    )

    named_poi = sum(1 for v in landblocks.values() if v["source"] == "poi")

    print(f"    landblocks.json   {len(landblocks):,} blocks ({named_poi} from points_of_interest)")
    print(f"    quests.json       {len(quests):,} quests")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
