#!/usr/bin/env bash
# Shadowgain 124 - deploy the web character sheet to the droplet.
#
#   ./web-deploy.sh                # ship api + assets + front-end, restart the service
#   ./web-deploy.sh --setup        # first run: create the unix user, venv, DB user, Caddy block
#   ./web-deploy.sh --assets-only  # just the static side (Cowork front-end iteration)
#   ./web-deploy.sh --host X       # a different droplet (TEST)
#
# WHAT THIS DOES NOT DO
#
# It does not restart, touch, or even look at the game server. The web service is standalone
# infrastructure that reads the shard over 127.0.0.1:3306 - deploying it while players are online
# is a non-event, and if it fails the world does not notice. That independence is the whole
# reason Part 1 was scoped with no game-server code in it (Task.md 123).
#
# NEVER chmod a bare glob. The apex site's deploy learned this in 032: `chmod 644 /var/www/x/*`
# also matched the data/ DIRECTORY, stripping its execute bit so Caddy could no longer traverse
# into it, and every JSON feed 404'd while the files themselves were fine. Directories need 755,
# files need 644, and `find -type` is the only way to say that.
set -euo pipefail

KEY="C:/Users/Chris/.ssh/shadowgain_ed25519"
HOST="root@137.184.1.44"
SETUP=0
ASSETS_ONLY=0
SKIP_EXPORTER=0

while [ $# -gt 0 ]; do
  case "$1" in
    --host)        HOST="$2"; shift 2 ;;
    --key)         KEY="$2";  shift 2 ;;
    --setup)       SETUP=1;   shift ;;
    --assets-only) ASSETS_ONLY=1; shift ;;
    --skip-exporter) SKIP_EXPORTER=1; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

SSH="ssh -i $KEY -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WEB="$HERE/.."

APP_DIR=/opt/shadowgain-web
WWW_DIR=/var/www/my.shadowgain

# ---------------------------------------------------------------------------------------------
# pre-flight: refuse to ship anything that looks like a credential
# ---------------------------------------------------------------------------------------------
#
# Same check the apex site deploy runs, for the same reason: these files go to a public web root,
# and a leaked internal address or key is not something a later commit can take back.
echo "==> checking for secrets in the payload"
if grep -rlEi "BEGIN [A-Z ]*PRIVATE KEY|SG_WEB_DB_PASSWORD *= *[^ ]|SG_WEB_SECRET_KEY *= *[^ ]" \
     "$WEB/api" "$WEB/public" 2>/dev/null | grep -v '\.pyc$'; then
  echo "!! credential-shaped string in the files above - aborting"; exit 1
fi

# The API's data/ tables are generated, not authored. Shipping a stale or missing one means the
# rank maths silently falls back to nothing, so their presence is checked rather than assumed.
for f in xptable.json skills.json vitals.json enums.json landblocks.json quests.json; do
  [ -f "$WEB/api/data/$f" ] || { echo "!! missing api/data/$f - run tools/build-name-tables.sh and the exporter"; exit 1; }
done

# ---------------------------------------------------------------------------------------------
# pre-flight: the front-end has to PARSE
# ---------------------------------------------------------------------------------------------
#
# 163. index.html carries ~900 lines of inline JavaScript and the API's pytest suite cannot see a
# line of it. A syntax error there is not a degraded page, it is a BLANK one: the browser abandons
# the entire script block, so nothing mounts and every part of the portal breaks at once.
#
# Until now the only check was loading the deployed page and looking at it - i.e. after it was
# live. This is the same check, thirty seconds earlier.
#
# REQUIRED, not best-effort. A check that prints a warning and continues is a check nobody runs,
# and the point is that it cannot be forgotten on the one deploy that needed it. SG_SKIP_JSCHECK=1
# exists for a genuine emergency and announces itself.
NODE="$(command -v node || true)"
[ -n "$NODE" ] || { [ -x "/c/Program Files/nodejs/node.exe" ] && NODE="/c/Program Files/nodejs/node.exe"; }

