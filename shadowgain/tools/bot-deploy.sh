#!/usr/bin/env bash
. "$(dirname "${BASH_SOURCE[0]}")/_isolation-guard.sh"   # 191: refuse LIVE from the experiment worktree
# Shadowgain Discord bot deploy.
#
#   ./bot-deploy.sh             # ship code + deps + service, then restart
#   ./bot-deploy.sh --setup-db  # also create the read-only MySQL user (first run only)
#   ./bot-deploy.sh --status    # health check, no changes
#   ./bot-deploy.sh --no-start  # install everything but leave the service stopped
#
# The bot runs on the droplet under systemd so it survives reboots and gateway drops
# without a human, matching the standard the game server was brought up to in 025.
set -euo pipefail

# THE REPO IS THE SOURCE OF TRUTH, and this line is why that is now true.
#
# This used to point at "C:/Games/Claude AC/Shadowgain/bot", which is NOT a git repository - so
# what shipped to the droplet was never what git held, and the two drifted in BOTH directions
# without anything noticing:
#
#   - 113: the deploy copy of shadowgain_bot.py was ~58 lines AHEAD of the repo, carrying
#          mask_account() and the 069 honour-roll filter. Reading the repo showed code that had
#          not run for two entries.
#   - 120: announce.py, readchat.py, whois_linked.py, make_audit_channel.py and
#          screenshots_and_verified_color.py existed ONLY in the repo, so bot-deploy.sh could
#          not ship them and they had to be scp'd by hand - which is how the announce.py guard
#          reached the droplet.
#   - README.md had drifted too: the droplet carried a stale 5,855-byte copy from 2026-08-07
#          while the repo held the maintained 13,010-byte one.
#
# Pointing at the repo makes `git log` an honest record of what is running, and makes the
# md5 check in 113 unnecessary rather than merely habitual.
SRC="C:/Git Projects/Shadowgain/ACE/shadowgain/bot"
KEY="C:/Users/Chris/.ssh/shadowgain_ed25519"
HOST="root@137.184.1.44"
SSH="ssh -i $KEY -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR"

SETUP_DB=0; STATUS=0; START=1
for a in "$@"; do
  case "$a" in
    --setup-db) SETUP_DB=1 ;;
    --status)   STATUS=1 ;;
    --no-start) START=0 ;;
    *) echo "unknown arg: $a"; exit 1 ;;
  esac
done

if [ "$STATUS" = "1" ]; then
  $SSH "$HOST" 'bash -s' <<'REMOTE'
echo "=== service ==="
systemctl is-active shadowgain-bot >/dev/null 2>&1 && echo "  active" || echo "  NOT active"
systemctl is-enabled shadowgain-bot >/dev/null 2>&1 && echo "  enabled at boot" || echo "  NOT enabled at boot"
echo "=== feed files ==="
for f in /opt/ACE/Logs/chatrelay.jsonl /opt/ACE/Logs/sgevents.jsonl; do
  [ -f "$f" ] && echo "  $(wc -l < "$f") lines  $f" || echo "  MISSING  $f"
done
echo "=== config present? (names only, never values) ==="
if [ -f /opt/ACE/bot.env ]; then
  while IFS='=' read -r k v; do
    case "$k" in ''|\#*) continue ;; esac
    [ -n "$v" ] && echo "  $k = set" || echo "  $k = EMPTY"
  done < /opt/ACE/bot.env
else
  echo "  /opt/ACE/bot.env MISSING"
fi
echo "=== last 15 log lines ==="
journalctl -u shadowgain-bot -n 15 --no-pager 2>/dev/null | sed 's/^/  /' || echo "  no journal yet"
REMOTE
  exit 0
fi

cd "$SRC"

# Compile before shipping. A syntax error reaches the droplet perfectly happily and only
# announces itself as a systemd restart loop afterwards - which is exactly what happened
# once, and cost a deploy cycle to notice. py_compile is instant and catches all of it.
echo "==> syntax check"
if ! python -m py_compile shadowgain_bot.py 2>&1; then
  echo "!! shadowgain_bot.py does not compile - NOT deploying"
  exit 1
fi
echo "  OK"

echo "==> shipping bot source"
$SSH "$HOST" 'mkdir -p /opt/ACE/bot'

# Every .py in the directory, not a hand-maintained list. The old list named four files and
# silently omitted the five utility scripts, so they could only ever reach the droplet by hand -
# which meant the deploy script was not actually deploying the bot, only most of it. A glob
# cannot fall behind when a script is added.
#
# announcements/ carries the message bodies. announce.py reads wording from a FILE precisely so
# it can be reviewed as a diff before players see it, which only works if the files are in git.
tar -czf - *.py requirements.txt README.md setup.sql \
    $([ -d announcements ] && echo announcements) | \
  $SSH "$HOST" 'tar -xzf - -C /opt/ACE/bot'

echo "==> installing service unit"
cat shadowgain-bot.service | $SSH "$HOST" 'cat > /etc/systemd/system/shadowgain-bot.service'

echo "==> seeding bot.env if absent (never overwrites an existing one)"
cat env.example | $SSH "$HOST" '
  if [ ! -f /opt/ACE/bot.env ]; then
    cat > /opt/ACE/bot.env
    chmod 600 /opt/ACE/bot.env
    echo "  created /opt/ACE/bot.env - fill in DISCORD_BOT_TOKEN and DISCORD_VERIFIED_ROLE_ID"
  else
    cat > /dev/null
    echo "  /opt/ACE/bot.env already exists - left untouched"
  fi'

