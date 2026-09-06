#!/usr/bin/env bash
# Shadowgain 153 - publish vitaeum.shadowgain.com.
#
#   ./vitaeum-deploy.sh          # ship landing/vitaeum.html + install/refresh the Caddy block
#
# A sibling of site-deploy.sh rather than a flag on it. site-deploy.sh ships to
# /var/www/shadowgain and verifies every file back off https://shadowgain.com/<name>; vitaeum.html
# lives at a different host, in a different root, under a different filename (index.html), so
# folding it in would have meant a second destination and a second verify path inside a script
# whose value is that it does exactly one thing.
#
# Nothing here touches the game server, the API, or the apex site's content. The only shared
# resource is /etc/caddy/Caddyfile, which is spliced between markers and validated BEFORE it is
# moved into place - a malformed block would otherwise take shadowgain.com down with it.
#
# NEVER chmod a bare glob - see the header of site-deploy.sh for the outage that taught us.
set -euo pipefail

SRC="C:/Games/Claude AC/Shadowgain/landing/vitaeum.html"
SRCDIR="$(dirname "$SRC")"

# Files the page loads from its OWN web root, relative to SRCDIR. Everything else it references
# lives on CrimsonMage's github.io and needs no deploy at all - which is exactly the trap 225b
# walked into. The first version of this page hotlinked every image, so shipping one HTML file
# was the whole job; the moment the emblem became `src="vitaeum-logo.png"` the page started
# depending on a file this script did not carry. That failure is silent from here: the deploy is
# green, the HTML hash matches, and the only symptom is a broken image in someone else's browser.
# Hence the per-asset 200 check in the verify block - a hash of index.html cannot see it.
ASSETS=(vitaeum-logo.png)
BLOCK="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/Caddyfile.vitaeum"
KEY="C:/Users/Chris/.ssh/shadowgain_ed25519"
HOST="root@137.184.1.44"
DEST="/var/www/vitaeum"
NAME="vitaeum.shadowgain.com"
SSH="ssh -i $KEY -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR"

[ -f "$SRC" ]   || { echo "!! missing: $SRC"; exit 1; }
[ -f "$BLOCK" ] || { echo "!! missing: $BLOCK"; exit 1; }

# Same check the apex and portal deploys run: this goes to a public web root, and a leaked
# internal address or key is not something a later commit can take back.
if grep -lEi "192\.168\.|BEGIN [A-Z ]*PRIVATE KEY|password[\"' ]*[:=]|api[_-]?key[\"' ]*[:=]" "$SRC"; then
  echo "!! secret-ish string found in the file above - aborting"; exit 1
fi

# DNS BEFORE THE BLOCK. Caddy provisions the certificate through an HTTP challenge to this
# hostname, which cannot arrive if the name does not resolve; loading the block anyway starts a
# retry loop that fills the journal and eats Let's Encrypt rate limit on the whole domain.
# Asked FROM THE DROPLET - this machine's LAN resolver has cached a negative answer for a fresh
# shadowgain subdomain before, and reported a working deploy as broken (see web-deploy.sh).
if ! $SSH "$HOST" "getent hosts $NAME >/dev/null 2>&1"; then
  echo "!! $NAME does not resolve from the droplet - refusing to load the Caddy block."
  echo "!! Create the A record (GoDaddy) pointing at 137.184.1.44, then re-run."
  exit 1
fi
echo "==> $NAME resolves"

echo "==> shipping the page to $DEST/index.html"
$SSH "$HOST" "mkdir -p $DEST"
$SSH "$HOST" "cat > $DEST/index.html" < "$SRC"

for a in "${ASSETS[@]}"; do
  [ -f "$SRCDIR/$a" ] || { echo "!! missing asset: $SRCDIR/$a"; exit 1; }
  echo "==> shipping asset $a"
  $SSH "$HOST" "cat > $DEST/$a" < "$SRCDIR/$a"
done

# Ownership by name; permissions by TYPE.
$SSH "$HOST" "chown -R caddy:caddy $DEST && \
  find $DEST -type d -exec chmod 755 {} + && \
  find $DEST -type f -exec chmod 644 {} +"

echo "==> updating the Caddy site block"
$SSH "$HOST" "cat > /tmp/vitaeum.caddy" < "$BLOCK"
$SSH "$HOST" 'bash -s' <<'REMOTE'
set -euo pipefail
CF=/etc/caddy/Caddyfile

cp "$CF" "$CF.bak.$(date +%s)"

# Strip any previous managed block, then append the new one. The shadowgain.com block and
# web-deploy.sh's my.shadowgain.com block live in the same file and must survive untouched.
awk '
  /^# >>> vitaeum \(managed by vitaeum-deploy\.sh\)$/ { skip=1 }
  !skip { print }
  /^# <<< vitaeum$/ { skip=0 }
' "$CF" > /tmp/caddyfile.new

{
  echo ""
  echo "# >>> vitaeum (managed by vitaeum-deploy.sh)"
  cat /tmp/vitaeum.caddy
  echo "# <<< vitaeum"
} >> /tmp/caddyfile.new