if [ "${SG_SKIP_JSCHECK:-0}" = 1 ]; then
  echo "!! SG_SKIP_JSCHECK=1 - shipping front-end JavaScript that has NOT been parsed"
elif [ -z "$NODE" ]; then
  echo "!! node not found, so the front-end cannot be syntax-checked."
  echo "   winget install --id OpenJS.NodeJS.LTS -e      (or SG_SKIP_JSCHECK=1 to override)"
  exit 1
else
  echo "==> parsing inline JavaScript ($("$NODE" --version))"
  JSTMP="$(mktemp -d)"
  trap 'rm -rf "$JSTMP"' EXIT
  # Inline blocks that are actually JAVASCRIPT. Three ways to not be one, and the middle case is
  # why this is an allowlist on `type` rather than "everything without a src=":
  #
  #   src=...                  external, not ours to parse
  #   type="importmap"         JSON. Caught the first version of this check red-handed - it made
  #                            the check fail on a perfectly good file, which would have been
  #                            written off as "the checker is broken" and disabled.
  #   type="text/template"     markup someone may add later
  #
  # Joined with a `;` so a trailing expression in one block cannot fuse with the start of the next.
  python -c "
import re, sys
JS = ('', 'module', 'text/javascript', 'application/javascript')
src = open(sys.argv[1], encoding='utf-8').read()
blocks = []
for attrs, body in re.findall(r'<script([^>]*)>(.*?)</script>', src, re.S):
    if re.search(r'\bsrc\s*=', attrs):
        continue
    m = re.search(r'\btype\s*=\s*[\"\']([^\"\']*)[\"\']', attrs)
    if (m.group(1).strip().lower() if m else '') in JS:
        blocks.append(body)
if not blocks:
    print('!! no inline JavaScript found - has index.html changed shape?'); sys.exit(1)
open(sys.argv[2], 'w', encoding='utf-8').write('\n;\n'.join(blocks))
print('    %d block(s), %d lines' % (len(blocks), sum(b.count(chr(10)) for b in blocks)))
" "$WEB/public/index.html" "$JSTMP/inline.js" || exit 1
  "$NODE" --check "$JSTMP/inline.js" || { echo "!! index.html inline JavaScript does not parse - aborting"; exit 1; }
  echo "    parses"
fi

# ---------------------------------------------------------------------------------------------
# --setup: everything that only has to happen once
# ---------------------------------------------------------------------------------------------
if [ "$SETUP" = 1 ]; then
  echo "==> first-run setup on $HOST"

  $SSH "$HOST" 'bash -s' <<'REMOTE'
set -euo pipefail

# A dedicated unmixed user. Not caddy (which must not be able to read web.env) and not root.
id -u sgweb >/dev/null 2>&1 || useradd --system --home /opt/shadowgain-web --shell /usr/sbin/nologin sgweb

mkdir -p /opt/shadowgain-web /var/www/my.shadowgain
chown -R sgweb:sgweb /opt/shadowgain-web
chown -R caddy:caddy /var/www/my.shadowgain

apt-get install -y python3-venv >/dev/null 2>&1 || true

[ -d /opt/shadowgain-web/venv ] || python3 -m venv /opt/shadowgain-web/venv
REMOTE

  # web.env: the two secrets, generated ON THE DROPLET so neither ever exists on this machine or
  # in the repo.
  #
  # The MySQL user is NOT created here - it is created after the API ships, because the grants
  # live in api/setup.sql and that file is not on the box yet. Doing it in this block is what
  # the first run actually tried, and it failed with "can't read setup.sql".
  echo "==> creating $APP_DIR/web.env (secrets generated on the droplet)"
  $SSH "$HOST" 'bash -s' <<'REMOTE'
set -euo pipefail
ENV=/opt/shadowgain-web/web.env

if [ -f "$ENV" ]; then
  echo "    web.env already exists - leaving it alone (delete it to regenerate)"
  exit 0
fi

