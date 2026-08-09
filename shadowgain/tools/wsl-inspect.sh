#!/usr/bin/env bash
# Report what ACE-related leftovers remain in WSL, without removing anything.
#
#   wsl -d Ubuntu -- bash -c 'tr -d "\r" < "/mnt/c/Git Projects/Shadowgain/ACE/shadowgain/tools/wsl-inspect.sh" > /tmp/i.sh && bash /tmp/i.sh'
#
# A script file, because inline `bash -c` in this environment loses variables and quoting -
# every attempt to loop over paths inline came back with an empty variable. Read-only by
# design: it says what is there so a human decides what goes.
set -u

for d in "$HOME/ACE" "$HOME/ace-vm"; do
  echo "=== $d ==="

  if [ ! -e "$d" ]; then
    echo "    (absent)"
    continue
  fi

  echo "    size: $(du -sh "$d" 2>/dev/null | cut -f1)"
  echo "    contents:"
  ls "$d" 2>/dev/null | head -8 | sed 's/^/      /'

  if [ -d "$d/.git" ]; then
    echo "    git remote:  $(cd "$d" && git remote get-url origin 2>/dev/null || echo none)"
    echo "    uncommitted: $(cd "$d" && git status --porcelain 2>/dev/null | wc -l) file(s)"
    echo "    unpushed:    $(cd "$d" && git log --oneline --branches --not --remotes 2>/dev/null | wc -l) commit(s)"
  else
    echo "    (not a git repo)"
  fi
done

echo "=== docker images still present ==="
docker images --format '    {{.Repository}}:{{.Tag}}  {{.Size}}' 2>/dev/null

echo "=== anything still running ==="
docker ps -q 2>/dev/null | wc -l | sed 's/^/    running containers: /'
