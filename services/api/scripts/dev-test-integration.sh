#!/usr/bin/env bash
#
# Run the API's INTEGRATION tier against THIS MACHINE's dev stack.
#
#   ./services/api/scripts/dev-test-integration.sh              # the whole tier
#   ./services/api/scripts/dev-test-integration.sh -k documents # pytest args pass through
#
# The unit tier (`scripts/dev-test.sh`, a bare `pytest`) runs the Flask app
# in-process over memory adapters. This tier does the opposite: it signs in
# to this machine's real Cognito pool and drives the API `scripts/dev-up.sh`
# is serving on :8080 over HTTP, so the rows land in this machine's real
# tables and the bytes in its real bucket. CI runs the SAME tier against
# deployed staging (`api-staging.yml`, after the alias shift); this script is
# the local half, and the repo's rule (root CLAUDE.md) is that the local half
# exists for everything.
#
# WHAT THIS POINTS AT — and none of it is a default in the suite itself:
#   INTEGRATION_TARGET          dev        picks seeds/dev.json as who exists
#   INTEGRATION_API_URL         http://localhost:8080
#   INTEGRATION_AUTH_POOL_ID    this machine's pool,   from services/api/.env
#   INTEGRATION_AUTH_CLIENT_ID  this machine's client, from services/api/.env
#   E2E_TEST_USER_PASSWORD      the dev account's password (see below)
#
# NEVER STAGING FROM A LAPTOP, for the reason e2e/scripts/dev-test.sh gives:
# that run needs staging's test password in a developer's shell for a job CI
# already does on every deploy. There is no --target flag on purpose.
#
# NO CREDENTIALS HERE, EVER. The password comes from E2E_TEST_USER_PASSWORD if
# exported, else from ~/.config/insolvia/dev.env — the file
# scripts/dev-aws-seed.sh offers to write — the same two sources the browser
# suite's wrapper reads, so the two local runs cannot disagree about who
# signs in. No AWS credentials are needed: sign-in is SRP over unsigned
# Cognito calls and everything else is HTTP to the API.
#
# THE ACCOUNT ALSO NEEDS A FIRM — scripts/dev-aws-seed.sh. Without one every
# route behind current_accessor() answers 403 and the suite's first test
# names the missing firm rather than reporting a regression.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
API_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
VENV="$API_DIR/.venv"
API_ENV="$API_DIR/.env"
DEV_ENV_FILE="$HOME/.config/insolvia/dev.env"
BASE_URL="${INTEGRATION_API_URL:-http://localhost:8080}"

die()  { printf '\033[1;31m[fail]\033[0m %s\n' "$*" >&2; exit 1; }
log()  { printf '\033[1;34m[integration]\033[0m %s\n' "$*"; }

[[ -x "$VENV/bin/pytest" ]] ||
  die "No API venv at $VENV — run ./services/api/scripts/dev-setup.sh first."

# ── The pool this machine signs in against ─────────────────────────────────
# From the .env dev-aws-setup.sh writes rather than `terraform output`, which
# would need AWS credentials and the remote state for values already on disk.
[[ -f "$API_ENV" ]] || die "No $API_ENV — run ./scripts/dev-aws-setup.sh first."
read_env() { sed -n "s/^$1=//p" "$API_ENV" | tail -1; }
POOL_ID="${INTEGRATION_AUTH_POOL_ID:-$(read_env AUTH_USER_POOL_ID)}"
CLIENT_ID="${INTEGRATION_AUTH_CLIENT_ID:-$(read_env AUTH_CLIENT_ID)}"
[[ -n "$POOL_ID" && -n "$CLIENT_ID" ]] ||
  die "AUTH_USER_POOL_ID / AUTH_CLIENT_ID are not in $API_ENV — re-run ./scripts/dev-aws-setup.sh."

# ── Credentials: named, never echoed ────────────────────────────────────────
if [[ -z "${E2E_TEST_USER_PASSWORD:-}" && -f "$DEV_ENV_FILE" ]]; then
  # shellcheck source=/dev/null
  source "$DEV_ENV_FILE"
  E2E_TEST_USER_PASSWORD="${DEV_USER_PASSWORD:-}"
fi
[[ -n "${E2E_TEST_USER_PASSWORD:-}" ]] ||
  die "E2E_TEST_USER_PASSWORD is not set and $DEV_ENV_FILE provides no DEV_USER_PASSWORD.
       There is no default on purpose — this repo is public. Use the password you
       gave ./scripts/dev-aws-seed.sh for the account in seeds/dev.json (it offers
       to write that file), and make sure that script has run."

# ── The API has to actually be serving ──────────────────────────────────────
curl -sf -o /dev/null --max-time 5 "$BASE_URL/health" ||
  die "Nothing is serving at $BASE_URL — start it with ./services/api/scripts/dev-up.sh (or ./scripts/dev-up.sh)."

export INSOLVIA_INTEGRATION=1
export INTEGRATION_TARGET=dev
export INTEGRATION_API_URL="$BASE_URL"
export INTEGRATION_AUTH_POOL_ID="$POOL_ID"
export INTEGRATION_AUTH_CLIENT_ID="$CLIENT_ID"
export E2E_TEST_USER_PASSWORD

log "target   $BASE_URL (seeds/dev.json)"
log "sign-in  pool $POOL_ID"

cd "$API_DIR"
exec "$VENV/bin/pytest" tests/integration "$@"
