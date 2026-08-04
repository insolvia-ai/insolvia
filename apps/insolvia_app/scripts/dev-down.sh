#!/usr/bin/env bash
#
# Stop the Expo dev server — the counterpart to dev-up.sh.
#
# Unlike the compose services there is no container to remove; what this
# reclaims is the PORT. Expo runs through npx, which spawns a separate node
# process that outlives its parent — so a stray one keeps holding 3000 long
# after the terminal that started it is gone, and the next `dev-up.sh` fails
# to bind with an error that names the port rather than the cause.
#
# It kills whatever is listening on 3000, which is safe to state that bluntly
# only because the port is pinned to us rather than preferred: Cognito
# registers http://localhost:3000 as an exact-match origin (see dev-up.sh).
#
# Idempotent and safe to run when nothing is up: teardown that can fail is
# teardown you stop trusting.
#
set -euo pipefail

PORT=3000

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
