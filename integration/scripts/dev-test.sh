#!/usr/bin/env bash
#
# Run the integration tier against THIS MACHINE's local backend.
#
#   ./integration/scripts/dev-test.sh              # the whole tier
#   ./integration/scripts/dev-test.sh -k documents # pytest args pass through
#
# The tier signs in to the shared dev Cognito pool and drives the .NET API
# that `scripts/dev-up.sh` serves on :8080 (backend/docker-compose.yml) over
# HTTP. CI runs the SAME tier against deployed staging (`api-staging.yml`,
# after the alias shift); this script is the local half.
#
# WHAT THIS POINTS AT — and none of it is a default in the suite itself:
#   INTEGRATION_TARGET          dev        picks seeds/dev.json as who exists
#   INTEGRATION_API_URL         http://localhost:8080
#   INTEGRATION_AUTH_POOL_ID    the shared dev pool, from backend/.env
#   INTEGRATION_AUTH_CLIENT_ID  its web client,      from backend/.env
#   INTEGRATION_AUTH_PORTAL_CLIENT_ID  its portal client (ADR 0023), from backend/.env
#   E2E_TEST_USER_PASSWORD      the dev account's password (see below)
#
# backend/.env is written by scripts/dev-aws-setup.sh. The local database must
# hold seeds/dev.json with the pool's real subjects (scripts/dev-aws-seed.sh),
# or the first signed-in test 403s and names the missing firm.
#
# LOCAL DIFFERS FROM STAGING IN ONE WAY: the compose API keeps its blob store
# and sealing keys as local twins (backend/docker-compose.yml), so the
# documents and vault specs exercise the routes, not this machine's bucket or
# KMS key. Staging is where those cross a real role.
#
# NEVER STAGING FROM A LAPTOP, for the reason e2e/scripts/dev-test.sh gives:
# that run needs staging's test password in a developer's shell for a job CI
# already does on every deploy. There is no --target flag on purpose.
#
# NO CREDENTIALS HERE, EVER. The password comes from E2E_TEST_USER_PASSWORD if
# exported, else from ~/.config/insolvia/dev.env — the file
# scripts/dev-aws-seed.sh offers to write. No AWS credentials are needed:
# sign-in is SRP over unsigned Cognito calls and everything else is HTTP.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TIER_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$TIER_DIR/.." && pwd)"
VENV="$TIER_DIR/.venv"
BACKEND_ENV="$REPO_ROOT/backend/.env"
DEV_ENV_FILE="$HOME/.config/insolvia/dev.env"
BASE_URL="${INTEGRATION_API_URL:-http://localhost:8080}"

die() {
  printf '\033[1;31m[fail]\033[0m %s\n' "$*" >&2
  exit 1
}
log() { printf '\033[1;34m[integration]\033[0m %s\n' "$*"; }

# ── The tier's own venv: three pins, converged on every run ─────────────────
if [[ ! -x "$VENV/bin/pytest" ]]; then
  log "creating $VENV"
  "${PYTHON:-python3}" -m venv "$VENV"
fi
"$VENV/bin/pip" install --quiet --disable-pip-version-check -r "$TIER_DIR/requirements.txt"

# ── The pool this machine signs in against ─────────────────────────────────
[[ -f "$BACKEND_ENV" ]] || die "No $BACKEND_ENV — run ./scripts/dev-aws-setup.sh first."
read_env() { sed -n "s/^$1=//p" "$BACKEND_ENV" | tail -1; }
POOL_ID="${INTEGRATION_AUTH_POOL_ID:-$(read_env AUTH_USER_POOL_ID)}"
CLIENT_ID="${INTEGRATION_AUTH_CLIENT_ID:-$(read_env AUTH_CLIENT_ID)}"
PORTAL_CLIENT_ID="${INTEGRATION_AUTH_PORTAL_CLIENT_ID:-$(read_env AUTH_PORTAL_CLIENT_ID)}"
[[ -n "$POOL_ID" && -n "$CLIENT_ID" && -n "$PORTAL_CLIENT_ID" ]] ||
  die "AUTH_USER_POOL_ID / AUTH_CLIENT_ID / AUTH_PORTAL_CLIENT_ID are not in $BACKEND_ENV — re-run ./scripts/dev-aws-setup.sh."

# ── Credentials: named, never echoed ────────────────────────────────────────
if [[ -z "${E2E_TEST_USER_PASSWORD:-}" && -f "$DEV_ENV_FILE" ]]; then
  # shellcheck source=/dev/null
  source "$DEV_ENV_FILE"
  E2E_TEST_USER_PASSWORD="${DEV_USER_PASSWORD:-}"
fi
[[ -n "${E2E_TEST_USER_PASSWORD:-}" ]] ||
  die "E2E_TEST_USER_PASSWORD is not set and $DEV_ENV_FILE provides no DEV_USER_PASSWORD.
       There is no default on purpose — this repo is public. Use the password you
       gave ./scripts/dev-aws-seed.sh for the account in seeds/dev.json."

# ── The API has to actually be serving ──────────────────────────────────────
curl -sf -o /dev/null --max-time 5 "$BASE_URL/health" ||
  die "Nothing is serving at $BASE_URL — start it with ./scripts/dev-up.sh (or ./backend/scripts/dev-up.sh)."

export INSOLVIA_INTEGRATION=1
export INTEGRATION_TARGET=dev
export INTEGRATION_API_URL="$BASE_URL"
export INTEGRATION_AUTH_POOL_ID="$POOL_ID"
export INTEGRATION_AUTH_CLIENT_ID="$CLIENT_ID"
export INTEGRATION_AUTH_PORTAL_CLIENT_ID="$PORTAL_CLIENT_ID"
export E2E_TEST_USER_PASSWORD

log "target   $BASE_URL (seeds/dev.json)"
log "sign-in  pool $POOL_ID"

cd "$TIER_DIR"
exec "$VENV/bin/pytest" "$@"
