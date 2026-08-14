#!/usr/bin/env bash
# Shadowgain 124 - list every item IconId the shard actually references.
#
#   ./icon-ids.sh > icon-ids.txt
#
# Feeds `sg-datexport --item-ids`, which exports exactly these rather than sweeping the whole
# 0x06 texture range. The difference is not small: the range holds tens of thousands of 32x32
# textures, while the shard references a few hundred - so this keeps the committed asset set to
# what the site can actually display today.
#
# The trade is that an item nobody owns yet has no icon until this is re-run. That is why
# web-deploy.sh regenerates the list on every deploy, and why the front-end falls back to
# /assets/icons/placeholder.png rather than showing a broken image.
#
# READ-ONLY. One SELECT DISTINCT against biota_properties_d_i_d.
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

$SSH "$HOST" 'bash -s' <<'REMOTE'
set -euo pipefail
cd /opt/ACE
RP=$(grep '^MYSQL_ROOT_PASSWORD=' docker.env | cut -d= -f2)
# PropertyDataId.Icon == 8. Ordered so a regenerated list diffs cleanly against the last one.
docker exec ace-db mysql -uroot -p"$RP" -N -B ace_shard -e "
SELECT DISTINCT value FROM biota_properties_d_i_d WHERE type = 8 AND value > 0 ORDER BY value;" 2>/dev/null
REMOTE
