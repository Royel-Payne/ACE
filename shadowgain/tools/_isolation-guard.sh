#!/usr/bin/env bash
# _isolation-guard.sh - refuse to touch LIVE from an isolated experiment worktree.
#
# WHY THIS EXISTS
#
# 191 puts the rebalance experiment on its own branch in its own worktree. The branch is the merge
# gate, and deploy.sh pulls ONLY origin/shadowgain-usage-leveling onto the droplet - so experimental
# COMMITS cannot reach LIVE.
#
# That is not the whole hazard. deploy.sh ships BINARIES from a local `dotnet publish`, not from git,
# so running it here would push experimental code to the production shard while git looked clean.
# console.sh would change LIVE dials. Neither is caught by the branch, because neither reads it.
#
# FAIL-SAFE BY DEFAULT, AND THIS IS THE IMPORTANT PART
#
# The first version of this guard trusted $SG_HOST to decide whether the target was LIVE. That was
# wrong and was caught immediately: console.sh, bot-deploy.sh, sitedata.sh and site-deploy.sh all
# HARDCODE `HOST="root@137.184.1.44"` and ignore SG_HOST entirely. So exporting SG_HOST=<test> both
# satisfied the guard AND left the script pointing at production - a guard that reads a variable the
# script does not honour is worse than none, because it manufactures confidence.
#
# So: a tool must PROVE it honours the override by sourcing this with SG_TOOL_HONORS_ENV=1. Anything
# that does not is refused unconditionally. Unknown tools fail closed.
#
# HOW IT IS MERGE-SAFE
#
# The guard trips only when `.rebalance-experiment` exists at the repo root. That marker is
# deliberately UNTRACKED, so it lives in this worktree and nowhere else. If this branch is ever
# merged, the guard comes with it and is INERT - no marker, no refusal. Merging needs to remember
# nothing.
#
# Usage:
#   SG_TOOL_HONORS_ENV=1 . "$(dirname "${BASH_SOURCE[0]}")/_isolation-guard.sh"   # honours SG_HOST
#   . "$(dirname "${BASH_SOURCE[0]}")/_isolation-guard.sh"                        # refused outright

_sg_guard() {
    local root marker host why
    root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
    marker="$root/.rebalance-experiment"

    [ -f "$marker" ] || return 0          # not an isolated worktree - do nothing

    if [ "${SG_TOOL_HONORS_ENV:-0}" != "1" ]; then
        why="this tool HARDCODES the LIVE host and ignores SG_HOST, so its target cannot be verified"
    else
        host="${SG_HOST:-root@137.184.1.44}"
        case "$host" in
            *137.184.1.44*|*shadowgain.com*) why="the target resolves to LIVE ($host)" ;;
            *) return 0 ;;                # verified non-LIVE target - allow
        esac
    fi

    echo ""                                                                        >&2
    echo "!! REFUSED - ISOLATED REBALANCE WORKTREE (Task.md 191)"                   >&2
    echo "!!   worktree : $root"                                                    >&2
    echo "!!   reason   : $why"                                                     >&2
    echo "!!"                                                                       >&2
    echo "!! Nothing here reaches LIVE except by Chris's explicit go."               >&2
    echo "!! Run LIVE tooling from the MAINLINE worktree instead:"                   >&2
    echo "!!   C:/Git Projects/Shadowgain/ACE"                                       >&2
    echo ""                                                                          >&2
    exit 1
}

_sg_guard
