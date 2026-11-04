#!/usr/bin/env bash
# CI: run=false when every file the push or pull request changed is documentation (docs/ or *.md), so the .NET jobs
# end green in seconds; run=true otherwise and whenever the change set is unknown (manual run, new branch, force-push).
# The skill and pool checks read the Markdown files and always run. Needs BEFORE (push) or BASE_SHA (pull request).
set -u
case "${GITHUB_EVENT_NAME:-}" in
  push) base="${BEFORE:-}" ;;
  pull_request) base="${BASE_SHA:-}" ;;
  *) base="" ;;
esac
run=true
if [ -n "$base" ] && ! [[ "$base" =~ ^0+$ ]] && git fetch --no-tags --depth=1 -q origin "$base" 2>/dev/null; then
  if files=$(git diff --name-only FETCH_HEAD HEAD) && [ -n "$files" ] && ! grep -qvE '^docs/|\.md$' <<< "$files"; then
    run=false
    echo "Only documentation changed: the .NET build and tests are skipped."
  fi
fi
echo "run=$run" >> "$GITHUB_OUTPUT"