# Validate BEFORE replacing, while the live config is still the old one.
caddy validate --config /tmp/caddyfile.new --adapter caddyfile >/dev/null

mv /tmp/caddyfile.new "$CF"
systemctl reload caddy
echo "    Caddy reloaded"
REMOTE

echo "==> verifying"
rc=0

# From the droplet, with the address pinned: a failure there is genuinely this deploy's problem,
# where a failure from here could just be a stale negative cache on the LAN resolver.
#
# POLLED, not asked once. Caddy does not provision a certificate until the hostname is in its
# config AND something asks for it, so the first deploy of a new name has a whole ACME HTTP-01
# round trip - acquire lock, serve the challenge, Let's Encrypt validating from five vantage
# points, download the chain - sitting between `systemctl reload` and the first byte. That took
# three seconds on the day this script was written, and the original zero-wait check duly reported
# "DEPLOY HAD FAILURES" for a deploy that had succeeded completely. A first-time hostname is the
# one case this script exists for, so failing it spuriously is worse than waiting a minute.
#
# The loop runs ON THE DROPLET, not here: one ssh round trip instead of twenty, and the waiting
# happens next to the thing being polled.
echo "  waiting for https://$NAME/ (a new hostname provisions its certificate on the first request)"
code=$($SSH "$HOST" "bash -s $NAME" <<'REMOTE'
NAME="$1"
for i in $(seq 1 20); do
  c=$(curl -sS --max-time 10 --resolve "$NAME:443:127.0.0.1" \
        -o /dev/null -w '%{http_code}' "https://$NAME/" 2>/dev/null)
  [ "$c" = "200" ] && break
  sleep 3
done
# curl already prints 000 through -w when the transfer fails. Appending a `|| echo 000` fallback
# on top of that is what produced the nonsense `http 000000` in this script's first run.
echo "${c:-000}"
REMOTE
)
printf "  %-34s http %s\n" "https://$NAME/" "$code"

if [ "$code" != "200" ]; then
  # Sixty seconds is far past the ACME round trip, so this is no longer the race.
  echo "!! still not serving after 60s - that is not the certificate, something else is wrong."
  echo "!!   journalctl -u caddy -n 40"
  rc=1
else
  # Everything below needs a site that answers. Running them anyway after a failed first hit
  # reports three failures for one cause and buries the one that means something.

  # Served bytes must match what we shipped - a 200 from a stale or half-written file is still wrong.
  remote=$($SSH "$HOST" "curl -sS --max-time 20 --resolve $NAME:443:127.0.0.1 https://$NAME/" 2>/dev/null | sha256sum | cut -d' ' -f1)
  local=$(sha256sum "$SRC" | cut -d' ' -f1)
  if [ "$remote" = "$local" ]; then echo "  content matches local"
  else echo "!! content mismatch between the served page and $SRC"; rc=1; fi

  # Each asset by name. Byte count compared, not just the status code: Caddy serves this root with
  # file_server and no SPA fallback, so a missing file is a real 404 rather than a 200 of index.html
  # - but a truncated or half-written upload would still be a 200, and only the size catches that.
  for a in "${ASSETS[@]}"; do
    read -r ac asize <<<"$($SSH "$HOST" "curl -sS --max-time 20 --resolve $NAME:443:127.0.0.1       -o /dev/null -w '%{http_code} %{size_download}' https://$NAME/$a" 2>/dev/null)"
    want=$(wc -c < "$SRCDIR/$a" | tr -d ' ')
    if [ "$ac" = "200" ] && [ "$asize" = "$want" ]; then
      printf "  asset OK   %-22s 200  %s bytes
" "$a" "$asize"
    else
      printf "!! asset BAD %-22s http %s  %s bytes (local %s)
" "$a" "$ac" "$asize" "$want"; rc=1
    fi
  done

  # A real certificate, not Caddy's internal fallback - which also answers 200 and would leave every
  # browser showing a warning. HSTS includeSubDomains from the apex makes this load-bearing.
  ISSUER=$($SSH "$HOST" "echo | openssl s_client -connect $NAME:443 -servername $NAME 2>/dev/null \
    | openssl x509 -noout -issuer 2>/dev/null" || true)
  echo "  certificate -> ${ISSUER:-unknown}"
  case "$ISSUER" in
    *"Let's Encrypt"*|*ZeroSSL*) ;;
    *) echo "!! that is not a public CA - browsers will warn. Check: journalctl -u caddy -n 40"; rc=1 ;;
  esac
fi

# Both neighbours share the Caddyfile. If the splice broke either, know it now, not from a player.
for u in https://shadowgain.com/ https://my.shadowgain.com/api/health; do
  c=$($SSH "$HOST" "curl -sS --max-time 10 -o /dev/null -w '%{http_code}' $u" 2>/dev/null) || true
  [ -n "$c" ] || c=000
  printf "  %-34s http %s (unchanged?)\n" "$u" "$c"
  [ "$c" = "200" ] || { echo "!! the splice broke a neighbouring site"; rc=1; }
done

[ $rc -eq 0 ] && echo "==> deployed: https://$NAME" || echo "!! DEPLOY HAD FAILURES"
exit $rc
