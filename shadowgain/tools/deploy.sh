#!/usr/bin/env bash
# Shadowgain deploy: build locally, ship the output, restart the droplet.
#
#   ./deploy.sh                # build + ship + build image + restart
#   ./deploy.sh --stage        # everything EXCEPT the restart - server keeps running
#   ./deploy.sh --restart-only # graceful shutdown + swap to the staged image
#   ./deploy.sh --no-build     # ship whatever is already in publish-linux/
#   ./deploy.sh --git-only     # just pull the branch on the droplet (no binaries)
#
# --stage / --restart-only exist so a deploy can be scheduled around players. The
# docker image build takes longer than the container swap and does NOT need the
# server down (Dockerfile.fast only COPYs the published output), so staging first
# cuts the actual outage to a few seconds. Stage whenever; restart when everyone
# has had warning.
#
# Why: build locally, ship the output. On the old 1 vCPU / 2GB droplet, compiling .NET
# there took minutes and once wedged the box badly enough to need an API reboot. It is
# 2 vCPU / 4GB since 025 and could probably cope now, but locally still takes ~10s and
# the droplet's job is to RUN the server, not build it.
#
# The stock ./Dockerfile is left untouched as a fallback. If this path ever
# misbehaves, on the droplet:  cd /opt/ACE && docker compose up -d --build
set -euo pipefail

REPO="C:/Git Projects/Shadowgain/ACE"

# TARGET. Defaults are LIVE, so every existing invocation behaves exactly as before.
#
# These exist because until 2026-08-19 this script could only ever be pointed at LIVE, which meant
# a change to the DEPLOY PATH ITSELF had nowhere to be rehearsed - the first run of any edit was on
# the production shard with players in it. That is how the closed-world bug below survived: it was
# never wrong on a deploy anyone watched, only on one that followed Phase 1.
#
# TEST is not a clone of LIVE and the differences bite:
#   - the compose SERVICE is ace-server on both, but the CONTAINER is sg-server on TEST
#     (container_name in docker-compose.local.yml). `docker compose restart sg-server` fails with
#     "no such service"; `docker logs ace-server` fails with "no such container". Both names are
#     needed and they are not interchangeable.
#   - TEST has no docker-compose.fast.yml and lives in /home/chris/shadowgain, not /opt/ACE.
#
#   Rehearse a change to this script:
#     SG_HOST=chris@192.168.20.20 SG_KEY=~/.ssh/sgtest_ed25519 SG_CONTAINER=sg-server \
#     SG_COMPOSE_DIR=/home/chris/shadowgain SG_COMPOSE_FILES="-f docker-compose.yml -f docker-compose.local.yml" \
#       ./deploy.sh --restart-only
KEY="${SG_KEY:-C:/Users/Chris/.ssh/shadowgain_ed25519}"
HOST="${SG_HOST:-root@137.184.1.44}"
CONTAINER="${SG_CONTAINER:-ace-server}"          # docker ps / docker logs
SERVICE="${SG_SERVICE:-ace-server}"              # docker compose <service>
COMPOSE_DIR="${SG_COMPOSE_DIR:-/opt/ACE}"
COMPOSE_FILES="${SG_COMPOSE_FILES:--f docker-compose.yml -f docker-compose.fast.yml}"
SSH="ssh -i $KEY -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR"

# Countdown before the world closes. ACE broadcasts a warning to everyone online at
# fixed thresholds only - 30s, 15s, 10s, 5s (and 1m, 2m, 5m... for longer waits) - so
# a value that isn't near one of those gives fewer notices. 15 gets a player two
# warnings; 10 gets one plus the final "shutting down NOW".
SHUTDOWN_SECS="${SG_SHUTDOWN_SECS:-15}"
SHUTDOWN_MSG="${SG_SHUTDOWN_MSG:-Quick restart to apply skill and attribute fixes - back in under a minute.}"

BUILD=1
GIT_ONLY=0
STAGE_ONLY=0
RESTART_ONLY=0
for a in "$@"; do
  case "$a" in
    --no-build) BUILD=0 ;;
    --git-only) GIT_ONLY=1 ;;
    --stage) STAGE_ONLY=1 ;;
    --restart-only) RESTART_ONLY=1 ;;
    *) echo "unknown arg: $a"; exit 1 ;;
  esac
done

cd "$REPO"

if [ "$RESTART_ONLY" = "1" ]; then
  echo "==> --restart-only: using the already-staged image, no rebuild"
else

echo "==> droplet: pull branch (keeps Dockerfile.fast / compose files in sync)"
$SSH "$HOST" 'cd /opt/ACE && git fetch -q origin && git merge --ff-only origin/shadowgain-usage-leveling >/dev/null && git log --oneline -1'

if [ "$GIT_ONLY" = "1" ]; then echo "==> --git-only: done (no rebuild, no restart)"; exit 0; fi

