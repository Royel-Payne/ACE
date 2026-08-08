#!/usr/bin/env bash
# skillsnap.sh - snapshot / diff a Shadowgain character's skill proficiency state.
#
#   ./skillsnap.sh save <Character> <label>   -> write a snapshot
#   ./skillsnap.sh diff <Character> <a> <b>   -> compare two snapshots
#
# Reads biota_properties_skill straight from ace_shard (authoritative), not chat.
#
# CAVEAT: the "xp" column is p_p / ExperienceSpent, which is incremented by BOTH
# Proficiency.OnSuccessUse (passive, usage-based gain) AND the player manually
# spending Unassigned Experience via HandleActionRaiseSkill. A +xp delta here is
# therefore NOT proof of passive gain. If the tester spent points, this conflates
# the two. The lastUsed column is safer: LastUsedTime and ResistanceAtLastCheck
# are written ONLY by Proficiency, so a change there does prove an award fired.
# For real per-award numbers, log inside Proficiency.OnSuccessUse instead.
set -euo pipefail

KEY="C:/Users/Chris/.ssh/shadowgain_ed25519"
HOST="root@137.184.1.44"
DIR="$(dirname "$0")/snaps"
mkdir -p "$DIR"

remote_query() {
  ssh -i "$KEY" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null \
      -o LogLevel=ERROR "$HOST" "cd /opt/ACE
RP=\$(grep '^MYSQL_ROOT_PASSWORD=' docker.env | cut -d= -f2)
docker exec ace-db mysql -uroot -p\"\$RP\" -N -B ace_shard -e \"$1\" 2>/dev/null"
}

case "${1:-}" in
  save)
    CHAR="${2:?character name}"; LABEL="${3:?label}"
    remote_query "SELECT s.type, s.s_a_c, s.level_From_P_P, s.p_p, s.last_Used_Time \
FROM \\\`character\\\` c JOIN biota_properties_skill s ON s.object_Id=c.id \
WHERE c.name='$CHAR' ORDER BY s.type;" > "$DIR/${CHAR}.${LABEL}.tsv"
    echo "saved -> $DIR/${CHAR}.${LABEL}.tsv ($(wc -l < "$DIR/${CHAR}.${LABEL}.tsv") rows)"
    ;;
  diff)
    CHAR="${2:?character}"; A="${3:?snapshot a}"; B="${4:?snapshot b}"
    python - "$DIR/${CHAR}.${A}.tsv" "$DIR/${CHAR}.${B}.tsv" "C:/Git Projects/Shadowgain/ACE/Source/ACE.Entity/Enum/Skill.cs" <<'PY'
import sys, re
def load(p):
    d={}
    for line in open(p):
        f=line.rstrip("\n").split("\t")
        if len(f)>=5: d[int(f[0])]=(int(f[1]),int(f[2]),int(f[3]),float(f[4]))
    return d
def names(p):
    txt=open(p,encoding='utf-8',errors='replace').read()
    body=txt.split('public enum Skill',1)[1].split('{',1)[1]
    out={}; i=0
    for line in body.splitlines():
        s=line.strip()
        if s.startswith('}'): break
        m=re.match(r'^([A-Za-z_][A-Za-z0-9_]*)\s*,',s)
        if m: out[i]=m.group(1); i+=1
    return out
a,b,nm=load(sys.argv[1]),load(sys.argv[2]),names(sys.argv[3])
SAC={0:'?',1:'Untrained',2:'Trained',3:'Specialized'}
rows=[]
for t in sorted(set(a)|set(b)):
    pa,pb=a.get(t),b.get(t)
    if pa is None or pb is None: continue
    dpp, drank, dsac = pb[2]-pa[2], pb[1]-pa[1], pb[0]!=pa[0]
    if dpp or drank or dsac or pb[3]!=pa[3]:
        rows.append((nm.get(t,f'#{t}'), SAC.get(pb[0]), pa[2], pb[2], dpp, pa[1], pb[1], drank, pb[3]))
if not rows:
    print("NO CHANGE between snapshots.")
else:
    print(f"{'skill':<20}{'class':<13}{'pp':>18}{'+pp':>9}{'rank':>10}{'+rank':>7}   lastUsed")
    for n,sac,ppa,ppb,dpp,ra,rb,dr,lu in rows:
        print(f"{n:<20}{sac:<13}{ppa:>8}->{ppb:<8}{dpp:>+9}{ra:>5}->{rb:<4}{dr:>+7}   {lu:.0f}")
PY
    ;;
  *) echo "usage: $0 save <Char> <label> | diff <Char> <labelA> <labelB>"; exit 1;;
esac
