#!/usr/bin/env bash
# announce.sh - post (or rehearse) a Shadowgain announcement to Discord.
#
#   ./announce.sh --dry-run announce-187.md      # render the embed, send nothing
#   ./announce.sh announce-187.md                # post it to #info
#   ./announce.sh --channel bugs announce-x.md   # somewhere other than #info
#   ./announce.sh --edit 15404... announce-187.md
#   ./announce.sh --list                         # enumerate channels
#
# WHY THIS EXISTS
#
# The bot does not run locally. It lives on the droplet under systemd, with its own venv
# and its token in an EnvironmentFile - so posting means assembling three things that are
# easy to get wrong from memory:
#
#   1. the ssh invocation (key + host),
#   2. `set -a; . /opt/ACE/bot.env; set +a` to load DISCORD_BOT_TOKEN / DISCORD_GUILD_ID,
#   3. /opt/ACE/botenv/bin/python - NOT the system python3, which lacks discord.py.
#
# DEPLOY.md documented all three, but as `ssh ...` with an ellipsis. On 2026-08-21 that
# ellipsis cost two failed attempts: first a LOCAL python (ModuleNotFoundError: discord,
# which read as a broken machine rather than the wrong host), then the right python
# without the env file (missing DISCORD_BOT_TOKEN). Neither was a hard problem; both were
# reconstruction of a command that should not need reconstructing.
#
# Every other step of a deploy is a script. This makes the announcement one too, so the
# runbook can say `./announce.sh <file>` and mean it literally.
#
# THE FILE ARGUMENT IS A BASENAME under shadowgain/bot/announcements/. It is resolved
# against the REPO copy for existence, and sent from the DROPLET copy - which is what
# bot-deploy.sh ships. If the two differ, the droplet's is what players get, so this
# refuses rather than guessing.
set -euo pipefail

KEY="${SG_KEY:-C:/Users/Chris/.ssh/shadowgain_ed25519}"
HOST="${SG_HOST:-root@137.184.1.44}"
SSH="ssh -i $KEY -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR"
REMOTE_DIR="/opt/ACE/bot/announcements"
CHANNEL="info"
PASS=()
FILE=""

while [ $# -gt 0 ]; do
  case "$1" in
    --channel) CHANNEL="$2"; shift 2 ;;
    --list)    PASS+=("--list"); shift ;;
    --dry-run) PASS+=("--dry-run"); shift ;;
    --force|--no-lint) PASS+=("$1"); shift ;;
    --edit)    PASS+=("--edit" "$2"); shift 2 ;;
    -*)        echo "unknown flag: $1" >&2; exit 2 ;;
    *)         FILE="$1"; shift ;;
  esac
done

# --list needs no file.
if printf '%s\n' "${PASS[@]:-}" | grep -qx -- "--list"; then
  exec $SSH "$HOST" "cd /opt/ACE/bot && set -a && . /opt/ACE/bot.env && set +a && \
    /opt/ACE/botenv/bin/python announce.py --list"
fi

[ -n "$FILE" ] || { echo "usage: ./announce.sh [--dry-run] <announce-xxx.md>" >&2; exit 2; }
FILE="$(basename "$FILE")"

# The repo is the source of truth (the 113/120 lesson that bot-deploy.sh exists for).
REPO_FILE="$(cd "$(dirname "${BASH_SOURCE[0]}")/../bot/announcements" && pwd)/$FILE"
[ -f "$REPO_FILE" ] || { echo "not in the repo: $REPO_FILE" >&2; exit 1; }

# Refuse to send something other than what the repo holds - a stale droplet copy would
# post wording nobody reviewed, and the diff-in-the-repo review would be worthless.
LOCAL_SUM=$(sha256sum "$REPO_FILE" | cut -d' ' -f1)
REMOTE_SUM=$($SSH "$HOST" "sha256sum $REMOTE_DIR/$FILE 2>/dev/null | cut -d' ' -f1" || true)

if [ -z "$REMOTE_SUM" ]; then
  echo "!! $FILE is not on the droplet. Run ./bot-deploy.sh first (it ships announcements/)." >&2
  exit 1
fi
if [ "$LOCAL_SUM" != "$REMOTE_SUM" ]; then
  echo "!! $FILE differs between repo and droplet - the droplet copy is what would be sent." >&2
  echo "   repo:    $LOCAL_SUM" >&2
  echo "   droplet: $REMOTE_SUM" >&2
  echo "   Run ./bot-deploy.sh to ship the current wording, then retry." >&2
  exit 1
fi

exec $SSH "$HOST" "cd /opt/ACE/bot && set -a && . /opt/ACE/bot.env && set +a && \
  /opt/ACE/botenv/bin/python announce.py --channel '$CHANNEL' \
  --file $REMOTE_DIR/$FILE ${PASS[*]:-}"
