#!/usr/bin/env bash
# Shadowgain landing-site deploy: ship static files to the Caddy web root.
#
#   ./site-deploy.sh                 # ship every page + asset in landing/
#   ./site-deploy.sh index.html      # ship just these files
#
# Exists because this deploy used to be a tar|ssh one-liner typed by hand, and in 032
# the hand-typed version carried `chmod 644 /var/www/shadowgain/*`. That glob also
# matched the `data/` DIRECTORY, and 644 on a directory strips its execute bit - so
# Caddy could no longer traverse into it and every JSON feed 404'd. The pages still
# rendered (top-level files were fine), which made it look like a feed outage rather
# than a permissions mistake. sitedata.sh kept writing into data/ throughout, because
# root ignores permission bits; the data was current the whole time, just unreadable.
#
# Hence the rule below: NEVER chmod a bare glob. Directories need 755, files need 644,
# and `find -type` is the only way to say that without depending on what happens to be
# sitting in the directory today.
set -euo pipefail

SRC="C:/Games/Claude AC/Shadowgain/landing"
KEY="C:/Users/Chris/.ssh/shadowgain_ed25519"
HOST="root@137.184.1.44"
DEST="/var/www/shadowgain"
SSH="ssh -i $KEY -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR"

# Everything meant to be publicly served. og.svg / discord-icon.svg are SOURCES for the
# generated PNGs and are deliberately not shipped.
DEFAULT_FILES=(index.html setup.html tuning.html honourroll.html
               favicon.svg og-image.png discord-icon-512.png)

cd "$SRC"
FILES=("$@"); [ ${#FILES[@]} -eq 0 ] && FILES=("${DEFAULT_FILES[@]}")

for f in "${FILES[@]}"; do
  [ -f "$f" ] || { echo "!! missing: $f"; exit 1; }
done

# Credentials must never reach a public page. Cheap to check, expensive to miss.
if grep -lEi "192\.168\.|BEGIN [A-Z ]*PRIVATE KEY|password[\"' ]*[:=]|api[_-]?key[\"' ]*[:=]" "${FILES[@]}" 2>/dev/null; then
  echo "!! secret-ish string found in the files above - aborting"; exit 1
fi

echo "==> shipping ${#FILES[@]} file(s) to $DEST"
tar -czf - "${FILES[@]}" | $SSH "$HOST" "tar -xzf - -C $DEST"

# Ownership by name; permissions by TYPE. The -type split is the whole point - see header.
echo "==> fixing ownership + permissions (dirs 755, files 644)"
$SSH "$HOST" "chown -R caddy:caddy $DEST && \
  find $DEST -type d -exec chmod 755 {} + && \
  find $DEST -type f -exec chmod 644 {} +"

echo "==> verifying served bytes match local"
rc=0
for f in "${FILES[@]}"; do
  code=$(curl -s -o /dev/null -w '%{http_code}' "https://shadowgain.com/$f")
  remote=$(curl -s "https://shadowgain.com/$f" | sha256sum | cut -d' ' -f1)
  local=$(sha256sum "$f" | cut -d' ' -f1)
  if [ "$code" = "200" ] && [ "$remote" = "$local" ]; then
    printf "  OK    %-24s %s\n" "$f" "$code"
  else
    printf "  FAIL  %-24s http %s %s\n" "$f" "$code" \
      "$([ "$remote" != "$local" ] && echo '(content mismatch)')"; rc=1
  fi
done

# The JSON feeds are what the permissions bug actually broke, and they are NOT part of
# the upload - they are written by sitedata.sh. Check them anyway: a deploy that leaves
# the pages perfect and the feeds 404 is the exact failure this script exists to catch.
echo "==> verifying the data feeds still serve (written by sitedata.sh, not by us)"
for f in data/status.json data/honourroll.json; do
  code=$(curl -s -o /dev/null -w '%{http_code}' "https://shadowgain.com/$f")
  [ "$code" = "200" ] && printf "  OK    %-24s 200\n" "$f" \
                      || { printf "  FAIL  %-24s http %s\n" "$f" "$code"; rc=1; }
done

[ $rc -eq 0 ] && echo "==> DEPLOY OK" || echo "!! DEPLOY HAD FAILURES"
exit $rc
