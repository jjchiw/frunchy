#!/usr/bin/env bash
# The /guide screenshots of Frunchy's own pages, from the pages PageSnapshots saved, into wwwroot/guide/.
# Usage: bash tools/snapshots/guide.sh <abs snapshot dir> [output dir, default wwwroot/guide]
set -euo pipefail

DIR="${1:?usage: guide.sh <abs snapshot dir> [output dir]}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="${2:-$HERE/../../wwwroot/guide}"
mkdir -p "$OUT"
exec bash "$HERE/chrome.sh" "$DIR" guide.mjs "$(cd "$OUT" && pwd)"
