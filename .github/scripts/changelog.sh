#!/usr/bin/env bash
# Read and prepare CHANGELOG.md for a release.
#
#   changelog.sh notes VERSION       print VERSION's section, without its heading
#   changelog.sh unreleased          print the "## Unreleased" section (may print nothing)
#   changelog.sh prepare VERSION     turn "## Unreleased" into "## VERSION - <today, UTC>"
#   changelog.sh released            fail if a released section differs from the copy in its own tag
#                                    (vX.Y.Z:CHANGELOG.md): released notes are never edited
#
#   CHANGELOG   path of the file (default: CHANGELOG.md at the repository root)
#
# notes prints the lines under "## VERSION" or "## VERSION - date" up to the next "## ", with
# blank lines trimmed at both ends. The release workflow publishes that as the GitHub release's
# notes. It exits 1 when the section is missing or empty, so no release goes out with nothing to say.
#
# prepare puts "## VERSION - <date>" right under "## Unreleased": the entries that were
# Unreleased become the version's, and an empty "## Unreleased" stays on top for the next release.
# It refuses an empty Unreleased section and a version that already has a section.
#
# released catches the trap of a branch started before a release: when it is merged, git can put
# its new entries inside the section that release created, leaving Unreleased empty. Every release
# tag holds its own section (the release workflow makes sure of it), so each "## X.Y.Z - date" here
# must read exactly as in vX.Y.Z:CHANGELOG.md; new entries belong under Unreleased. Tags made before
# that rule (whose CHANGELOG.md doesn't have their section) are skipped. Needs the tags fetched.
#
# Ported from Caravana's tools/release.sh. The release workflow is .github/workflows/release.yml.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
changelog="${CHANGELOG:-$root/CHANGELOG.md}"

usage() {
  echo "usage: changelog.sh notes VERSION | unreleased | prepare VERSION | released" >&2
  exit 2
}

# A version is digits and dots with an optional -suffix, like the image tag it becomes.
check_version() {
  [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]] ||
    { echo "changelog: version $1 isn't X.Y.Z or X.Y.Z-suffix" >&2; exit 2; }
}

# The lines under the heading "## NAME" (exactly that, then the end of the line or " - date"),
# up to the next "## ", blank lines at either end trimmed.
section() {
  awk -v v="$1" '
    /^## / { on = ($2 == v && (NF == 2 || $3 == "-")); next }
    on' "$changelog" | sed -e '/./,$!d' | sed -e ':a' -e '/^\n*$/{$d;N;ba' -e '}'
}

notes() {
  local version="$1" text
  check_version "$version"
  text="$(section "$version")"
  if [ -z "$text" ]; then
    echo "changelog: $changelog has no section \"## $version - <date>\", or it is empty" >&2
    # The usual cause: the changes are still under "Unreleased". Say how to fix it.
    if grep -q '^## Unreleased' "$changelog"; then
      echo "changelog: run \".github/scripts/changelog.sh prepare $version\", commit the result, and tag that commit" >&2
    fi
    exit 1
  fi
  printf '%s\n' "$text"
}

unreleased() {
  local text
  text="$(section Unreleased)"
  if [ -n "$text" ]; then
    printf '%s\n' "$text"
  fi
}

prepare() {
  local version="$1" date tmp
  check_version "$version"
  if awk -v v="$version" '/^## / && $2 == v { found = 1 } END { exit !found }' "$changelog"; then
    echo "changelog: $changelog already has a section for $version" >&2
    exit 1
  fi
  if ! awk '/^## Unreleased/ { on = 1; next } /^## / { on = 0 } on && /[^[:space:]]/ { found = 1 } END { exit !found }' "$changelog"; then
    echo "changelog: the \"## Unreleased\" section of $changelog is missing or empty: nothing to release" >&2
    exit 1
  fi
  date="$(date -u +%Y-%m-%d)"
  tmp="$(mktemp)"
  awk -v v="$version" -v d="$date" '
    !done && /^## Unreleased[[:space:]]*$/ { print; print ""; print "## " v " - " d; done = 1; next }
    { print }' "$changelog" >"$tmp"
  cat "$tmp" >"$changelog"
  rm -f "$tmp"
  echo "changelog: \"## Unreleased\" is now \"## $version - $date\"; commit it, then tag v$version"
}

released() {
  local version tagged theirs mine bad=0 checked=0
  tagged="$(mktemp)"
  for version in $(awk '/^## [0-9]/ { print $2 }' "$changelog"); do
    git -C "$root" show "v$version:CHANGELOG.md" >"$tagged" 2>/dev/null || continue
    theirs="$(CHANGELOG="$tagged" "$0" notes "$version" 2>/dev/null || true)"
    [ -n "$theirs" ] || continue # a tag from before the rule: its CHANGELOG.md doesn't hold its section
    checked=$((checked + 1))
    mine="$(section "$version")"
    if [ "$mine" != "$theirs" ]; then
      echo "changelog: the released section \"## $version\" differs from the one in its tag v$version." >&2
      echo "changelog: released notes are never edited; move this branch's new entries under \"## Unreleased\"." >&2
      bad=1
    fi
  done
  rm -f "$tagged"
  [ "$bad" -eq 1 ] || echo "changelog: $checked released section(s) match their tags"
  exit "$bad"
}

case "${1:-}" in
released)
  [ $# -eq 1 ] || usage
  released
  ;;
notes)
  [ $# -eq 2 ] || usage
  notes "$2"
  ;;
unreleased)
  [ $# -eq 1 ] || usage
  unreleased
  ;;
prepare)
  [ $# -eq 2 ] || usage
  prepare "$2"
  ;;
*) usage ;;
esac
