#!/usr/bin/env bash
# Shadowgain: one-time setup of the nightly backup and the read-only pull user on the droplet
# (Task 229). Run as root ON THE DROPLET from a directory holding the other files in this folder.
#
#   ./backup-droplet-setup.sh                       install script + timer, create sgbackup
#   /opt/ACE/backup-droplet-setup.sh --add-key 'ssh-ed25519 AAAA... comment'
#                                                   authorise a pull key (read-only, nightly/ only)
#
# The pull key is wrapped in `restrict` + a forced rrsync -ro command, so whoever holds it can
# read /opt/ACE/backups/nightly and nothing else: no shell, no port forwarding, no writes, no
# other path. That is the whole security model of the off-site copy - the droplet never connects
# out to anything, and the home side holds a key that can only fetch.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
USER_NAME=sgbackup
ROOT=/opt/ACE/backups/nightly

if [ "${1:-}" = "--add-key" ]; then
  key="${2:?usage: --add-key 'ssh-ed25519 AAAA... comment'}"
  case "$key" in ssh-ed25519\ *|ssh-rsa\ *|ecdsa-*) ;; *) echo "not a public key line" >&2; exit 1;; esac
  ak=/home/$USER_NAME/.ssh/authorized_keys
  line="restrict,command=\"/usr/bin/rrsync -ro $ROOT\" $key"
  grep -qxF "$line" "$ak" 2>/dev/null && { echo "key already present"; exit 0; }
  echo "$line" >> "$ak"
  chown $USER_NAME:$USER_NAME "$ak"; chmod 600 "$ak"
  echo "added: $(echo "$key" | ssh-keygen -lf -)"
  exit 0
fi

id $USER_NAME >/dev/null 2>&1 || useradd --system --create-home --shell /bin/sh $USER_NAME
passwd -l $USER_NAME >/dev/null           # key-only; a forced command needs a real shell, not a password
install -d -o $USER_NAME -g $USER_NAME -m 700 /home/$USER_NAME/.ssh
touch /home/$USER_NAME/.ssh/authorized_keys
chown $USER_NAME:$USER_NAME /home/$USER_NAME/.ssh/authorized_keys; chmod 600 /home/$USER_NAME/.ssh/authorized_keys

[ "$HERE" = /opt/ACE ] || install -m 750 "$HERE/backup-nightly.sh" /opt/ACE/
[ "$HERE" = /opt/ACE ] || install -m 750 "$HERE/backup-droplet-setup.sh" /opt/ACE/   # kept for --add-key later
install -m 644 "$HERE/shadowgain-backup.service" /etc/systemd/system/
install -m 644 "$HERE/shadowgain-backup.timer"   /etc/systemd/system/
install -d -g $USER_NAME -m 750 "$ROOT"
systemctl daemon-reload
systemctl enable --now shadowgain-backup.timer
systemctl list-timers shadowgain-backup.timer --no-pager
