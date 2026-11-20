#!/usr/bin/env bash
# Shadowgain: fetch the droplet's nightly backups to a home machine and rotate them (Task 229).
#
# PULL ONLY. This machine connects out to the droplet with a key that the droplet restricts to a
# read-only rsync of /opt/ACE/backups/nightly. Nothing ever connects in to your network.
#
# Each night is a directory <DEST>/days/<YYYY-MM-DD>/ holding ace_shard/ace_auth/ace_world
# .sql.gz and SHA256SUMS. Unchanged files (usually ace_world) are hard-linked to the previous
# night with --link-dest, so a night where only characters changed costs ~4 MB, not ~27 MB.
#
# Local rotation (independent of the droplet's 7-day one):
#   - every night for 30 days
#   - Sundays for 12 weeks
#   - the first night of each month for 12 months
#   - and the newest 7 are never deleted, whatever their age, so if pulls stop for a while the
#     last good copies do not age out underneath you.
#
# Config: ~/.config/shadowgain-backup.conf (written by backup-pull-install.sh).
#   backup-pull.sh            pull + verify + rotate
#   backup-pull.sh --dry-run  show what would be pulled and deleted
set -euo pipefail

CONF="${SG_BACKUP_CONF:-$HOME/.config/shadowgain-backup.conf}"
# shellcheck disable=SC1090
. "$CONF"
: "${SG_HOST:?}" "${SG_USER:?}" "${SG_KEY:?}" "${SG_KNOWN_HOSTS:?}" "${SG_DEST:?}"
KEEP_DAILY=${SG_KEEP_DAILY:-30}
KEEP_WEEKLY=${SG_KEEP_WEEKLY_DAYS:-84}
KEEP_MONTHLY=${SG_KEEP_MONTHLY_DAYS:-366}
KEEP_MIN=${SG_KEEP_MIN:-7}
STALE_DAYS=${SG_STALE_DAYS:-2}
DRY=0; [ "${1:-}" = "--dry-run" ] && DRY=1

SSH="ssh -i $SG_KEY -o IdentitiesOnly=yes -o UserKnownHostsFile=$SG_KNOWN_HOSTS -o StrictHostKeyChecking=yes -o BatchMode=yes"
REMOTE="$SG_USER@$SG_HOST"
DAYS="$SG_DEST/days"
DAYRE='^20[0-9][0-9]-[01][0-9]-[0-3][0-9]$'
mkdir -p "$DAYS"
log() { echo "pull: $*"; }
problems=0
warn() { echo "pull: WARNING: $*" >&2; problems=1; }
local_list() { ls -1 "$DAYS" | grep -E "$DAYRE" | sort || true; }

# --- what the droplet has ------------------------------------------------------------------
listing=$(rsync -e "$SSH" --list-only "$REMOTE:/") || { echo "pull: cannot list droplet backups" >&2; exit 1; }
mapfile -t remote_days < <(awk -v re="$DAYRE" '$1 ~ /^d/ && $NF ~ re {print $NF}' <<<"$listing" | sort)
[ "${#remote_days[@]}" -gt 0 ] || { echo "pull: droplet lists no backup days" >&2; exit 1; }

tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
if rsync -e "$SSH" -q "$REMOTE:/LAST_RESULT" "$tmp/LAST_RESULT"; then
  remote_result=$(cat "$tmp/LAST_RESULT")
else
  remote_result="unreadable"
fi
log "droplet last result: $remote_result"
case "$remote_result" in *" OK "*) ;; *) warn "droplet's last backup did not report OK";; esac

newest_remote=${remote_days[-1]}
age=$(( ( $(date -u +%s) - $(date -u -d "$newest_remote" +%s) ) / 86400 ))
[ "$age" -le "$STALE_DAYS" ] || warn "newest droplet backup is $newest_remote, ${age} days old"

# --- fetch -----------------------------------------------------------------------------------
for day in "${remote_days[@]}"; do
  if [ -f "$DAYS/$day/SHA256SUMS" ]; then
    # Already have it - re-pull only if the droplet re-ran that day and the content changed.
    rsync -e "$SSH" -q "$REMOTE:/$day/SHA256SUMS" "$tmp/SUMS" || { warn "cannot read SHA256SUMS for $day"; continue; }
    cmp -s "$tmp/SUMS" "$DAYS/$day/SHA256SUMS" && continue
    log "$day changed on the droplet, re-pulling"
  fi
  if [ "$DRY" = 1 ]; then log "would pull $day"; continue; fi
  prev=$(local_list | awk -v d="$day" '$0 < d' | tail -n 1)
  part="$DAYS/.part-$day"; rm -rf "$part"
  linkdest=(); [ -n "$prev" ] && linkdest=(--link-dest="$DAYS/$prev")
  if ! rsync -e "$SSH" -a "${linkdest[@]}" "$REMOTE:/$day/" "$part/"; then
    warn "rsync failed for $day"; rm -rf "$part"; continue
  fi
  if ( cd "$part" && sha256sum -c --quiet SHA256SUMS ); then
    rm -rf "${DAYS:?}/$day"; mv "$part" "$DAYS/$day"
    log "pulled $day"
  else
    warn "checksum mismatch for $day, discarded"; rm -rf "$part"
  fi
done

# --- rotate ----------------------------------------------------------------------------------
mapfile -t local_days < <(local_list)
n=${#local_days[@]}
now=$(date -u +%s)
seen_month=""
for i in "${!local_days[@]}"; do
  day=${local_days[$i]}
  month=${day:0:7}
  first_of_month=0
  if [ "$month" != "$seen_month" ]; then first_of_month=1; seen_month=$month; fi
  [ $(( n - i )) -le "$KEEP_MIN" ] && continue                       # newest N always kept
  a=$(( (now - $(date -u -d "$day" +%s)) / 86400 ))
  [ "$a" -lt "$KEEP_DAILY" ] && continue
  [ "$(date -u -d "$day" +%u)" = 7 ] && [ "$a" -lt "$KEEP_WEEKLY" ] && continue
  [ "$first_of_month" = 1 ] && [ "$a" -lt "$KEEP_MONTHLY" ] && continue
  if [ "$DRY" = 1 ]; then log "would rotate out $day (${a}d)"; else rm -rf "${DAYS:?}/$day"; log "rotated out $day"; fi
done

[ "$DRY" = 1 ] && exit 0
status="OK"; [ "$problems" = 1 ] && status="WARN"
newest=$(local_list | tail -n 1)
kept=$(local_list | wc -l)
echo "$(date -u +%FT%TZ) $status newest=$newest kept=$kept size=$(du -sh "$DAYS" | cut -f1)" | tee "$SG_DEST/LAST_RESULT"
exit "$problems"