# A fresh random password for the read-only MySQL user, and a session-signing key.
DBPW=$(head -c 24 /dev/urandom | base64 | tr -d '/+=' | head -c 32)
SECRET=$(head -c 48 /dev/urandom | base64 | tr -d '/+=' | head -c 64)

umask 077
cat > "$ENV" <<EOF
# Shadowgain web character sheet. Generated by web-deploy.sh --setup. NEVER COMMIT THIS.
SG_WEB_DB_HOST=127.0.0.1
SG_WEB_DB_PORT=3306
SG_WEB_DB_USER=sgweb
SG_WEB_DB_PASSWORD=$DBPW
SG_WEB_DB_SHARD=ace_shard
SG_WEB_DB_AUTH=ace_auth
SG_WEB_SECRET_KEY=$SECRET
SG_WEB_STATUS_URL=https://shadowgain.com/data/status.json
SG_WEB_ONLINE_NAMES=/opt/ACE/online-names.json
EOF

chown root:sgweb "$ENV"
chmod 640 "$ENV"
echo "    web.env written (mode 640, root:sgweb)"
REMOTE
fi

# ---------------------------------------------------------------------------------------------
# ship the static side
# ---------------------------------------------------------------------------------------------
# --- 143: refuse to ship an index.html that has lost a core definition -------------------------
#
# index.html is one large inline script, and twice now a structural edit to it removed a
# neighbouring block along with its target - once caught locally, once DEPLOYED, which left the
# live page unable to wire its own login handler. Clicking "Sign in" then did a native form
# submit and landed on "/?", which is what Chris hit.
#
# There is no JS engine on this machine or the droplet, so this is not a syntax check. It is the
# narrower guard that actually matches the failure: if a top-level definition the page cannot run
# without has vanished, something was deleted that should not have been, and we stop.
INDEX="$WEB/public/index.html"

# Patterns include the character that FOLLOWS the name. Without it `grep "const WORN_SLOTS"`
# happily matches `const WORN_SLOTS_ANYTHING`, so a renamed or half-deleted definition sails
# through - which is exactly how the first version of this guard passed a file I had deliberately
# broken to test it.
for sym in "function renderBanner(" "function renderAttributes(" "function renderSkills("            "function renderInventory(" "function showReadout(" "function renderGrid("            "function mountModel(" "function examineHtml(" "function creditsLine("            "const WORN_SLOTS = [" "const ARMOUR_GRID = [" "const AETHERIA_ICONS = ["            "let slotsOn = false"; do
  if ! grep -qF "$sym" "$INDEX"; then
    echo "!! $INDEX is missing '$sym' - refusing to deploy."
    echo "   A structural edit almost certainly deleted more than it meant to. Check git diff."
    exit 1
  fi
done

echo "    index.html core definitions present"

echo "==> shipping front-end + assets to $WWW_DIR"

# public/ holds the front-end (Cowork's) and assets/ (the exporter's output). Both are static and
# both belong to caddy.
if [ -d "$WEB/public" ]; then
  # 216: public/suit is a MIRRORED generated tree, not an accumulating one. The wasm publish
  # fingerprints its runtime files with a fresh hash on every rebuild, and tar-over-existing
  # never deletes, so without this the droplet gains an orphaned ~30MB generation per deploy.
  # Removed just before the tar lands, so the gap where /suit/ 404s is a second or two.
  $SSH "$HOST" "rm -rf $WWW_DIR/suit"

  tar -czf - -C "$WEB/public" . | $SSH "$HOST" "mkdir -p $WWW_DIR && tar -xzf - -C $WWW_DIR"

  $SSH "$HOST" "chown -R caddy:caddy $WWW_DIR && \
    find $WWW_DIR -type d -exec chmod 755 {} + && \
    find $WWW_DIR -type f -exec chmod 644 {} +"

  echo "    $($SSH "$HOST" "find $WWW_DIR -type f | wc -l") files in the web root"
fi

if [ "$ASSETS_ONLY" = 1 ]; then
  echo "==> assets only - done"
  exit 0
fi

# ---------------------------------------------------------------------------------------------
# ship the API
# ---------------------------------------------------------------------------------------------
echo "==> shipping API to $APP_DIR"

