#!/usr/bin/env bash
#
# Pick the commit release.yml's `changes` job diffs against — the commit
# staging is known to be at — or say that nothing is known and every leg
# must deploy.
#
#   git rev-list --first-parent --max-count=200 "$HEAD_SHA^" |
#     REPO=owner/name BEFORE=<sha> ALL=false .github/scripts/release-diff-base.sh
#
# STDIN   candidate commits, newest first, one SHA per line.
# STDOUT  exactly one line: the chosen base SHA, or the word `all`.
# STDERR  which commits were checked and why the answer is what it is.
# EXIT    0 whenever an answer was reached (including `all`); 64 on bad usage.
#
# WHY NOT `github.event.before`. That is the previous tip of `main`, which
# says what THIS PUSH changed — not what staging is missing. When a leg of an
# earlier run was cancelled or failed (a concurrency-group eviction, a flaky
# deploy), the next push's diff does not mention that service, so it is never
# redeployed and staging drifts behind `main` with nothing red anywhere.
#
# THE BASE IS THE NEWEST ANCESTOR CARRYING A SUCCESSFUL
# `insolvia/staging-release` STATUS. `record` stamps that status only when
# every leg the run needed deployed (a cancelled or failed leg refuses it), so
# at that commit staging served every service at that commit's source. Diffing
# HEAD against it names every path that changed since staging was last whole,
# however many runs fell short in between. HEAD itself is not a candidate —
# pass the list from HEAD's parent — because a re-run of an already-green
# commit should not diff against itself.
#
# DECISION ORDER, first match wins:
#   1. ALL=true                      → all   (a dispatch that asked for it)
#   2. a candidate reads `success`   → that SHA
#   3. a status lookup FAILED        → BEFORE if usable, else all
#                                      (the API is down or the token lacks
#                                      `statuses: read`; the old behaviour is
#                                      a better guess than stopping)
#   4. no candidate reads `success`  → all   (fail open: deploying an
#                                      unchanged service costs minutes;
#                                      skipping a changed one costs a staging
#                                      that silently lies)
#
# THE LOOKUP. Default: the combined-status endpoint via `gh api`, which
# returns the LATEST status per context — a re-stamped commit reads right.
# Set STATUS_LOOKUP to a command to replace it; it is called with one SHA and
# must print that commit's `insolvia/staging-release` state (`success`,
# `pending`, `failure`, `error`, or `missing`) and exit non-zero only when it
# could not find out. That seam is what release-diff-base.test.sh drives.
#
# Bash 3.2 (macOS) compatible, so the test runs on a developer's machine.
set -euo pipefail

ZERO_SHA=0000000000000000000000000000000000000000
CONTEXT=insolvia/staging-release

log() { printf '%s\n' "$*" >&2; }

default_lookup() {
  : "${REPO:?REPO (owner/name) is required for the default status lookup}"
  gh api "repos/$REPO/commits/$1/status" \
    --jq "[.statuses[] | select(.context == \"$CONTEXT\")][0].state // \"missing\""
}

lookup() {
  if [ -n "${STATUS_LOOKUP:-}" ]; then
    "$STATUS_LOOKUP" "$1"
  else
    default_lookup "$1"
  fi
}

usable_before() {
  [ -n "${BEFORE:-}" ] && [ "$BEFORE" != "$ZERO_SHA" ]
}

if [ "$#" -ne 0 ]; then
  log "usage: <commits on stdin> | [ALL=…] [BEFORE=…] [REPO=…] [STATUS_LOOKUP=…] $0"
  exit 64
fi

if [ "${ALL:-false}" = "true" ]; then
  log "Diff base: none — the dispatch asked for every service (all=true)."
  echo all
  exit 0
fi

checked=0
while IFS= read -r sha; do
  [ -n "$sha" ] || continue
  checked=$((checked + 1))
  if ! state="$(lookup "$sha")"; then
    if usable_before; then
      log "::warning::Could not read $CONTEXT on $sha — falling back to the push's previous tip $BEFORE. A leg an earlier run missed will NOT be redeployed by this run."
      echo "$BEFORE"
    else
      log "::warning::Could not read $CONTEXT on $sha, and there is no previous tip to fall back to — deploying every service."
      echo all
    fi
    exit 0
  fi
  if [ "$state" = "success" ]; then
    log "Diff base: $sha — the newest ancestor staging released green ($checked commit(s) checked)."
    echo "$sha"
    exit 0
  fi
  log "  $sha: $CONTEXT is '$state' — staging was not whole there, looking further back."
done

log "Diff base: none — no $CONTEXT success among the $checked commit(s) checked. Deploying every service."
echo all
