#!/usr/bin/env bash
# Shadowgain: nightly database backup on the LIVE droplet (Task 229).
#
# Runs from shadowgain-backup.timer. Dumps ace_shard, ace_auth and ace_world from the ace-db
# container into /opt/ACE/backups/nightly/<YYYY-MM-DD>/, verifies each dump, writes SHA256SUMS,
# then rotates. Nothing here writes to the database: --single-transaction takes a consistent
# InnoDB snapshot without locking tables, so players do not notice it.
#
# Dumps are byte-stable when the data has not changed (--skip-dump-date, gzip -n). That lets the
# off-site pull hard-link an unchanged ace_world against the previous night instead of storing it
# again.
#
# Rotation on the droplet: the last 7 nights, plus Sunday's for 5 weeks. The off-site copy keeps
# far more (see backup-pull.sh); the droplet only needs enough to survive a missed pull.
#
# The pull user (sgbackup) reads this tree through rrsync -ro and can do nothing else; see
# backup-droplet-setup.sh. Files are group-readable by sgbackup for that reason.
#
#   /opt/ACE/backup-nightly.sh            run now (safe any time, same-day rerun replaces the day)
#   /opt/ACE/backup-nightly.sh --dry-run  show what rotation would delete, dump nothing
set -euo pipefail

ROOT=/opt/ACE/backups/nightly
DBS=(ace_shard ace_auth ace_world)
KEEP_DAILY_DAYS=7
KEEP_SUNDAY_DAYS=35
GROUP=sgbackup
DRY=0; [ "${1:-}" = "--dry-run" ] && DRY=1

log() { echo "backup: $*"; }
fail() { echo "backup: FAILED: $*" >&2; echo "$(date -u +%FT%TZ) FAILED $*" > "$ROOT/LAST_RESULT"; exit 1; }

cd /opt/ACE
RP=$(grep '^MYSQL_ROOT_PASSWORD=' docker.env | cut -d= -f2)
[ -n "$RP" ] || { echo "backup: no MYSQL_ROOT_PASSWORD in docker.env" >&2; exit 1; }

umask 027
mkdir -p "$ROOT"; chgrp "$GROUP" "$ROOT"; chmod 750 "$ROOT"

DAY=$(date -u +%F)
if [ "$DRY" = 0 ]; then
  TMP="$ROOT/.tmp-$DAY"
  rm -rf "$TMP"; mkdir "$TMP"
  for db in "${DBS[@]}"; do
    out="$TMP/$db.sql.gz"
    # pipefail makes a mysqldump failure fail this line; stderr is left visible on purpose.
    docker exec -e MYSQL_PWD="$RP" ace-db mysqldump -uroot \
        --single-transaction --quick --routines --triggers --events \
        --skip-dump-date --databases "$db" | gzip -n -6 > "$out" \
      || fail "mysqldump $db"
    gzip -t "$out" || fail "gzip test $db"
    # mysqldump writes this trailer only after the last table; its absence means a cut-off dump.
    zcat "$out" | tail -n 1 | grep -q '^-- Dump completed' || fail "$db dump has no completion trailer"
    log "$db $(stat -c %s "$out") bytes"
  done
  ( cd "$TMP" && sha256sum *.sql.gz > SHA256SUMS )
  chgrp -R "$GROUP" "$TMP"; chmod 750 "$TMP"; chmod 640 "$TMP"/*
  rm -rf "$ROOT/$DAY"; mv "$TMP" "$ROOT/$DAY"
  # The day directory gets its own mtime last, so a pull never sees a half-written day:
  # it is either absent or complete (the .tmp- prefix is excluded by the pull).
fi

# Rotation: dated directories only, judged by name, never by mtime.
now=$(date -u +%s)
for d in "$ROOT"/20[0-9][0-9]-[0-1][0-9]-[0-3][0-9]; do
  [ -d "$d" ] || continue
  name=$(basename "$d")
  t=$(date -u -d "$name" +%s) || continue
  age=$(( (now - t) / 86400 ))
  dow=$(date -u -d "$name" +%u)   # 7 = Sunday
  if [ "$age" -lt "$KEEP_DAILY_DAYS" ]; then continue; fi
  if [ "$dow" = 7 ] && [ "$age" -lt "$KEEP_SUNDAY_DAYS" ]; then continue; fi
  if [ "$DRY" = 1 ]; then log "would delete $name (age ${age}d)"; else rm -rf "$d"; log "rotated out $name"; fi
done

[ "$DRY" = 1 ] && exit 0
echo "$(date -u +%FT%TZ) OK $DAY" > "$ROOT/LAST_RESULT"
chgrp "$GROUP" "$ROOT/LAST_RESULT"; chmod 640 "$ROOT/LAST_RESULT"
log "done: $ROOT/$DAY"
