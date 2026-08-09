#!/usr/bin/env bash
# Stand up a LOCAL clone of Shadowgain inside WSL, from the repo plus a DB dump.
#
#   wsl -d Ubuntu -- bash "/mnt/c/Git Projects/Shadowgain/ACE/shadowgain/tools/local-clone.sh"
#
# TWO JOBS, and the second is the one that outlives this week:
#
#   1. A crash box for the Holtburg investigation (068/084) - somewhere to break things
#      destructively without a real player noticing. There are real players now.
#   2. Proof that Shadowgain can be REBUILT. The code lives in git and can always be
#      recompiled; the characters live in exactly one MySQL instance on one droplet, and
#      before this there was no copy of them anywhere else. This script is the rehearsal
#      of the restore, which is the only part of a backup that is ever actually in doubt.
#
# WHAT IT DOES NOT TOUCH: the production droplet, and the other local ACE VM at
# 192.168.20.102. Everything here is a fresh stack in WSL with its own credentials.
set -euo pipefail

REPO="/mnt/c/Git Projects/Shadowgain/ACE"
BACKUPS="/mnt/c/Games/Claude AC/Shadowgain/backups"
DATS="/mnt/c/Games/Asheron's Call"
WORK="$HOME/shadowgain-local"

# Local-only, and deliberately NOT production's. A test box that shares credentials with
# the live server is a way to lose the live server by accident.
#
# FIXED, not generated. This was `localonly-$(date +%s)` and that was a bug: MySQL sets the
# root password only when the data volume is FIRST initialised, so every re-run wrote a new
# password into docker.env that the existing database had never heard of, and the server
# died on connect. Deterministic here, and the volume is torn down below anyway.
DBPASS="shadowgain-local-only"

echo "==> preparing $WORK"
mkdir -p "$WORK"
cd "$WORK"

cp "$REPO/docker-compose.yml" "$REPO/Dockerfile.fast" .
rm -rf publish-linux && cp -r "$REPO/publish-linux" .

# --- env -------------------------------------------------------------------------------
# Mirrors production's docker.env, with local credentials and NO world download: the world
# comes from our dump, so the container must not go and fetch a different one.
cat > docker.env <<EOF
PUID=1000
PGID=1000
TZ=Etc/UTC
MYSQL_ROOT_HOST=%
MYSQL_ROOT_PASSWORD=$DBPASS
MYSQL_USER=ace
MYSQL_PASSWORD=$DBPASS
MYSQL_DATABASE=ace_world
ACE_WORLD_NAME=ShadowgainLocal
ACE_DAT_FILES_DIRECTORY=/ace/Dats
ACE_SQL_AUTH_DATABASE_NAME=ace_auth
ACE_SQL_AUTH_DATABASE_HOST=ace-db
ACE_SQL_AUTH_DATABASE_PORT=3306
ACE_SQL_SHARD_DATABASE_NAME=ace_shard
ACE_SQL_SHARD_DATABASE_HOST=ace-db
ACE_SQL_SHARD_DATABASE_PORT=3306
ACE_SQL_WORLD_DATABASE_NAME=ace_world
ACE_SQL_WORLD_DATABASE_HOST=ace-db
ACE_SQL_WORLD_DATABASE_PORT=3306
ACE_SQL_INITIALIZE_DATABASES=false
ACE_SQL_DOWNLOAD_LATEST_WORLD_RELEASE=false
ACE_NONINTERACTIVE_SETUP=true
EOF

# --- local override --------------------------------------------------------------------
# DATs are mounted read-only straight off the Windows drive - they are ~1.4GB and already
# on this machine for the client, so copying them would waste time and disk for nothing.
# container_name is OVERRIDDEN, and that is not cosmetic. The stock compose hardcodes
# `ace-db` / `ace-server`, which are the same names Chris's OTHER local ACE stack
# (project `ace`, ~/ace) already owns - so bringing this one up collided with his and
# disturbed a server he had deliberately shut down. Namespacing keeps the two stacks from
# ever touching each other again.
cat > docker-compose.local.yml <<'EOF'
services:
  ace-db:
    container_name: sglocal-db
    ports: !override
      - "127.0.0.1:3307:3306/tcp"

  ace-server:
    container_name: sglocal-server
    build:
      context: .
      dockerfile: Dockerfile.fast
    restart: "no"
    # 9010, NOT 9000. Chris's other local ACE stack owns 9000-9001/udp and came back up
    # twice during setup for reasons I could not pin down - so rather than keep fighting it
    # for a port, this one lives somewhere else entirely. Two stacks that never contend
    # cannot disturb each other, which matters more here than matching production's port.
    ports: !override
      - "9010:9000/udp"
      - "9011:9001/udp"
    volumes:
      - "/mnt/c/Games/Asheron's Call:/ace/Dats:ro"
EOF

COMPOSE="docker compose -f docker-compose.yml -f docker-compose.local.yml"

# Torn down INCLUDING volumes. This script is a restore rehearsal - if it reuses a database
# that is already populated, it proves nothing, and a half-restored volume from a failed run
# is worse than no volume at all.
echo "==> tearing down any previous local stack (volumes included)"
$COMPOSE down -v --remove-orphans 2>/dev/null || true

echo "==> starting the database"
$COMPOSE up -d ace-db
for i in $(seq 1 60); do
  docker exec "$($COMPOSE ps -q ace-db)" mysqladmin ping -uroot -p"$DBPASS" >/dev/null 2>&1 && break
  sleep 2
done

DB=$($COMPOSE ps -q ace-db)
echo "    db container: $DB"

echo "==> restoring dumps (this is the part a backup is actually judged on)"
for db in ace_auth ace_shard ace_world; do
  f=$(ls -t "$BACKUPS"/${db}-*.sql.gz | head -1)
  echo "    $db  <-  $(basename "$f")"
  docker exec -i "$DB" mysql -uroot -p"$DBPASS" -e "DROP DATABASE IF EXISTS $db; CREATE DATABASE $db;" 2>/dev/null
  zcat "$f" | docker exec -i "$DB" mysql -uroot -p"$DBPASS" "$db" 2>/dev/null
done

echo "==> verifying the restore"
docker exec "$DB" mysql -uroot -p"$DBPASS" -N -B -e "
SELECT CONCAT('    ', RPAD(table_schema,11,' '), LPAD(COUNT(*),3,' '), ' tables')
FROM information_schema.tables WHERE table_schema LIKE 'ace_%' GROUP BY table_schema;" 2>/dev/null

docker exec "$DB" mysql -uroot -p"$DBPASS" -N -B ace_shard -e "
SELECT CONCAT('    characters restored: ', COUNT(*)) FROM \`character\` WHERE is_Deleted=0;" 2>/dev/null

echo "==> building and starting the server"
$COMPOSE up -d --build ace-server

echo "==> waiting for the world to open"
for i in $(seq 1 60); do
  if $COMPOSE logs ace-server 2>&1 | grep -q "World is now open"; then
    echo "    WORLD OPEN"; break
  fi
  sleep 3
done

$COMPOSE ps
echo
echo "==> connect the client to this box, not production:"
echo "    WSL address: $(hostname -I | awk '{print $1}'):9010"
echo "    (WSL2 forwards localhost for TCP; ACE is UDP, so use the address above if"
echo "     localhost:9000 does not answer - or enable mirrored networking in .wslconfig)"
