#!/usr/bin/env bash
# The release a delta update starts from: the newest published release older than <version>. Prints its version and
# saves its manifest (Hearthpulse-Files-<previous>-win-x64.json) into <out-dir>, or prints nothing when there is no
# such release or it has no manifest (0.14.0 and older): then only the full setup is published.
# SIMULATE_PREVIOUS=true (trial runs only) prints 0.0.0 and saves nothing: build.sh makes a stand-in manifest.
# Usage: previous.sh <version> <out-dir>; needs gh with GH_TOKEN and GITHUB_REPOSITORY.
set -euo pipefail

VERSION="$1"
OUT="$2"
if [ "${SIMULATE_PREVIOUS:-}" = "true" ]; then
  echo "0.0.0"
  exit 0
fi

PREV="$(gh release list -R "$GITHUB_REPOSITORY" --exclude-drafts --exclude-pre-releases --limit 100 \
  --json tagName -q '.[].tagName' | python3 -c '
import re, sys
want = tuple(int(x) for x in sys.argv[1].split("."))
older = [tuple(int(x) for x in m.groups()) for t in sys.stdin
         if (m := re.fullmatch(r"v(\d+)\.(\d+)\.(\d+)", t.strip())) and tuple(int(x) for x in m.groups()) < want]
print(".".join(map(str, max(older))) if older else "")
' "$VERSION")"
[ -n "$PREV" ] || exit 0

# A release without the manifest is the expected case (0.14.0): no delta. Any other failure (network, rights) stops
# the build instead of quietly publishing a release without the delta it should have.
NAME="Hearthpulse-Files-$PREV-win-x64.json"
ASSETS="$(gh release view "v$PREV" -R "$GITHUB_REPOSITORY" --json assets -q '.assets[].name')"
grep -qxF "$NAME" <<< "$ASSETS" || exit 0
mkdir -p "$OUT"
gh release download "v$PREV" -R "$GITHUB_REPOSITORY" -p "$NAME" -D "$OUT" --clobber >&2
[ -s "$OUT/$NAME" ] || { echo "$NAME of v$PREV downloaded empty" >&2; exit 1; }
echo "$PREV"
