#!/usr/bin/env bash
# Shadowgain 216 - build the Suit Builder (WASM) and sync it into public/suit/.
#
#   ./suit-build.sh          # dotnet publish (Release, AOT) + mirror into public/suit/
#   ./suit-build.sh --sync   # skip the build, just re-mirror the last publish output
#
# The Suit Builder is NOT part of this repo. It lives in the Mag-Plugins fork
# (https://github.com/Royel-Payne/Mag-Plugins, branch `shadowgain`, LGPL 2.1 - see the
# license and change notices there), cloned at $MAG below. This script is the only
# bridge: it publishes the .NET-WebAssembly app there and mirrors the static output into
# public/suit/, which web-deploy.sh then ships like any other static file.
#
# The output is COMMITTED here (same convention as the other generated assets) so a web
# deploy never depends on having the fork + .NET SDK + wasm-tools workload installed.
#
# MIRROR, not copy-over: the .NET publish fingerprints its runtime files
# (dotnet.native.<hash>.wasm etc), so a plain copy would pile up stale hashes forever.
# Everything under public/suit/ is generated - nothing hand-authored lives there.
set -euo pipefail

MAG="/c/Git Projects/Mag-Plugins"
PROJ="$MAG/Mag-SuitBuilder-Wasm"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="$HERE/../public/suit"
OUT="$PROJ/bin/Release/net10.0/publish/wwwroot"

[ -d "$PROJ" ] || { echo "!! Mag-Plugins fork not found at $MAG - clone Royel-Payne/Mag-Plugins (branch shadowgain) first"; exit 1; }

if [ "${1:-}" != "--sync" ]; then
  echo "==> dotnet publish (Release + AOT - this takes a few minutes)"
  dotnet publish "$PROJ" -c Release
fi

[ -f "$OUT/index.html" ] || { echo "!! no publish output at $OUT"; exit 1; }

echo "==> mirroring into public/suit/"
rm -rf "$DEST"
mkdir -p "$DEST"
cp -r "$OUT/." "$DEST/"

echo "    $(find "$DEST" -type f | wc -l | tr -d ' ') files ($(du -sh "$DEST" | cut -f1) - .br/.gz siblings included; Caddy serves those precompressed)"
echo "==> done. Commit public/suit/ and run web-deploy.sh --assets-only to ship."
