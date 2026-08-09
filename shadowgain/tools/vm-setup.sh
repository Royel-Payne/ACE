#!/usr/bin/env bash
# Bring up the Shadowgain clone on the sg-test VM: restore the dumps, start the server.
#
# Runs ON the VM (~/shadowgain), not on Windows:
#   scp vm-setup.sh chris@<vm>:~/shadowgain/ && ssh chris@<vm> 'bash ~/shadowgain/vm-setup.sh'
#
# Everything here is deliberately ordinary - a Linux box, a LAN address, docker compose.
# That is the point: the WSL attempt failed on translation layers (9P paths, a distro that
# auto-terminates and wipes /tmp, UDP forwarding), none of which exist here.
set -euo pipefail

cd "$(dirname "$0")"

DBPASS="shadowgain-local-only"

cat > docker.env <<EOF
PUID=1000
PGID=1000
TZ=Etc/UTC
MYSQL_ROOT_HOST=%
MYSQL_ROOT_PASSWORD=$DBPASS
MYSQL_USER=ace
MYSQL_PASSWORD=$DBPASS
MYSQL_DATABASE=ace_world
ACE_WORLD_NAME=ShadowgainTest
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

cat > docker-compose.local.yml <<'EOF'
services:
  ace-db:
    container_name: sg-db
    ports: !override
      - "127.0.0.1:3306:3306/tcp"

  ace-server:
    container_name: sg-server
    build:
      context: .
      dockerfile: Dockerfile.fast
    restart: "no"
    # Bound to every interface ON PURPOSE - the whole reason this is a VM rather than WSL is
    # so a client on the LAN can reach it over UDP without a forwarding layer in the way.
    ports: !override
      - "9000:9000/udp"
      - "9001:9001/udp"
    volumes:
      - "./Dats:/ace/Dats:ro"
EOF

COMPOSE="docker compose -f docker-compose.yml -f docker-compose.local.yml"

echo "==> tearing down anything previous (volumes included - a restore must start clean)"
$COMPOSE down -v --remove-orphans 2>/dev/null || true

echo "==> starting the database"
$COMPOSE up -d ace-db
until docker exec sg-db mysqladmin ping -uroot -p"$DBPASS" >/dev/null 2>&1; do sleep 2; done
echo "    database ready"

echo "==> restoring dumps"
for db in ace_auth ace_shard ace_world; do
  f=$(ls -t backups/"$db"-*.sql.gz | head -1)
  echo "    $db  <-  $(basename "$f")"
  docker exec -i sg-db mysql -uroot -p"$DBPASS" \
    -e "DROP DATABASE IF EXISTS $db; CREATE DATABASE $db;" 2>/dev/null
  zcat "$f" | docker exec -i sg-db mysql -uroot -p"$DBPASS" "$db" 2>/dev/null
done

# DROP DATABASE takes the GRANTS with it. MySQL only grants the `ace` user on MYSQL_DATABASE
# at first init, so after a drop/recreate that user can reach nothing and ACE dies with
# "Access denied for user 'ace'@'%' to database 'ace_shard'". Production never hit this
# because its grants were made once and never destroyed - which is exactly the kind of state
# a restore rehearsal exists to catch. Re-granting is part of restoring, not an extra step.
echo "==> re-granting database access (DROP DATABASE removed it)"
docker exec sg-db mysql -uroot -p"$DBPASS" -e "
GRANT ALL PRIVILEGES ON ace_auth.*  TO 'ace'@'%';
GRANT ALL PRIVILEGES ON ace_shard.* TO 'ace'@'%';
GRANT ALL PRIVILEGES ON ace_world.* TO 'ace'@'%';
FLUSH PRIVILEGES;" 2>/dev/null

echo "==> restore verification"
docker exec sg-db mysql -uroot -p"$DBPASS" -N -B -e "
SELECT CONCAT('    ', RPAD(table_schema,11,' '), LPAD(COUNT(*),3,' '), ' tables')
FROM information_schema.tables WHERE table_schema LIKE 'ace_%' GROUP BY table_schema;" 2>/dev/null

docker exec sg-db mysql -uroot -p"$DBPASS" -N -B ace_shard -e "
SELECT CONCAT('    ', RPAD(name,16,' '), 'level ',
  COALESCE((SELECT value FROM biota_properties_int WHERE object_Id=c.id AND type=25),1))
FROM \`character\` c WHERE is_Deleted=0 ORDER BY name;" 2>/dev/null

echo "==> building and starting the server"
$COMPOSE up -d --build ace-server

echo "==> waiting for the world"
for i in $(seq 1 60); do
  if docker logs sg-server 2>&1 | grep -q "World is now open"; then
    echo "    WORLD OPEN"
    break
  fi
  sleep 3
done

docker ps --format '    {{.Names}}  {{.Status}}  {{.Ports}}'
echo
echo "==> point the client at:  $(hostname -I | awk '{print $1}'):9000"
