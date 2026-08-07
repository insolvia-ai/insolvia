#!/usr/bin/env bash
#
# Run the E2E suite against THIS MACHINE's local stack instead of staging.
#
# CI runs these against deployed staging, post-deploy, in app-staging.yml. That
# remains the authoritative run and this script does not change it.
#
# What it changes is the feedback loop. Staging only refreshes on a merge to
# main, so until now the only way to exercise a change to these specs — or to
# the sign-in path they cover — was to merge and wait for a deploy. The repo's
# own rule (root CLAUDE.md) is that everything must be testable locally unless
# there is a reason good enough to write down; there was no such reason here,
# only missing wiring.
#
# WHAT THIS POINTS AT
#   E2E_BASE_URL       http://localhost:3000   the app from scripts/dev-up.sh
#   E2E_COGNITO_DOMAIN this machine's dev pool, read from the .env that
#                      scripts/dev-aws-setup.sh writes
#
# `infra/envs/dev` registers `http://localhost:3000` as an exact-match Cognito
# web origin (infra/envs/dev/main.tf) — which is what makes a real sign-in
# round trip possible locally at all. The app's dev server is pinned to port
# 3000 for the same reason; Expo's own default of 8081 would not be allowed.
#
# NO CREDENTIALS HERE, EVER. The repo is public. The test user's email and
# password come from the environment with no defaults and no fixture file, the
# same rule support/env.ts enforces. Create the account with
# scripts/dev-aws-create-user.sh — there is no sign-up screen on any pool.
#
# This stays an explicit, separate invocation — it needs the stack running and
# a provisioned dev account, so it does not belong in any aggregate check. Same
# reasoning that keeps E2E out of the required PR checks (e2e/CLAUDE.md).
#
# Usage:
#   export E2E_TEST_USER_EMAIL=...  E2E_TEST_USER_PASSWORD=...
#   ./e2e/scripts/dev-test.sh                 # headless
#   ./e2e/scripts/dev-test.sh --headed        # watch it drive the browser
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
E2E_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$E2E_DIR/.." && pwd)"

APP_ENV="$REPO_ROOT/apps/insolvia_app/.env"
BASE_URL="${E2E_BASE_URL:-http://localhost:3000}"

die()  { printf '\033[1;31m[fail]\033[0m %s\n' "$*" >&2; exit 1; }
log()  { printf '\033[1;34m[e2e]\033[0m %s\n' "$*"; }

HEADED=0
[[ "${1:-}" == "--headed" ]] && HEADED=1

# ── Credentials: named, never echoed ────────────────────────────────────────
missing=()
[[ -n "${E2E_TEST_USER_EMAIL:-}"    ]] || missing+=(E2E_TEST_USER_EMAIL)
[[ -n "${E2E_TEST_USER_PASSWORD:-}" ]] || missing+=(E2E_TEST_USER_PASSWORD)
if [[ "${#missing[@]}" -ne 0 ]]; then
  die "Not set: ${missing[*]}
       These have no defaults on purpose — this repo is public.
       Create a dev account with ./scripts/dev-aws-create-user.sh, then export
       both variables in your shell before running this."
fi

# ── The dev pool's hosted domain ────────────────────────────────────────────
# Read from the .env that dev-aws-setup.sh writes rather than calling
# `terraform output`, which would need AWS credentials and the remote state for
# a value already sitting on disk.
if [[ -z "${E2E_COGNITO_DOMAIN:-}" ]]; then
  [[ -f "$APP_ENV" ]] || die "No $APP_ENV — run ./scripts/dev-aws-setup.sh first (it provisions this machine's dev pool and writes the app's env)."
  E2E_COGNITO_DOMAIN="$(sed -n 's/^EXPO_PUBLIC_COGNITO_DOMAIN=//p' "$APP_ENV" | tail -1)"
  [[ -n "$E2E_COGNITO_DOMAIN" ]] || die "EXPO_PUBLIC_COGNITO_DOMAIN is not in $APP_ENV — re-run ./scripts/dev-aws-setup.sh."
fi
export E2E_COGNITO_DOMAIN
export E2E_BASE_URL="$BASE_URL"

# ── The app has to actually be serving ──────────────────────────────────────
# Without this the first symptom is a Playwright navigation timeout, which
# reads as a broken test rather than a stack that was never started.
curl -sf -o /dev/null --max-time 5 "$BASE_URL" \
  || die "Nothing is serving at $BASE_URL — start the stack with ./scripts/dev-up.sh (it runs the app on port 3000)."

if [[ ! -d "$E2E_DIR/node_modules" ]]; then
  log "installing e2e dependencies (own lockfile — not a workspace member)"
  (cd "$E2E_DIR" && npm ci)
fi

log "target      $BASE_URL"
log "sign-in via $E2E_COGNITO_DOMAIN"

cd "$E2E_DIR"
if [[ "$HEADED" -eq 1 ]]; then
  npm run test:headed
else
  npm test
fi
