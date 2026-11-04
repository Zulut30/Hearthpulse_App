#!/usr/bin/env bash
# CI: run=false when every file the push or pull request changed is documentation (docs/ or *.md), so the .NET jobs
# end green in seconds; run=true otherwise and whenever the change set is unknown (manual run, new branch, force-push).
# A push skips only on top of a commit CI passed on: otherwise a docs push over red code would turn CI green (and the
# commit before may still be running: run). The skill and pool checks read the Markdown files and always run.
# Needs BEFORE (push) or BASE_SHA (pull request), GH_TOKEN with actions: read.
set -u
case "${GITHUB_EVENT_NAME:-}" in
  push) base="${BEFORE:-}" ;;
  pull_request) base="${BASE_SHA:-}" ;;
  *) base="" ;;
esac
run=true
if [ -n "$base" ] && ! [[ "$base" =~ ^0+$ ]] && git fetch --no-tags --depth=1 -q origin "$base" 2>/dev/null; then
  if files=$(git diff --name-only FETCH_HEAD HEAD) && [ -n "$files" ] && ! grep -qvE '^docs/|\.md$' <<< "$files"; then
    passed=0
    if [ "$GITHUB_EVENT_NAME" = "pull_request" ]; then
      passed=1
    else
      passed=$(gh api "repos/$GITHUB_REPOSITORY/actions/workflows/ci.yml/runs?head_sha=$base&status=success" \
        --jq '.total_count' 2>/dev/null || echo 0)
    fi
    if [ "${passed:-0}" -gt 0 ]; then
      run=false
      echo "Only documentation changed on top of a green commit: the .NET build and tests are skipped."
    else
      echo "Only documentation changed, but CI has not passed on $base: everything runs."
    fi
  fi
fi
echo "run=$run" >> "$GITHUB_OUTPUT"
