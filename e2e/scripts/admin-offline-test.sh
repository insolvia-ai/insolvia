#!/usr/bin/env bash
# The admin portal's flow, offline — one command, no AWS, no Google, no
# password:
#
#   e2e/scripts/admin-offline-test.sh            # extra args go to playwright
#
# 1. contract/scripts/dev-test.sh brings up its throwaway .NET stack (fresh
#    Postgres, the API/admin/MCP hosts, the aux server publishing the run's
#    keys) and, through --exec, runs step 2 inside it instead of Jest;
# 2. the portal's Vite dev server starts on a port of its own
#    (E2E_ADMIN_PORTAL_PORT, 13100), so a developer's dev stack on :3100 and
#    :8090 is left alone;
# 3. playwright.admin.config.ts drives it, with Google's sign-in stood in for
#    in the browser (support/google-identity-stand-in.ts).
#
# Everything is stopped however it ends. Needs what the contract suite's
# offline target needs (Docker, the .NET SDK), plus `npm ci` in e2e/ and in
# apps/insolvia_admin/.
set -euo pipefail

E2E="$(cd "$(dirname "$0")/.." && pwd)"
REPO="$(cd "$E2E/.." && pwd)"
ADMIN_APP="$REPO/apps/insolvia_admin"

if [[ "${1:-}" != --inside-stack ]]; then
  [[ -d "$E2E/node_modules/@playwright/test" ]] || { echo "run npm ci in e2e/ first" >&2; exit 1; }
  [[ -d "$ADMIN_APP/node_modules/vite" ]] || { echo "run npm ci in apps/insolvia_admin/ first" >&2; exit 1; }
  args=""
  for a in "$@"; do args+=" $(printf '%q' "$a")"; done
  exec "$REPO/contract/scripts/dev-test.sh" --exec "$(printf '%q' "$0") --inside-stack$args"
fi
shift

PORT="${E2E_ADMIN_PORTAL_PORT:-13100}"
export E2E_ADMIN_PORTAL_URL="http://localhost:$PORT"
LOG="$(mktemp "${TMPDIR:-/tmp}/insolvia-admin-portal.XXXXXX")"
VITE_PID=""
cleanup() {
  if [[ -n "$VITE_PID" ]]; then kill "$VITE_PID" 2>/dev/null || true; wait "$VITE_PID" 2>/dev/null || true; fi
  rm -f "$LOG"
}
trap cleanup EXIT

(cd "$ADMIN_APP" && VITE_INSOLVIA_ENV=local exec npx vite --port "$PORT" --strictPort) >"$LOG" 2>&1 &
VITE_PID=$!
for _ in $(seq 1 150); do
  curl -fsS -o /dev/null "$E2E_ADMIN_PORTAL_URL" 2>/dev/null && break
  kill -0 "$VITE_PID" 2>/dev/null || { echo "the portal's dev server exited:" >&2; cat "$LOG" >&2; exit 1; }
  sleep 0.2
done
curl -fsS -o /dev/null "$E2E_ADMIN_PORTAL_URL" || { echo "the portal did not start:" >&2; cat "$LOG" >&2; exit 1; }
echo "admin portal ready on $E2E_ADMIN_PORTAL_URL"

cd "$E2E"
npx playwright test --config playwright.admin.config.ts "$@"
