#!/usr/bin/env bash
#
# Stop the admin portal dev server — the counterpart to dev-up.sh.
#
# Unlike the compose services there is no container to remove; what this
# reclaims is the PORT. A Vite dev server can outlive the shell that started
# it, and 3100 matters more than most: it is the exact origin the dev Google
# OAuth client registers, so the strict-port dev-up.sh fails to bind rather
# than drifting somewhere sign-in cannot work.
#
# Idempotent and safe to run when nothing is up: teardown that can fail is
# teardown you stop trusting.
#
set -euo pipefail

PORT=3100

if ! command -v lsof >/dev/null 2>&1; then
  printf '\033[1;33m[warn]\033[0m lsof not found — cannot identify the listener on %s. Stop it by hand.\n' "$PORT" >&2
  exit 0
fi

pids="$(lsof -ti "tcp:$PORT" 2>/dev/null || true)"
if [[ -z "$pids" ]]; then
  printf '\033[1;34m[dev-down]\033[0m Nothing is listening on %s.\n' "$PORT"
  exit 0
fi

# TERM first, so the dev server gets to shut down on its own terms; KILL only
# for whatever ignores it.
# shellcheck disable=SC2086
kill $pids 2>/dev/null || true

remaining=""
for _ in 1 2 3 4 5 6 7 8 9 10; do
  sleep 0.5
  remaining="$(lsof -ti "tcp:$PORT" 2>/dev/null || true)"
  [[ -z "$remaining" ]] && break
done
if [[ -n "$remaining" ]]; then
  # shellcheck disable=SC2086
  kill -9 $remaining 2>/dev/null || true
fi

printf '\033[1;32m[ ok ]\033[0m Port %s is free.\n' "$PORT"
