#!/usr/bin/env python3
"""Measure landed-hit rate from ACBridge telemetry.

    python hitrate.py                 # all characters
    python hitrate.py "Black Breath"  # one

WHY THIS EXISTS

The pacing simulator's weakest input is SG_SWINGS - how many attacks per kill
actually LAND. It matters because Proficiency.OnSuccessUse only fires on a
successful hit, so a miss trains nothing. Validating against Black Breath at
level 7 showed the default of 12 was badly wrong (~3 was right), and that error
inflated every accuracy-driven projection by ~2x. Endurance, which comes from
hits TAKEN and so doesn't care about your accuracy, matched exactly - which is
what identified the fault.

Skill ranks can be read from the shard DB after the fact. Hit-versus-miss exists
only in the moment, and ACBridge's chat capture is the only place it survives.

NOTE ON ACBRIDGE TIER 3: the plugin has a raw ServerDispatch hook that would give
exact combat events, but it is commented out - suspected of closing the client
after ~1min. That suspicion now looks like the same misattribution that blamed
our server code for what turned out to be the Utility Belt plugin. It still
should not be re-enabled: chat parsing yields the same numbers with zero risk to
the client, and there is no reason to spend client stability on data we already
have.
"""
import json
import os
import re
import sys
from collections import defaultdict

TELEMETRY = r"C:\Games\Claude AC\telemetry"

# Combat lines carry a proc prefix that anchored patterns would trip over:
#   "Recklessness! You cut Drudge Slinker for 10 points of slashing damage!"
#   "Reckless! Young Mosswart grazes your lower arm for 1 point..."
# Strip it before matching. (Measured against real ACBridge capture - my first
# pass anchored on ^You and silently counted 1 hit in a whole session.)
PROC = re.compile(r"^(?:Recklessness|Reckless|Sneak Attack|Dirty Fighting|Critical hit)!\s*", re.I)

# Outgoing: attacks I made. A landed hit always states the damage.
OUT_HIT = re.compile(r"^You \w+ .+ for \d+ points? of", re.I)

# "Drudge Slinker evaded your attack." - note "your", not "you"; the original
# \bevaded you\b never matched a single real miss.
OUT_MISS = re.compile(r"\bevaded your attack\b|\bdodge[sd]? your\b|^You miss\b|resists your spell", re.I)

# Kill messages are highly varied in AC and share no single stem, so this is a
# best-effort list plus the common "...by your assault" tail. Under-counting kills
# inflates landed-per-kill, so treat that column as approximate; the hit RATE
# beside it needs no kill detection and is the number to trust.
OUT_KILL = re.compile(
    r"by your assault|in twain|lifeless pulp|to (?:pieces|ribbons)|"
    r"torn (?:asunder|to)|smashed apart|is destroyed|slain|killed|"
    r"^You (?:cleave|dismember|destroy|obliterate|eviscerate|slaughter|annihilate)\b", re.I)

# Incoming: attacks made at me.
IN_HIT = re.compile(r"\b(?:grazes|hits|slashes|crushes|pierces|bludgeons|nicks|"
                    r"scratches|bites|claws|stings|smites|cuts|mangles)\s+your\b", re.I)
IN_MISS = re.compile(r"^You evaded\b", re.I)

# Bonus: the server announces every rank-up in chat, so progression can be read
# straight from telemetry without touching the shard DB.
SKILLUP = re.compile(r"^Your base (.+?) (?:skill )?is now (\d+)!", re.I)


def scan(path):
    c = defaultdict(int)
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            try:
                ev = json.loads(line)
            except ValueError:
                continue
            if ev.get("type") != "chat":
                continue
            t = (ev.get("text") or "").strip()
            if not t or t.startswith("You say,") or "tells you," in t:
                continue

            m = SKILLUP.match(t)
            if m:
                c["rankups"] += 1

            t = PROC.sub("", t)      # drop "Recklessness! " etc before matching

            if OUT_MISS.search(t):
                c["out_miss"] += 1
            elif OUT_KILL.search(t):   # search, not match: 'X is torn to ribbons by your assault' has no anchor at 0
                c["out_kill"] += 1
            elif OUT_HIT.match(t):
                c["out_hit"] += 1

            if IN_MISS.match(t):
                c["in_miss"] += 1
            elif IN_HIT.search(t):
                c["in_hit"] += 1
    return c


def main():
    want = sys.argv[1] if len(sys.argv) > 1 else None
    if not os.path.isdir(TELEMETRY):
        print("no telemetry directory - is ACBridge installed and has a character logged in?")
        return

    rows = []
    for name in sorted(os.listdir(TELEMETRY)):
        if want and name != want:
            continue
        p = os.path.join(TELEMETRY, name, "events.jsonl")
        if not os.path.isfile(p):
            continue
        c = scan(p)
        if sum(c.values()) == 0:
            continue
        rows.append((name, c))

    if not rows:
        print("no combat events found yet - play a little with ACBridge loaded, then re-run")
        return

    print(f"{'character':22}{'landed':>8}{'missed':>8}{'kills':>7}{'hit rate':>10}"
          f"{'swings/kill':>13}{'LANDED/kill':>12}")
    for name, c in rows:
        out_hit, out_miss, kills = c["out_hit"], c["out_miss"], c["out_kill"]
        att = out_hit + out_miss
        rate = out_hit / att if att else 0
        spk = att / kills if kills else 0
        lpk = out_hit / kills if kills else 0
        print(f"{name:22}{out_hit:>8}{out_miss:>8}{kills:>7}{rate:>9.0%}"
              f"{spk:>13.1f}{lpk:>12.1f}")

    print()
    print("SG_SWINGS for the pacing simulator is the LANDED/kill column.")
    print("Incoming (feeds SG_HITS, and drives Endurance):")
    for name, c in rows:
        tot = c["in_hit"] + c["in_miss"]
        if tot:
            print(f"  {name:22} took {c['in_hit']}, evaded {c['in_miss']} "
                  f"({c['in_hit']/tot:.0%} of incoming landed)")


if __name__ == "__main__":
    main()
