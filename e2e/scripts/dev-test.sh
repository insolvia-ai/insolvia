#!/usr/bin/env bash
#
# Run the E2E suite against THIS MACHINE's local stack instead of staging.
#
# CI runs these against deployed staging, post-deploy, in app-staging.yml,
# and the `smoke` project against production in app-prod.yml. Those remain
# the authoritative runs and this script does not change them.
#
# What it changes is the feedback loop. Staging only refreshes on a merge to
# main, so until this existed the only way to exercise a change to these
# specs — or to the sign-in path they cover — was to merge and wait for a
# deploy. The repo's own rule (root CLAUDE.md) is that everything must be
# testable locally unless there is a reason good enough to write down; there
# was no such reason here, only missing wiring.
#
# WHAT THIS POINTS AT
#   E2E_TARGET            dev — picks seeds/dev.json as who exists, "Local"
#                         as the footer label the smoke project expects
#   E2E_BASE_URL          http://localhost:3000   the app from scripts/dev-up.sh
#   E2E_API_URL           http://localhost:8080   the API from the same script
#   E2E_COGNITO_DOMAIN    this machine's dev pool, read from the .env that
#   E2E_COGNITO_CLIENT_ID scripts/dev-aws-setup.sh writes for the app
#
# `infra/envs/dev` registers `http://localhost:3000` as an exact-match Cognito
# web origin (infra/envs/dev/main.tf) — which is what makes a real sign-in
# round trip possible locally at all. The app's dev server is pinned to port
# 3000 for the same reason; Expo's own default of 8081 would not be allowed.
#
# NO CREDENTIALS HERE, EVER. The repo is public. The PASSWORD comes from the
# environment with no default, the same rule support/env.ts enforces. The
# ADDRESS is a different matter and comes from seeds/dev.json: it ends in
# `.test`, a reserved TLD that can never be a real mailbox, and keeping it in
# the fixture is what stops "who this machine seeded" and "who the suite signs
# in as" becoming two answers.
#
# THE ACCOUNT ALSO NEEDS A FIRM — scripts/dev-aws-seed.sh. Signing in is not
# enough for intake-persists.spec.ts, which drives /cases: without a firm the
# account resolves to no accessor and that route answers 403, so the spec fails
# on a missing case link and reads as an app regression rather than an
# unprovisioned test user. Staging seeds itself on every deploy; a laptop does
# not, which is why this is a step here and not there.
#
# This stays an explicit, separate invocation — it needs the stack running and
# a provisioned dev account, so it does not belong in any aggregate check. Same
# reasoning that keeps E2E out of the required PR checks (e2e/CLAUDE.md).
#
# Usage:
#   ./e2e/scripts/dev-test.sh                 # both projects, headless
#   ./e2e/scripts/dev-test.sh --headed        # watch it drive the browser
#   ./e2e/scripts/dev-test.sh --project smoke # only the unauthenticated half
#
# Anything after the script's own flag passes through to `playwright test`.
# The password comes from E2E_TEST_USER_PASSWORD if exported, else from
# ~/.config/insolvia/dev.env (the file dev-aws-seed.sh offers to write).
# The address comes from seeds/dev.json.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
E2E_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$E2E_DIR/.." && pwd)"

APP_ENV="$REPO_ROOT/apps/insolvia_app/.env"
BASE_URL="${E2E_BASE_URL:-http://localhost:3000}"
API_URL="${E2E_API_URL:-http://localhost:8080}"

die()  { printf '\033[1;31m[fail]\033[0m %s\n' "$*" >&2; exit 1; }
log()  { printf '\033[1;34m[e2e]\033[0m %s\n' "$*"; }

HEADED=0
PASSTHROUGH=()
for arg in "$@"; do
  case "$arg" in
    --headed) HEADED=1 ;;
    *) PASSTHROUGH+=("$arg") ;;
  esac
done