echo "==> python venv + dependencies"
$SSH "$HOST" 'bash -s' <<'REMOTE'
set -euo pipefail
# Test for the venv MODULE, not the interpreter. Ubuntu ships python3 without ensurepip,
# so `command -v python3` succeeds while `python3 -m venv` fails - which is exactly how
# this broke on the first run.
if ! python3 -c "import ensurepip" >/dev/null 2>&1; then
  apt-get update -qq
  apt-get install -y -qq python3-venv >/dev/null
fi
# Probe for PIP, not for the python symlink. A venv whose create failed at the ensurepip
# step still leaves bin/python behind, so testing that alone reports a broken venv as
# healthy and skips the rebuild - which is how this failed twice.
if [ ! -x /opt/ACE/botenv/bin/pip ]; then
  rm -rf /opt/ACE/botenv
  python3 -m venv /opt/ACE/botenv
fi
/opt/ACE/botenv/bin/pip install -q --upgrade pip
/opt/ACE/botenv/bin/pip install -q -r /opt/ACE/bot/requirements.txt
echo "  $(/opt/ACE/botenv/bin/python -c 'import discord,pymysql; print("discord.py", discord.__version__, "/ pymysql", pymysql.__version__)')"
REMOTE

if [ "$SETUP_DB" = "1" ]; then
  echo "==> creating the read-only MySQL user"
  # The password is generated ON the droplet and written straight into bot.env. It is
  # never printed, never passed as an argument, and never leaves the box.
  $SSH "$HOST" 'bash -s' <<'REMOTE'
set -euo pipefail
cd /opt/ACE
RP=$(grep '^MYSQL_ROOT_PASSWORD=' docker.env | cut -d= -f2)
BOTPW=$(tr -dc 'A-Za-z0-9' </dev/urandom | head -c 32)
sed "s/REPLACE_ME/$BOTPW/" /opt/ACE/bot/setup.sql | docker exec -i ace-db mysql -uroot -p"$RP" 2>/dev/null
# Replace the line in place, keeping mode 600.
if grep -q '^SG_DB_PASSWORD=' /opt/ACE/bot.env; then
  sed -i "s|^SG_DB_PASSWORD=.*|SG_DB_PASSWORD=$BOTPW|" /opt/ACE/bot.env
else
  echo "SG_DB_PASSWORD=$BOTPW" >> /opt/ACE/bot.env
fi
unset BOTPW
echo "  sgbot created, password written to /opt/ACE/bot.env"
REMOTE

  echo "==> verifying the user is READ-ONLY (a write must fail)"
  $SSH "$HOST" 'bash -s' <<'REMOTE'
set -euo pipefail
PW=$(grep '^SG_DB_PASSWORD=' /opt/ACE/bot.env | cut -d= -f2)
# </dev/null on EVERY docker exec here is load-bearing. `docker exec -i` inherits this
# heredoc as its stdin and consumes the rest of the script, so a single missing
# redirect silently truncates the run - which is exactly how the first deploy of this
# script appeared to "stop" with no error.
m() { docker exec ace-db mysql -usgbot -p"$PW" -N -B "$@" </dev/null 2>&1 | grep -v "Using a password"; }
echo "  SELECT works : $(m -e "SELECT COUNT(*) FROM ace_shard.\`character\`;")"
if m -e "UPDATE ace_shard.\`character\` SET name=name WHERE id=0;" | grep -qi denied; then
  echo "  write denied : correct"
else
  echo "  !! WRITE WAS NOT DENIED - the user is NOT read-only"; exit 1
fi
if m -e "SELECT passwordHash FROM ace_auth.account LIMIT 1;" | grep -qi denied; then
  echo "  passwordHash unreadable : correct"
else
  echo "  !! passwordHash READABLE - column scoping failed"; exit 1
fi
REMOTE
fi

echo "==> systemd"
$SSH "$HOST" 'systemctl daemon-reload && systemctl enable -q shadowgain-bot && echo "  enabled at boot"'

if [ "$START" = "1" ]; then
  # Refuse to start half-configured: systemd would restart-loop and bury the reason.
  #
  # Only the two that make the bot unable to RUN are fatal. DISCORD_VERIFIED_ROLE_ID is
  # deliberately not in this list - without it the relay and the bug funnel work fine and
  # only role-granting is inert, so blocking startup on it would hold back two working
  # features for the sake of a third.
  # The trailing `exit 0` is load-bearing. A bare for-loop returns the status of its LAST
  # command, and `[ -z "$v" ] && echo` is FALSE (exit 1) precisely when the variable is
  # set - so a fully-configured env made this look like a failure and set -e killed the
  # deploy silently, right at the point where it should have started the bot.
  MISSING=$($SSH "$HOST" 'for k in DISCORD_BOT_TOKEN SG_DB_PASSWORD; do
      v=$(grep "^$k=" /opt/ACE/bot.env 2>/dev/null | cut -d= -f2-)
      [ -z "$v" ] && echo -n "$k "
    done; exit 0')
  ROLE=$($SSH "$HOST" 'grep "^DISCORD_VERIFIED_ROLE_ID=" /opt/ACE/bot.env | cut -d= -f2-; exit 0')
  [ -z "$ROLE" ] && echo "==> NOTE: DISCORD_VERIFIED_ROLE_ID is unset - relay and bugs will work, /verify will not grant a role"
  if [ -n "$MISSING" ]; then
    echo "==> NOT starting - still unset in /opt/ACE/bot.env: $MISSING"
    echo "==> fill those in, then: ./bot-deploy.sh"
    exit 0
  fi
  echo "==> restarting"
  $SSH "$HOST" 'systemctl restart shadowgain-bot && sleep 4 && systemctl is-active shadowgain-bot'
  $SSH "$HOST" 'journalctl -u shadowgain-bot -n 12 --no-pager' | sed 's/^/  /'
else
  echo "==> --no-start: installed, service left stopped"
fi
