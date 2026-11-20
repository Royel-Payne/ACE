# Nightly backups and the off-site pull (Task 229)

Two halves, and the droplet never connects out to anything.

| where | what | schedule | keeps |
|---|---|---|---|
| LIVE droplet | `backup-nightly.sh` dumps `ace_shard`, `ace_auth`, `ace_world` into `/opt/ACE/backups/nightly/<YYYY-MM-DD>/` with `SHA256SUMS`, then rotates | `shadowgain-backup.timer`, 18:30 UTC (quietest player hour) | 7 nights + Sundays for 5 weeks |
| home Linux box | `backup-pull.sh` fetches new nights over SSH, verifies checksums, hard-links unchanged files, rotates | systemd **user** timer, 19:30 UTC, catches up after the machine was off | 30 nights, Sundays for 12 weeks, 1st of month for 12 months, newest 7 always |

Dumps use `--single-transaction` (no table locks, players don't notice) and are byte-stable
when nothing changed (`--skip-dump-date`, `gzip -n`), so an unchanged `ace_world` costs nothing
on the home side. Steady state at home is ~49 nights; at today's sizes (shard ~5 MB, world
~23 MB, auth ~13 KB) that's well under 1 GB.

## Security model

The home machine holds a dedicated key, `~/.ssh/shadowgain_backup_ed25519`. On the droplet it
is authorised for user `sgbackup` as

    restrict,command="/usr/bin/rrsync -ro /opt/ACE/backups/nightly" ssh-ed25519 ...

so it can read that one folder and nothing else. Verified 2026-11-20: a shell is refused, a
write is refused, and `..` paths out of the folder are refused. The droplet's host key is pinned
in `~/.config/shadowgain-backup.known_hosts`, so a rebuilt droplet makes the pull fail loudly
instead of trusting a new host.

## Install on the home Linux box

    mkdir -p ~/sg-backup && cd ~/sg-backup
    B=https://raw.githubusercontent.com/Royel-Payne/ACE/shadowgain-usage-leveling/shadowgain/tools/dbbackup
    curl -fsSLO $B/backup-pull.sh && curl -fsSLO $B/backup-pull-install.sh
    bash backup-pull-install.sh            # optional arg: destination, default ~/Backups/shadowgain
    sudo loginctl enable-linger $USER      # so the timer runs while logged out

The installer prints one public-key line. Authorise it on the droplet (as root):

    /opt/ACE/backup-droplet-setup.sh --add-key 'ssh-ed25519 AAAA... shadowgain-backup-pull@host'

Then run `~/.local/bin/shadowgain-backup-pull` once by hand.

## Checking on it

    # droplet
    cat /opt/ACE/backups/nightly/LAST_RESULT
    journalctl -u shadowgain-backup -n 20
    # home
    cat ~/Backups/shadowgain/LAST_RESULT
    systemctl --user list-timers shadowgain-backup-pull.timer
    journalctl --user -u shadowgain-backup-pull -n 20

The pull exits non-zero (systemd shows the unit failed) if the droplet's last backup did not
report OK, if the newest droplet backup is more than 2 days old, or if a checksum fails.

## Restoring

The dumps carry `CREATE DATABASE` / `USE` for their own name, so piping one straight into mysql
**replaces that database**. To inspect without touching anything, rewrite the name first:

    zcat ace_shard.sql.gz \
      | sed -E 's/^CREATE DATABASE (\/\*[^*]*\*\/ )?`ace_shard`/CREATE DATABASE \1`restorecheck_shard`/; s/^USE `ace_shard`;/USE `restorecheck_shard`;/' \
      | docker exec -i -e MYSQL_PWD="$P" sg-db mysql -uroot

A real LIVE restore follows DEPLOY.md: world closed and the server stopped first, because the
server holds characters in memory and flushes them over the DB (see the memory note on character
writes needing the world down).
