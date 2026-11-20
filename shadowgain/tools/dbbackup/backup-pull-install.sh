#!/usr/bin/env bash
# Shadowgain: install the off-site backup pull on a Linux machine (Task 229). Run as your normal
# user, not root, from a folder holding backup-pull.sh. Idempotent: re-running keeps the
# existing key and config.
#
#   ./backup-pull-install.sh [DEST]      DEST defaults to ~/Backups/shadowgain
#
# It creates a dedicated pull key, pins the droplet's host key, writes the config, installs a
# systemd USER timer (daily, catches up after the machine was off), and prints the public key.
# That one line gets authorised on the droplet with
#   backup-droplet-setup.sh --add-key '<line>'
# where it is restricted to a read-only fetch of the nightly backup folder.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
DEST="${1:-$HOME/Backups/shadowgain}"
KEY="$HOME/.ssh/shadowgain_backup_ed25519"
KH="$HOME/.config/shadowgain-backup.known_hosts"
CONF="$HOME/.config/shadowgain-backup.conf"
UNITS="$HOME/.config/systemd/user"

for c in rsync ssh ssh-keygen sha256sum systemctl; do
  command -v "$c" >/dev/null || { echo "missing: $c (install it first)" >&2; exit 1; }
done
[ -f "$HERE/backup-pull.sh" ] || { echo "backup-pull.sh must sit next to this script" >&2; exit 1; }
mkdir -p "$HOME/.ssh" "$HOME/.config" "$HOME/.local/bin" "$UNITS" "$DEST"
chmod 700 "$HOME/.ssh"

# No passphrase: the timer runs unattended. The droplet confines this key to a read-only rsync
# of the backup folder, which is what makes an unencrypted key acceptable here.
[ -f "$KEY" ] || ssh-keygen -q -t ed25519 -N "" -C "shadowgain-backup-pull@$(hostname)" -f "$KEY"

# Pinned host key of the LIVE droplet (SHA256:1sXKQeysqvMAvzzYGVl6e+M6HLGtVl38eZ572MbuNkg).
# If the droplet is ever rebuilt this line must be updated; until then the pull fails loudly
# rather than trusting a new host.
echo "137.184.1.44 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL574k0TSm+GSOAtGql4QzeTpXIobJSjgruQHQxvTFwF" > "$KH"

if [ ! -f "$CONF" ]; then
  {
    echo "SG_HOST=137.184.1.44"
    echo "SG_USER=sgbackup"
    echo "SG_KEY=$KEY"
    echo "SG_KNOWN_HOSTS=$KH"
    echo "SG_DEST=$DEST"
    echo "# Rotation, in days. The newest SG_KEEP_MIN are never deleted."
    echo "SG_KEEP_DAILY=30"
    echo "SG_KEEP_WEEKLY_DAYS=84"
    echo "SG_KEEP_MONTHLY_DAYS=366"
    echo "SG_KEEP_MIN=7"
    echo "SG_STALE_DAYS=2"
  } > "$CONF"
fi

install -m 755 "$HERE/backup-pull.sh" "$HOME/.local/bin/shadowgain-backup-pull"

printf '%s\n' \
  "[Unit]" \
  "Description=Fetch Shadowgain nightly backups from the droplet (pull only)" \
  "Wants=network-online.target" \
  "After=network-online.target" \
  "" \
  "[Service]" \
  "Type=oneshot" \
  "ExecStart=%h/.local/bin/shadowgain-backup-pull" \
  > "$UNITS/shadowgain-backup-pull.service"

printf '%s\n' \
  "[Unit]" \
  "Description=Fetch Shadowgain backups daily, an hour after the droplet's 18:30 UTC dump" \
  "" \
  "[Timer]" \
  "OnCalendar=*-*-* 19:30:00 UTC" \
  "Persistent=true" \
  "RandomizedDelaySec=10min" \
  "" \
  "[Install]" \
  "WantedBy=timers.target" \
  > "$UNITS/shadowgain-backup-pull.timer"

systemctl --user daemon-reload
systemctl --user enable --now shadowgain-backup-pull.timer

echo
echo "Installed. Backups will land in: $DEST/days/"
echo "So the timer also runs when you are not logged in:  sudo loginctl enable-linger $USER"
echo "Run once by hand after the key is authorised:      ~/.local/bin/shadowgain-backup-pull"
echo "Check later:  systemctl --user list-timers shadowgain-backup-pull.timer; cat $DEST/LAST_RESULT"
echo
echo "=== Public key to authorise on the droplet (one line) ==="
cat "$KEY.pub"
