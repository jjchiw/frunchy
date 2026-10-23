#!/usr/bin/env bash
# Screenshots every page PageSnapshots saved, for a design review.
# Usage: bash tools/snapshots/shoot.sh <abs snapshot dir>   (the dir PageSnapshots wrote its html/ folder into)
set -euo pipefail

DIR="${1:?usage: shoot.sh <abs snapshot dir>}"
exec bash "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/chrome.sh" "$DIR" shoot.mjs