# --exclude on the way out rather than a clean-up on the way in: __pycache__ from a Windows
# CPython is useless on the droplet, and .pyc files with embedded absolute paths are pure noise.
tar --exclude='__pycache__' --exclude='*.pyc' -czf - -C "$WEB" api \
  | $SSH "$HOST" "tar -xzf - -C $APP_DIR"

$SSH "$HOST" "cp $APP_DIR/api/setup.sql $APP_DIR/setup.sql && chown -R sgweb:sgweb $APP_DIR/api $APP_DIR/setup.sql"

if [ "$SETUP" = 1 ]; then
  # NOW the grants can go in - setup.sql arrived with the API above.
  #
  # Idempotent on purpose: the password is read back out of web.env rather than regenerated, and
  # ALTER USER re-syncs it. So a --setup that failed halfway (as the first one did) can simply be
  # re-run, and it converges instead of leaving a user whose password nobody knows.
  echo "==> creating the read-only MySQL user"
  $SSH "$HOST" 'bash -s' <<'REMOTE'
set -euo pipefail
DBPW=$(grep '^SG_WEB_DB_PASSWORD=' /opt/shadowgain-web/web.env | cut -d= -f2-)

[ -n "$DBPW" ] || { echo "!! no SG_WEB_DB_PASSWORD in web.env"; exit 1; }

cd /opt/ACE
RP=$(grep '^MYSQL_ROOT_PASSWORD=' docker.env | cut -d= -f2)

# setup.sql carries a REPLACE_ME placeholder precisely so the real password never sits in a
# committed file. ALTER after CREATE IF NOT EXISTS so a re-run fixes a drifted password.
{
  sed "s/REPLACE_ME/$DBPW/" /opt/shadowgain-web/setup.sql
  echo "ALTER USER 'sgweb'@'%' IDENTIFIED BY '$DBPW';"
  echo "FLUSH PRIVILEGES;"
} | docker exec -i ace-db mysql -uroot -p"$RP" 2>&1 | grep -v "Using a password" || true

# Prove the grants actually work, as the sgweb user, before anything depends on them. A missing
# grant does not fail at deploy time - it fails on the first request that needs that table.
docker exec ace-db mysql -usgweb -p"$DBPW" -N -B -e \
  "SELECT 'shard', COUNT(*) FROM ace_shard.\`character\`;
   SELECT 'auth',  COUNT(*) FROM ace_auth.account;
   SELECT 'int64', COUNT(*) FROM ace_shard.biota_properties_int64;
   SELECT 'dials', COUNT(*) FROM ace_shard.config_properties_boolean;" 2>&1 | grep -v "Using a password"

# And prove it CANNOT write. This is the rule the whole service rests on, so it is tested rather
# than assumed - an accidentally over-broad grant would otherwise go unnoticed indefinitely.
if docker exec ace-db mysql -usgweb -p"$DBPW" -e \
     "UPDATE ace_shard.\`character\` SET name=name WHERE id=0;" >/dev/null 2>&1; then
  echo "!! sgweb CAN WRITE to the shard - the grant is wrong. Aborting."
  exit 1
fi
echo "    verified: sgweb can read, and cannot write"
REMOTE
fi

# The model assembler: a self-contained linux-x64 binary built from shadowgain/exporter. Shipped
# separately from the API because it is ~76MB and changes far less often - `--skip-exporter` skips
# it on the common deploy where only Python changed.
if [ "$SKIP_EXPORTER" = 0 ] && [ -f "$WEB/../exporter/publish/sg-datexport" ]; then
  echo "==> shipping the model exporter"
  $SSH "$HOST" "mkdir -p $APP_DIR/bin $APP_DIR/models && chown sgweb:sgweb $APP_DIR/models"
  scp -q -i "$KEY" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR     "$WEB/../exporter/publish/sg-datexport" "$HOST:$APP_DIR/bin/sg-datexport"
  $SSH "$HOST" "chmod +x $APP_DIR/bin/sg-datexport && chown -R sgweb:sgweb $APP_DIR/bin"
