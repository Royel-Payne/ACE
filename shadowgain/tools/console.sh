#!/usr/bin/env bash
SG_TOOL_HONORS_ENV=1 . "$(dirname "${BASH_SOURCE[0]}")/_isolation-guard.sh"   # 191: honours SG_HOST, so target is verifiable
# console.sh - run a command on the live ACE server console.
#
#   ./console.sh "acecommands"                    # list every console command
#   ./console.sh "serverstatus"
#   ./console.sh "set-accountaccess myacct 6"     # 6 = Admin
#   ./console.sh "sg-xptable 20"
#   ./console.sh -q "cmd"                         # run, don't print the log tail
#
# WHY THIS EXISTS, AND WHY IT LOOKS ODD
#
# ACE runs a console command thread on Console.ReadLine() (Command/CommandManager.cs),
# and the container has OpenStdin=true, so the prompt is reachable. But Tty=false, and
# without a TTY there is no Ctrl-P Ctrl-Q detach sequence - so a plain
# `docker attach ace-server` connects and never lets go, hanging the session. That is
# not Docker refusing access; it is the wrong tool for a TTY-less container.
#
# The two flags below are load-bearing. Do not drop them:
#   --sig-proxy=false   without it, stopping the attach client forwards the signal to
#                       the SERVER process - i.e. it can kill your world
#   timeout -s KILL     SIGKILL terminates only the local docker client and cannot be
#                       proxied anywhere
#
# Console commands run with session == null, which bypasses the AccessLevel check -
# so this can do things no in-game admin can, and there is no confirmation prompt.
# `stop-now` shuts the server down immediately. Read what you are sending.
#
# Output goes to the container log, not back over stdin, so this tails the log after.
set -euo pipefail

# 191: parameterised so the isolation guard can VERIFY the target. Defaults stay LIVE, so mainline
# behaviour is unchanged; the experiment worktree must pass SG_HOST/SG_KEY/SG_CONTAINER explicitly.
KEY="${SG_KEY:-C:/Users/Chris/.ssh/shadowgain_ed25519}"
HOST="${SG_HOST:-root@137.184.1.44}"
CONTAINER="${SG_CONTAINER:-ace-server}"
SSH="ssh -i $KEY -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR"

QUIET=0
if [ "${1:-}" = "-q" ]; then QUIET=1; shift; fi

CMD="${1:?usage: ./console.sh [-q] \"<console command>\"}"
TAIL="${2:-40}"

# The command is base64'd rather than interpolated into the remote shell string.
# ACE's ParseCommand DOES support quoted arguments - and it must be used for any
# multi-word value, because handlers read parameters[1], i.e. the FIRST TOKEN only
# (modifystring popup_motd Welcome to Shadowgain silently stores just "Welcome").
# But embedded double quotes would terminate the remote quoting and mangle the
# command. base64 sidesteps every layer of quoting between here and ACE's stdin.
CMD_B64=$(printf '%s' "$CMD" | base64 -w0)

$SSH "$HOST" "
  docker ps --format '{{.Names}}' | grep -q '^$CONTAINER\$' || { echo "$CONTAINER is not running"; exit 1; }
  MARK=\$(docker logs $CONTAINER 2>&1 | wc -l)
  printf '%s\n' \"\$(echo '$CMD_B64' | base64 -d)\" | timeout -s KILL 10 docker attach --sig-proxy=false $CONTAINER >/dev/null 2>&1 || true
  sleep 2
  if [ '$QUIET' = '0' ]; then
    docker logs $CONTAINER 2>&1 | tail -n +\$((MARK+1)) | tail -n $TAIL
  fi
"
