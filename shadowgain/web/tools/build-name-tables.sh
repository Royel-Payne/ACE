#!/usr/bin/env bash
# Shadowgain 124 - build the web sheet's two lookup tables from the LIVE databases.
#
#   ./build-name-tables.sh              # write both tables into web/api/data/
#   ./build-name-tables.sh --host X     # against a different droplet (TEST)
#
# Produces:
#   web/api/data/landblocks.json   landblock (hex) -> place name + indoor/outdoor
#   web/api/data/quests.json       quest key -> friendly name, cooldown, max solves
#
# WHY THIS IS GENERATED AND THEN COMMITTED
#
# Both tables come from ace_world, which is static content - it changes when we import a new
# world DB, not while players are online. Generating them at request time would put a 4,000-row
# join in front of every page load for data that is identical between deploys. So the tables are
# built here, committed, and read from disk by the API.
#
# WHAT ENTRY 124 EXPECTED VS WHAT IS ACTUALLY THERE
#
# The task listed "quest-key -> friendly-name map" as something to build. It largely exists:
# ace_world.quest already carries `message` (a human-readable line per quest), `min_Delta` (the
# cooldown in seconds) and `max_Solves`. So the quest table is an EXTRACT, not an authoring job -
# 4,237 rows, no hand-written names, and it stays correct across a world-DB update.
#
# Landblock names have no such single source, so this composes three, most specific first:
#   1. points_of_interest  - the 62 canonical town/POI names (@telepoi's own list)
#   2. portal destinations - ~2,266 landblocks named by the portal that leads there
#   3. neither             - no name; the API falls back to coordinates, which are always right
#
# Priority matters: a portal into Holtburg is named "Holtburg Portal", and letting that outrank
# the POI entry would put "Holtburg Portal" in the location banner instead of "Holtburg".
set -euo pipefail

KEY="C:/Users/Chris/.ssh/shadowgain_ed25519"
HOST="root@137.184.1.44"

while [ $# -gt 0 ]; do
  case "$1" in
    --host) HOST="$2"; shift 2 ;;
    --key)  KEY="$2";  shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

SSH="ssh -i $KEY -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR"

# Resolve web/api/data relative to this script, so it does not matter where it is run from.
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$HERE/../api/data"
mkdir -p "$OUT"

echo "==> querying $HOST (READ-ONLY)"

# -N -B gives raw tab-separated rows with no header and no box drawing, which is the only form
# that survives a pipe into awk/python intact.
$SSH "$HOST" 'bash -s' > "$OUT/.landblocks.tsv" <<'REMOTE'
set -euo pipefail
cd /opt/ACE
RP=$(grep '^MYSQL_ROOT_PASSWORD=' docker.env | cut -d= -f2)
q() { docker exec ace-db mysql -uroot -p"$RP" -N -B ace_world -e "$1" 2>/dev/null; }

# 1 - points of interest. weenie type 7 is Portal; a POI row names a portal weenie, and that
#     portal's DESTINATION (position_Type 2) is the landblock the name belongs to.
q "
SELECT LPAD(HEX(p.obj_Cell_Id >> 16), 4, '0'),
       CASE WHEN (p.obj_Cell_Id & 0xFFFF) >= 256 THEN 1 ELSE 0 END,
       'poi',
       poi.name
FROM points_of_interest poi
JOIN weenie_properties_position p ON p.object_Id = poi.weenie_Class_Id AND p.position_Type = 2;"

# 2 - every other portal destination, named by the portal itself. Lower priority; merged second.
q "
SELECT LPAD(HEX(p.obj_Cell_Id >> 16), 4, '0'),
       CASE WHEN (p.obj_Cell_Id & 0xFFFF) >= 256 THEN 1 ELSE 0 END,
       'portal',
       s.value
FROM weenie_properties_position p
JOIN weenie w ON w.class_Id = p.object_Id AND w.type = 7
JOIN weenie_properties_string s ON s.object_Id = w.class_Id AND s.type = 1
WHERE p.position_Type = 2 AND s.value IS NOT NULL AND s.value <> '';"
REMOTE

$SSH "$HOST" 'bash -s' > "$OUT/.quests.tsv" <<'REMOTE'
set -euo pipefail
cd /opt/ACE
RP=$(grep '^MYSQL_ROOT_PASSWORD=' docker.env | cut -d= -f2)
docker exec ace-db mysql -uroot -p"$RP" -N -B ace_world -e "
SELECT name, min_Delta, max_Solves, COALESCE(message,'')
FROM quest;" 2>/dev/null
REMOTE

echo "==> shaping JSON"
python "$HERE/shape-name-tables.py" "$OUT"

rm -f "$OUT/.landblocks.tsv" "$OUT/.quests.tsv"

echo "==> wrote $OUT/landblocks.json and $OUT/quests.json"