fi

echo "==> installing dependencies"
$SSH "$HOST" "$APP_DIR/venv/bin/pip install --quiet --upgrade pip && \
              $APP_DIR/venv/bin/pip install --quiet -r $APP_DIR/api/requirements.txt"

# ---------------------------------------------------------------------------------------------
# systemd + Caddy
# ---------------------------------------------------------------------------------------------
echo "==> installing systemd unit"
$SSH "$HOST" "cat > /etc/systemd/system/shadowgain-web.service" < "$WEB/deploy/shadowgain-web.service"

# DNS MUST EXIST BEFORE THE CADDY BLOCK GOES IN.
#
# Caddy provisions the certificate on first request via an ACME HTTP challenge, and that
# challenge is a request to my.shadowgain.com - which cannot arrive if the name does not
# resolve. Loading the block anyway does not break the apex site, but it does start a retry
# loop that fills the journal with failures and can trip Let's Encrypt rate limits on the
# domain. So the block is SKIPPED, loudly, rather than loaded hopefully.
#
# This is not hypothetical: on the first deploy the GoDaddy PAT in credentials.env was expired
# (401 on both auth schemes) and the A record could not be created.
if ! $SSH "$HOST" "getent hosts my.shadowgain.com >/dev/null 2>&1"; then
  echo "!! my.shadowgain.com does not resolve - SKIPPING the Caddy block."
  echo "!!"
  echo "!! Create the A record first:"
  echo "!!   curl -X PUT https://api.godaddy.com/v1/domains/shadowgain.com/records/A/my \\"
  echo "!!     -H \"Authorization: Bearer \$GODADDY_KEY\" -H 'Content-Type: application/json' \\"
  echo "!!     -d '[{\"data\":\"137.184.1.44\",\"ttl\":600}]'"
  echo "!!"
  echo "!! then re-run this script. The API itself is deployed and will be verified below on"
  echo "!! 127.0.0.1:8081 - it is simply not reachable from outside yet."
  SKIP_CADDY=1
else
  SKIP_CADDY=0
fi

if [ "$SKIP_CADDY" = 0 ]; then
echo "==> updating Caddy site block"
# Replace only the managed block. The apex shadowgain.com config lives in the same file and must
# survive untouched - hence markers and an awk splice rather than a whole-file overwrite.
$SSH "$HOST" "cat > /tmp/my.caddy" < "$WEB/deploy/Caddyfile.my"
$SSH "$HOST" 'bash -s' <<'REMOTE'
set -euo pipefail
CF=/etc/caddy/Caddyfile

cp "$CF" "$CF.bak.$(date +%s)"

# Strip any previous managed block, then append the new one.
awk '
  /^# >>> shadowgain-web \(managed by web-deploy\.sh\)$/ { skip=1 }
  !skip { print }
  /^# <<< shadowgain-web$/ { skip=0 }
' "$CF" > /tmp/caddyfile.new

{
  echo ""
  echo "# >>> shadowgain-web (managed by web-deploy.sh)"
  cat /tmp/my.caddy
  echo "# <<< shadowgain-web"
} >> /tmp/caddyfile.new

# Validate BEFORE replacing. A malformed block would take shadowgain.com down with it, and
# `caddy validate` catches that while the live config is still the old one.
caddy validate --config /tmp/caddyfile.new --adapter caddyfile >/dev/null

mv /tmp/caddyfile.new "$CF"
systemctl reload caddy
echo "    Caddy reloaded"
REMOTE
fi

echo "==> restarting the API"
# `restart`, NOT `enable --now`.
#
# `--now` starts a STOPPED unit and does nothing at all to a running one. So every deploy after
# the first shipped new code to disk, reported "active", and went on serving the old process -
# silently, for 45 minutes, until a payload came back in the pre-fix shape and the file on disk
# said otherwise. `is-active` cannot catch this: the service was genuinely active, just not the
# version that had been deployed.
#
# `enable` is kept for boot persistence and split out, because it is idempotent and unrelated.
$SSH "$HOST" "systemctl daemon-reload && systemctl enable -q shadowgain-web && systemctl restart shadowgain-web && sleep 3 && systemctl is-active shadowgain-web"

