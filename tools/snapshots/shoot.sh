#!/usr/bin/env bash
# Launches headless Chrome with a throwaway profile, runs shoot.mjs against it, then cleans up.
# Usage: bash tools/snapshots/shoot.sh <abs snapshot dir>   (the dir PageSnapshots wrote its html/ folder into)
set -euo pipefail

DIR="${1:?usage: shoot.sh <abs snapshot dir>}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NODE="${NODE:-$HOME/.nvm/versions/node/v24.22.1/bin/node}"
CHROME="${CHROME:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}"

[ -d "$DIR/html" ] || { echo "no $DIR/html: run the PageSnapshots test first" >&2; exit 1; }

PORT="$("$NODE" -e 'const s=require("net").createServer().listen(0,"127.0.0.1",()=>{console.log(s.address().port);s.close()})')"
PROFILE="$(mktemp -d "$DIR/chrome-profile.XXXXXX")"
CHROME_PID=""

cleanup() {
  if [ -n "$CHROME_PID" ]; then kill "$CHROME_PID" 2>/dev/null || true; wait "$CHROME_PID" 2>/dev/null || true; fi
  rm -rf "$PROFILE"
}
trap cleanup EXIT

"$CHROME" --headless=new --remote-debugging-port="$PORT" --user-data-dir="$PROFILE" \
  --no-first-run --disable-gpu --hide-scrollbars >/dev/null 2>&1 &
CHROME_PID=$!

for _ in $(seq 1 50); do
  curl -fs "http://127.0.0.1:$PORT/json/version" >/dev/null 2>&1 && break
  sleep 0.2
done

"$NODE" "$HERE/shoot.mjs" "$DIR" "$PORT"