if [ "$BUILD" = "1" ]; then
  echo "==> local publish (linux-x64)"
  dotnet publish ./Source/ACE.Server/ACE.Server.csproj \
    -c Release -r linux-x64 --no-self-contained -o ./publish-linux --nologo -v quiet
fi

echo "==> ship publish-linux ($(du -sh publish-linux | cut -f1)) over ssh"
# no rsync on this machine; tar+gzip the 51 files and unpack remotely
tar -czf - -C publish-linux . | $SSH "$HOST" 'mkdir -p /opt/ACE/publish-linux && tar -xzf - -C /opt/ACE/publish-linux'

echo "==> droplet: build image (server still running - Dockerfile.fast only COPYs)"
$SSH "$HOST" 'cd /opt/ACE && docker compose -f docker-compose.yml -f docker-compose.fast.yml build ace-server 2>&1 | tail -2'

if [ "$STAGE_ONLY" = "1" ]; then
  echo "==> STAGED. Server untouched and still running."
  echo "==> When ready:  ./deploy.sh --restart-only"
  exit 0
fi

fi   # end staging block

# Graceful shutdown via ACE's OWN console, not just SIGTERM.
#
# `docker stop -t 45` was never enough, and the 45s was fixing the wrong half of the
# problem. SIGTERM reaches ACE's OnProcessExit, which takes its IsRunningInContainer
# branch and returns almost immediately - the process exited in 0.787s with no shutdown
# log at all, so the extra grace period was simply unused. Nothing ever ran
# ServerManager.ShutdownServer(), which is what logs every player off (saving them),
# resyncs properties and stops the DB cleanly. Hence a rollback on every deploy.
#
# The container has OpenStdin=true, so ACE's command prompt is reachable and `stop-now`
# runs the real shutdown path. Two flags are load-bearing when injecting it:
#   --sig-proxy=false : otherwise stopping the attach client forwards a signal to the
#                       SERVER process - the very thing we are trying to avoid
#   timeout -s KILL   : SIGKILL hits only the local docker client and cannot be proxied
# (Ctrl-P Ctrl-Q cannot be used to detach because the container has Tty=false, which is
# why a plain `docker attach` hangs the session instead.)
echo "==> droplet: ACE graceful shutdown, ${SHUTDOWN_SECS}s warning"
# `shutdown` (not `stop-now`) so players get a countdown and an on-screen broadcast
# instead of vanishing mid-swing. The delay comes from ServerManager.ShutdownInterval,
# which is why set-shutdown-interval has to be sent first; any text after `shutdown`
# is broadcast to everyone online.
$SSH "$HOST" "
  if docker ps --format '{{.Names}}' | grep -q '^$CONTAINER\$'; then
    send() { printf '%s\n' \"\$1\" | timeout -s KILL 10 docker attach --sig-proxy=false $CONTAINER >/dev/null 2>&1 || true; }
    # Scope every check to lines produced AFTER this instant. A shutdown does NOT
    # recreate the container, so 'Exiting at' from previous deploys is still sitting in
    # the log and an unscoped grep matches stale output on the first poll.
    SINCE=\$(date -u +%Y-%m-%dT%H:%M:%S)
    # Clock starts HERE, before the sends - each send blocks up to 10s on its
    # 'timeout -s KILL 10 docker attach', so starting it afterwards under-reports the
    # wait by ~20s and makes the reported drain time meaningless against the countdown.
    START=\$(date +%s)

    send 'set-shutdown-interval $SHUTDOWN_SECS'
    sleep 1
    send 'shutdown $SHUTDOWN_MSG'

    # 'Exiting at' is the last line ShutdownServer writes before Environment.Exit, and
    # it is log.Info so it actually reaches the container log.
    #
    # It replaces 'World shut down|Shutting down world|Logging off all players', none of
    # which ever matched: the first two do not exist in the source at all, and 'Logging
    # off all players...' is log.DEBUG while the appender is capped at INFO. The old loop
    # therefore could never break early - it always burned its full 80s and always fell
    # through to the force-kill below. Verified empirically on TEST 2026-08-12: those
    # three markers occur 0 times across a complete shutdown, 'Exiting at' occurs twice.
    #
    # Budget = countdown + 5 minutes, because ACE's own stuck-player failsafe is 5
    # minutes; a shorter deadline can expire while ACE is still legitimately draining.
    # Wall-clock, NOT a count of sleeps. 'docker logs' on a large log takes seconds, so a
    # sleep-counter both under-reports elapsed time and silently inflates the deadline -
    # measured on TEST, 6s of counted sleep was ~27s of real time.
    DEADLINE=\$(( $SHUTDOWN_SECS + 300 ))
    DRAINED=0
    while [ \$(( \$(date +%s) - START )) -lt \$DEADLINE ]; do
      if ! docker ps --format '{{.Names}}' | grep -q '^$CONTAINER\$'; then
        echo \"    container exited after \$(( \$(date +%s) - START ))s\"; DRAINED=1; break
      fi
      if docker logs --since \"\$SINCE\" $CONTAINER 2>&1 | grep -q 'Exiting at'; then
        echo \"    drain complete after \$(( \$(date +%s) - START ))s\"; DRAINED=1; break
      fi
      sleep 2
    done

    if [ \$DRAINED -eq 0 ]; then
      echo \"    !! DRAIN DID NOT COMPLETE within \${DEADLINE}s - players may still be online.\"
      echo \"    !! Check 'serverstatus' before allowing the swap to force-kill them.\"
    fi

    # Progress + the stuck-player failsafe, which is the one warning worth seeing here.
    docker logs --since \"\$SINCE\" $CONTAINER 2>&1 \
      | grep -iE 'Waiting for [0-9]+|Waiting for world|Saving OfflinePlayers|failsafe|Exiting at' | tail -6
  fi"

# Timestamp captured BEFORE the swap so the startup checks below can be scoped to this run.
#
# They used to grep the whole log on the stated assumption that "docker compose up -d recreates the
# container, so its log starts empty". That is only true when the IMAGE CHANGED. Re-run
# --restart-only without a fresh --stage, or restart with a config-only change, and compose starts
# the SAME container with its log intact - so the grep matches a marker from a previous run.
#
# Caught on TEST 2026-08-19: --restart-only reported "WORLD OPEN" against a world with
# world_closed=1, matching a "World is now open" line that was three days and 4,776 log lines old.
# On LIVE the assumption has held only because --stage always changes the image first.
SWAP_SINCE=$($SSH "$HOST" 'date -u +%Y-%m-%dT%H:%M:%S')

echo "==> droplet: swap container to the staged image"
$SSH "$HOST" "cd $COMPOSE_DIR && \
  docker compose $COMPOSE_FILES stop -t 45 $SERVICE >/dev/null 2>&1 && \
  docker compose $COMPOSE_FILES up -d 2>&1 | tail -3"

echo "==> waiting for the world to come up"
# TWO OUTCOMES ARE BOTH SUCCESS, and treating only one as success was a real bug.
#
# DEPLOY.md Phase 1 sets world_closed=true on purpose, so that a staged deploy comes back up with
# the door shut until the dials are flipped. In that state ACE logs
#
#     World started and is currently Closed
#     To open world to players, use command: world open
#
# and never emits "World is now open" - because it is waiting for a human to type it. This loop
# used to grep only for "World is now open", so following the runbook made the tool burn its full
# 150s and exit 1 announcing a failure on a perfectly healthy deploy.
#
# Reproduced on TEST 2026-08-19 rather than reasoned about: with world_closed=1 the marker is
# provably absent and the old loop reports "world did not open".
#
# That false failure is why deploys got hand-run phase by phase instead of using this script - and
# hand-running is what produced the 178 incident, where a bespoke wait blocked on the container
# stopping, which never happens, leaving the world empty and players locked out for 13 minutes.
#
# The open-world path logs "...currently Closed and will open automatically..." first, so match the
# END STATE, not that phrase.
for i in $(seq 1 15); do
  # Grep the WHOLE log, not a tail. `docker compose up -d` recreates the container, so its
  # log starts empty - if the marker is present at all, it was produced by THIS run.
  #
  # It used to check `tail -5`, which broke the moment the 15s status timer started
  # injecting serverstatus output: the marker scrolled out of the window within seconds
  # and every deploy reported "world did not open" while the world was, in fact, open.
  if $SSH "$HOST" "docker logs --since '$SWAP_SINCE' $CONTAINER 2>&1 | grep -qi 'World is now open'" 2>/dev/null; then
    echo "==> WORLD OPEN"
    $SSH "$HOST" "cd $COMPOSE_DIR && docker compose ps --format \"{{.Name}}\t{{.Status}}\""
    exit 0
  fi
  if $SSH "$HOST" "docker logs --since '$SWAP_SINCE' $CONTAINER 2>&1 | grep -qi 'use command: world open'" 2>/dev/null; then
    echo "==> WORLD UP, AND DELIBERATELY CLOSED (world_closed is set)"
    echo "    This is a SUCCESSFUL start. Nothing is wrong and nothing is waiting on this script."
    echo "    Still to do by hand: DEPLOY.md Phase 5 (migration), Phase 6 (dials), then:"
    echo "        ./console.sh -q \"modifybool world_closed false\""
    echo "        ./console.sh -q \"resyncproperties\""
    echo "        ./console.sh -q \"world open\""
    $SSH "$HOST" "cd $COMPOSE_DIR && docker compose ps --format \"{{.Name}}\t{{.Status}}\""
    exit 0
  fi
  sleep 10
done

echo "!! world neither opened nor reported itself closed within 150s"
echo "!! this one IS a failure - check: docker logs $CONTAINER"
exit 1