# Prove the process is NEWER than the code it is supposed to be running. This is the check that
# would have caught the above immediately, so it is the one that stays.
$SSH "$HOST" 'bash -s' <<'REMOTE'
set -euo pipefail
started=$(date -d "$(systemctl show shadowgain-web -p ExecMainStartTimestamp --value)" +%s)
newest=$(find /opt/shadowgain-web/api -name '*.py' -printf '%T@\n' | sort -rn | head -1 | cut -d. -f1)

if [ "$started" -lt "$newest" ]; then
  echo "!! the running process predates the deployed code - it did not pick up this deploy"
  exit 1
fi

echo "    process started $((started - newest))s after the newest source file"
REMOTE

# ---------------------------------------------------------------------------------------------
# verify: the deploy is not done until the thing answers
# ---------------------------------------------------------------------------------------------
echo "==> verifying"

HEALTH=$($SSH "$HOST" "curl -sS --max-time 8 http://127.0.0.1:8081/api/health" || true)
echo "    local  /api/health -> $HEALTH"

case "$HEALTH" in
  *'"ok":true'*) ;;
  *) echo "!! the API is not answering healthily - check: journalctl -u shadowgain-web -n 50"; exit 1 ;;
esac

if [ "$SKIP_CADDY" = 1 ]; then
  echo "==> API deployed and healthy on 127.0.0.1:8081."
  echo "==> NOT yet public: create the DNS A record above, then re-run."
  exit 0
fi

# The public check runs FROM THE DROPLET, not from here.
#
# Running it locally conflates two unrelated things. On the first successful deploy this machine
# could not resolve my.shadowgain.com - not because the site was down, but because the LAN
# resolver (192.168.20.1) was still holding a negative cache entry from before the A record
# existed. The site was live and correctly served; the deploy reported failure anyway.
#
# The droplet's own resolver is the honest place to ask "is this reachable over HTTPS", because
# a failure there is genuinely the deploy's problem. `--resolve` pins the address so a slow
# recursive lookup cannot fail the check either.
PUBLIC=$($SSH "$HOST" "curl -sS --max-time 15 --resolve my.shadowgain.com:443:127.0.0.1 \
  -o /dev/null -w '%{http_code}' https://my.shadowgain.com/api/health" 2>/dev/null || echo "000")

echo "    public /api/health -> $PUBLIC"

if [ "$PUBLIC" != "200" ]; then
  echo "!! not reachable over HTTPS yet."
  echo "!! DNS resolves, so this is most likely Caddy still finishing the ACME challenge -"
  echo "!! give it a minute and re-check. The service itself is up and healthy."
  echo "!!   journalctl -u caddy -n 40"
  exit 1
fi

# Prove the certificate is real and not Caddy's internal fallback, which also answers 200 and
# would leave every browser showing a warning.
ISSUER=$($SSH "$HOST" "echo | openssl s_client -connect my.shadowgain.com:443 \
  -servername my.shadowgain.com 2>/dev/null | openssl x509 -noout -issuer 2>/dev/null" || true)

echo "    certificate -> ${ISSUER:-unknown}"

case "$ISSUER" in
  *"Let's Encrypt"*|*ZeroSSL*) ;;
  *) echo "!! that is not a public CA - browsers will warn. Check: journalctl -u caddy -n 40" ;;
esac

# The apex site shares the Caddyfile. If the splice broke it, that is worth knowing NOW rather
# than from a player.
APEX=$($SSH "$HOST" "curl -sS --max-time 10 -o /dev/null -w '%{http_code}' https://shadowgain.com/" || echo "000")
echo "    shadowgain.com (unchanged?) -> $APEX"

[ "$APEX" = "200" ] || { echo "!! the APEX SITE is not answering - the Caddy splice broke it"; exit 1; }

echo "==> deployed: https://my.shadowgain.com"
