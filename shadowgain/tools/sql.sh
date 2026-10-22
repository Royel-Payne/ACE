#!/usr/bin/env bash
# sql.sh - run SQL against the LIVE or TEST database with errors VISIBLE.
#
#   ./sql.sh "SELECT COUNT(*) FROM ace_shard.character"        # LIVE, inline statement
#   ./sql.sh --test "SELECT ..."                                 # TEST (sg-test)
#   ./sql.sh --test -f fixups.sql                                # run a local file
#   zcat dump.sql.gz | ./sql.sh --test --db ace_shard            # pipe a dump into a schema
#   ./sql.sh --live-rw "UPDATE ..."                              # writes to LIVE need the flag
#
# WHY THIS EXISTS (227, 2026-10-22). Every ad-hoc query helper written for this project ended as
#   q() { docker exec db mysql -uroot -p"$PW" ... 2>/dev/null; }
# because `-p<password>` makes mysql print "Using a password on the command line interface can be
# insecure" on stderr and the warning is noise. Throwing stderr away also throws away the error
# when the statement fails - a failed UPDATE and a silent success look identical, the exit code
# is swallowed inside $(...), and the caller reads an empty result as "no rows". That cost two
# wrong findings in one session (memory: never-suppress-stderr-on-db-checks) and a silently
# un-applied fixup pass on 227.
#
# The fix is to remove the reason for the suppression: the password goes in as MYSQL_PWD inside
# the container's environment, so mysql prints no warning, so stderr stays attached. Errors come
# back on stderr with a non-zero exit, and `set -o pipefail` keeps that exit through the pipe.
#
# Output is tab-separated, no header (-N -B), the same shape the old helpers produced.
set -euo pipefail

LIVE_KEY="C:/Users/Chris/.ssh/shadowgain_ed25519"; LIVE_HOST="root@137.184.1.44";      LIVE_DB="ace-db"
TEST_KEY="C:/Users/Chris/.ssh/sgtest_ed25519";     TEST_HOST="chris@192.168.20.20";    TEST_DB="sg-db"

TARGET=live; RW=0; FILE=""; DB=""; SQL=""
while [ $# -gt 0 ]; do
  case "$1" in
    --test) TARGET=test ;;
    --live-rw) RW=1 ;;
    -f) FILE="$2"; shift ;;
    --db) DB="$2"; shift ;;
    -h|--help) sed -n 2,12p "$0"; exit 0 ;;
    *) SQL="$1" ;;
  esac
  shift
done

if [ "$TARGET" = live ]; then KEY=$LIVE_KEY; HOST=$LIVE_HOST; CONTAINER=$LIVE_DB; else KEY=$TEST_KEY; HOST=$TEST_HOST; CONTAINER=$TEST_DB; fi

# Writes to LIVE are a deliberate act, not a typo away. Reads never need the flag.
if [ "$TARGET" = live ] && [ "$RW" = 0 ]; then
  if printf '%s' "${SQL}$( [ -n "$FILE" ] && cat "$FILE" )" | grep -qiE '^\s*(UPDATE|INSERT|DELETE|DROP|ALTER|CREATE|TRUNCATE|REPLACE)\b|;\s*(UPDATE|INSERT|DELETE|DROP|ALTER|CREATE|TRUNCATE|REPLACE)\b'; then
    echo "sql.sh: that looks like a write and the target is LIVE - add --live-rw if you mean it" >&2; exit 2
  fi
fi

SSH="ssh -i $KEY -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR"

# The password is read from the running container's own environment on the remote side and
# never travels over the wire or appears in a process list; MYSQL_PWD lives only inside the
# `docker exec` environment.
REMOTE="PW=\$(docker inspect $CONTAINER --format '{{range .Config.Env}}{{println .}}{{end}}' | grep '^MYSQL_ROOT_PASSWORD=' | cut -d= -f2-)
exec docker exec -i -e MYSQL_PWD=\"\$PW\" $CONTAINER mysql -uroot -N -B --default-character-set=utf8mb4 $DB"

if [ -n "$FILE" ]; then
  $SSH "$HOST" "$REMOTE" < "$FILE"
elif [ -n "$SQL" ]; then
  printf '%s\n' "$SQL" | $SSH "$HOST" "$REMOTE"
else
  $SSH "$HOST" "$REMOTE"          # stdin
fi
