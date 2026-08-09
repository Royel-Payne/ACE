#!/usr/bin/env bash
# Restore the Shadowgain dumps into the local WSL database, and verify.
#
#   wsl -d Ubuntu -- bash -c 'tr -d "\r" < "/mnt/c/Git Projects/Shadowgain/ACE/shadowgain/tools/local-restore.sh" > /tmp/r.sh && bash /tmp/r.sh'
#
# The tr is not optional. This file lives on an NTFS mount, so git's autocrlf keeps putting
# carriage returns back, and bash reads `set -euo pipefail\r` as an invalid option.
#
# Paths are hardcoded rather than passed as arguments on purpose: every attempt to hand a
# path containing spaces through `wsl -- bash -c '...'` in this environment lost either the
# quoting or the variable. A script file with literal paths is the one thing that survives.
set -euo pipefail

BACKUPS="/mnt/c/Games/Claude AC/Shadowgain/backups"
DB="sglocal-db"
PW="shadowgain-local-only"

for db in ace_auth ace_shard ace_world; do
  f=$(ls -t "$BACKUPS/$db"-*.sql.gz | head -1)
  echo "==> $db  <-  $(basename "$f")"
  docker exec -i "$DB" mysql -uroot -p"$PW" \
    -e "DROP DATABASE IF EXISTS $db; CREATE DATABASE $db;" 2>/dev/null
  zcat "$f" | docker exec -i "$DB" mysql -uroot -p"$PW" "$db" 2>/dev/null
done

echo
echo "==> restored schema"
docker exec "$DB" mysql -uroot -p"$PW" -N -B -e "
SELECT CONCAT('    ', RPAD(table_schema,11,' '), LPAD(COUNT(*),3,' '), ' tables')
FROM information_schema.tables WHERE table_schema LIKE 'ace_%' GROUP BY table_schema;" 2>/dev/null

echo "==> characters recovered"
docker exec "$DB" mysql -uroot -p"$PW" -N -B ace_shard -e "
SELECT CONCAT('    ', RPAD(name,16,' '), 'level ',
       COALESCE((SELECT value FROM biota_properties_int
                 WHERE object_Id = c.id AND type = 25), 1))
FROM \`character\` c WHERE is_Deleted = 0 ORDER BY name;" 2>/dev/null

docker exec "$DB" mysql -uroot -p"$PW" -N -B ace_shard -e "
SELECT CONCAT('    total: ', COUNT(*), ' live characters')
FROM \`character\` WHERE is_Deleted = 0;" 2>/dev/null