# ── Credentials: named, never echoed ────────────────────────────────────────
# Only the password. The ADDRESS comes from seeds/dev.json below — the same
# file ./scripts/dev-aws-seed.sh loads — so who this machine seeded and who the
# suite signs in as cannot drift apart. An exported E2E_TEST_USER_PASSWORD
# wins; otherwise the dev password file dev-aws-seed.sh maintains covers the
# local run, because the suite signs in as exactly the account it creates.
DEV_ENV_FILE="$HOME/.config/insolvia/dev.env"
if [[ -z "${E2E_TEST_USER_PASSWORD:-}" && -f "$DEV_ENV_FILE" ]]; then
  # shellcheck source=/dev/null
  source "$DEV_ENV_FILE"
  E2E_TEST_USER_PASSWORD="${DEV_USER_PASSWORD:-}"
fi
export E2E_TEST_USER_PASSWORD
[[ -n "${E2E_TEST_USER_PASSWORD:-}" ]] ||
  die "E2E_TEST_USER_PASSWORD is not set and $DEV_ENV_FILE provides no DEV_USER_PASSWORD.
       There is no default on purpose — this repo is public. Use the password you
       gave ./scripts/dev-aws-seed.sh for the account in seeds/dev.json (it offers
       to write that file), and make sure that script has run."

# Which target this run is aimed at. The suite derives the fixture
# (seeds/dev.json) and the expected footer label from it.
export E2E_TARGET=dev

# ── The dev pool's hosted domain and client ─────────────────────────────────
# Read from the .env that dev-aws-setup.sh writes rather than calling
# `terraform output`, which would need AWS credentials and the remote state for
# values already sitting on disk.
[[ -f "$APP_ENV" ]] || die "No $APP_ENV — run ./scripts/dev-aws-setup.sh first (it provisions this machine's dev pool and writes the app's env)."
read_env() { sed -n "s/^$1=//p" "$APP_ENV" | tail -1; }
if [[ -z "${E2E_COGNITO_DOMAIN:-}" ]]; then
  E2E_COGNITO_DOMAIN="$(read_env EXPO_PUBLIC_COGNITO_DOMAIN)"
  [[ -n "$E2E_COGNITO_DOMAIN" ]] || die "EXPO_PUBLIC_COGNITO_DOMAIN is not in $APP_ENV — re-run ./scripts/dev-aws-setup.sh."
fi
if [[ -z "${E2E_COGNITO_CLIENT_ID:-}" ]]; then
  E2E_COGNITO_CLIENT_ID="$(read_env EXPO_PUBLIC_COGNITO_CLIENT_ID)"
  [[ -n "$E2E_COGNITO_CLIENT_ID" ]] || die "EXPO_PUBLIC_COGNITO_CLIENT_ID is not in $APP_ENV — re-run ./scripts/dev-aws-setup.sh."
fi
export E2E_COGNITO_DOMAIN E2E_COGNITO_CLIENT_ID
export E2E_BASE_URL="$BASE_URL"
export E2E_API_URL="$API_URL"

# ── The app and the API have to actually be serving ─────────────────────────
# Without this the first symptom is a Playwright navigation timeout, which
# reads as a broken test rather than a stack that was never started.
curl -sf -o /dev/null --max-time 5 "$BASE_URL" \
  || die "Nothing is serving at $BASE_URL — start the stack with ./scripts/dev-up.sh (it runs the app on port 3000)."
curl -sf -o /dev/null --max-time 5 "$API_URL/health" \
  || die "Nothing is serving at $API_URL — start the stack with ./scripts/dev-up.sh (it runs the API on port 8080)."

if [[ ! -d "$E2E_DIR/node_modules" ]]; then
  log "installing e2e dependencies (own lockfile — not a workspace member)"
  (cd "$E2E_DIR" && npm ci)
fi

log "target      dev — $BASE_URL (app), $API_URL (api)"
log "sign-in via $E2E_COGNITO_DOMAIN"

cd "$E2E_DIR"
if [[ "$HEADED" -eq 1 ]]; then
  npx playwright test --headed "${PASSTHROUGH[@]+"${PASSTHROUGH[@]}"}"
else
  npx playwright test "${PASSTHROUGH[@]+"${PASSTHROUGH[@]}"}"
fi
