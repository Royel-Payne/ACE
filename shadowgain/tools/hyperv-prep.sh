#!/usr/bin/env bash
# Prepare a Hyper-V disk + cloud-init seed for the Shadowgain TEST vm.
#
#   wsl -d Ubuntu -- bash -c 'tr -d "\r" < "/mnt/c/Git Projects/Shadowgain/ACE/shadowgain/tools/hyperv-prep.sh" > /tmp/p.sh && bash /tmp/p.sh'
#
# Reuses the recipe already proven on this machine (~/ace-vm), which is how ACE-Server was
# built in minutes rather than hours. Only the hostname changes, plus a fresh copy of the
# cloud image so the original kit stays untouched.
#
# WHY NOT WSL FOR THE SERVER ITSELF: tried, and it lost. Docker ran fine, but the distro
# auto-terminates when idle (wiping /tmp mid-run), paths with spaces do not survive the
# Git Bash -> WSL boundary, and ACE is UDP where WSL2's forwarding is least reliable. WSL
# is still the right place to BUILD the disk - qemu-img and cloud-localds live here - it is
# just the wrong place to run the server.
set -euo pipefail

KIT="$HOME/ace-vm"
WORK="$HOME/sg-test-vm"
OUT="/mnt/c/VMs/sg-test"
HOSTNAME="sg-test"

echo "==> preparing $WORK"
rm -rf "$WORK"; mkdir -p "$WORK"
mkdir -p "$OUT"

# A DEDICATED key for this VM, not Chris's personal id_ed25519 that the original kit used.
# Chris's call, and the right one: a throwaway test box should not be reachable with the
# key that opens everything else, and revoking it later should not mean rotating his own.
# No passphrase, because this exists to be driven by automation on a local-only machine.
PUBKEY=$(cat /mnt/c/Users/Chris/.ssh/sgtest_ed25519.pub)

cat > "$WORK/user-data" <<EOF
#cloud-config
hostname: $HOSTNAME
fqdn: $HOSTNAME.local
manage_etc_hosts: true

users:
  - name: chris
    groups: [sudo, docker]
    shell: /bin/bash
    sudo: ['ALL=(ALL) NOPASSWD:ALL']
    lock_passwd: true
    ssh_authorized_keys:
      - $PUBKEY

ssh_pwauth: false
disable_root: true

# Docker installed at first boot, so the VM is ready to run the compose stack without a
# second provisioning pass.
package_update: true
packages:
  - docker.io
  - docker-compose-v2

growpart:
  mode: auto
  devices: ['/']
  ignore_growroot_disabled: false

runcmd:
  - [ systemctl, enable, --now, ssh ]
  - [ systemctl, enable, --now, docker ]
  - [ usermod, -aG, docker, chris ]
EOF

cat > "$WORK/meta-data" <<EOF
instance-id: $HOSTNAME-001
local-hostname: $HOSTNAME
EOF

echo "==> building the cloud-init seed"
if command -v cloud-localds >/dev/null 2>&1; then
  cloud-localds "$WORK/seed.iso" "$WORK/user-data" "$WORK/meta-data"
else
  # cloud-init only looks for a filesystem labelled CIDATA; any ISO tool will do.
  genisoimage -output "$WORK/seed.iso" -volid CIDATA -joliet -rock \
    "$WORK/user-data" "$WORK/meta-data" 2>/dev/null
fi
ls -la "$WORK/seed.iso" | awk '{print "    seed.iso  "$5" bytes"}'

echo "==> converting the cloud image to VHDX (fresh copy; the kit is untouched)"
cp "$KIT/noble-cloudimg.img" "$WORK/disk.qcow2"
qemu-img resize "$WORK/disk.qcow2" 60G >/dev/null 2>&1 || true
qemu-img convert -O vhdx -o subformat=dynamic "$WORK/disk.qcow2" "$OUT/sg-test.vhdx"

echo "==> copying the seed where Hyper-V can attach it"
cp "$WORK/seed.iso" "$OUT/seed.iso"

ls -la "$OUT" | tail -3 | awk '{print "    "$9"  "$5" bytes"}'
echo "==> disk ready at C:\\VMs\\sg-test"
