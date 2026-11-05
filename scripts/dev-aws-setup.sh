#!/usr/bin/env bash
#
# Wire the local backend and the app at the ONE shared development pool
# (insolvia-services', ADR 0026), read through SSM. Everything else runs on
# this machine: the database, document bytes and queues are the local twins of
# backend/docker-compose.yml. The per-machine AWS stack (infra/envs/dev) was
# torn down on 2026-11-05 and is no longer applied here; see
# docs/runbooks/aws-paused.md.
#
#   ./scripts/dev-aws-setup.sh               # uses the default AWS profile
#   ./scripts/dev-aws-setup.sh --check       # verify, change nothing
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=dev-aws-common.sh
source "$SCRIPT_DIR/dev-aws-common.sh"

AUTO_APPROVE=0
CHECK_ONLY=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --profile) [[ $# -ge 2 ]] || die "--profile requires a value."; AWS_PROFILE_VALUE="$2"; shift ;;
    --region) [[ $# -ge 2 ]] || die "--region requires a value."; AWS_REGION_VALUE="$2"; shift ;;
    --yes|-y) AUTO_APPROVE=1 ;;
    --check) CHECK_ONLY=1 ;;
    --help|-h)
      printf 'Usage: %s [--profile NAME] [--region REGION] [--yes] [--check]\n' "$0"
      exit 0
      ;;
    *) die "Unknown option: $1" ;;
  esac
  shift
done

for command in aws jq; do require_command "$command"; done
if [[ "$CHECK_ONLY" -eq 1 ]]; then
  load_machine_id false
else
  load_machine_id true
fi
load_aws_identity

log "AWS account: $AWS_ACCOUNT_ID"
log "AWS principal: $AWS_PRINCIPAL_ARN"
log "Machine ID: $MACHINE_ID"
log "Resource prefix: $RESOURCE_PREFIX"

load_dev_pool_contract
aws_dev cognito-idp describe-user-pool --user-pool-id "$POOL_ID" >/dev/null ||
  die "Development Cognito pool '$POOL_ID' is unavailable."

if [[ "$CHECK_ONLY" -eq 1 ]]; then
  if [[ ! -f "$BACKEND_ENV" ]] ||
    ! grep -q "^AUTH_ISSUER_URL=$ISSUER_URL\$" "$BACKEND_ENV" ||
    ! grep -q "^AUTH_USER_POOL_ID=$POOL_ID\$" "$BACKEND_ENV" ||
    ! grep -q "^AUTH_CLIENT_ID=." "$BACKEND_ENV" ||
    ! grep -q "^AUTH_PORTAL_CLIENT_ID=." "$BACKEND_ENV"; then
    die "backend/.env is missing or stale. Run setup without --check."
  fi
  ok "The shared dev pool and backend/.env are ready."
  exit 0
fi

pool_id="$POOL_ID"
web_client_id="$WEB_CLIENT_ID"
portal_client_id="$PORTAL_CLIENT_ID"
auth_domain="$AUTH_DOMAIN"
issuer_url="$ISSUER_URL"
google_admin_client_id="$GOOGLE_ADMIN_CLIENT_ID_DEFAULT"
mcp_auth_client_ids="$MCP_AUTH_CLIENT_IDS"
mcp_inspector_client_id="$MCP_INSPECTOR_CLIENT_ID"

# ── Wire the local backend at the shared pool ───────────────────
# Mechanism: docker compose auto-reads backend/.env for VARIABLE SUBSTITUTION
# (not container env), and backend/docker-compose.yml's `environment:` blocks
# are ${VAR:-default} substitutions for exactly the keys below. None is a
# secret — every one appears in a sign-in redirect or a public config.
#   • AUTH_ISSUER_URL / AUTH_CLIENT_ID / AUTH_PORTAL_CLIENT_ID: token
#     verification against the shared dev pool. Without them every protected
#     route answers 401 — the API fails closed rather than waving requests
#     through when auth is unconfigured.
#   • AUTH_USER_POOL_ID: not read by compose; contract/scripts/dev-test.sh
#     --target deployed signs in against it, and scripts/dev-aws-seed.sh resolves real subjects
#     from it.
#   • GOOGLE_CLIENT_ID: the dev Workspace OAuth client the admin service
#     verifies staff tokens against (the admin portal on :3100).
#   • INSOLVIA_SEED=false: the compose migrator only migrates, because
#     scripts/dev-aws-seed.sh is the seeder once a pool is wired — with the
#     people's REAL subjects, which a derived local seed would contradict.
#   • AUTH_CLIENT_IDS: the MCP harness allowlist (this pool's claude +
#     inspector clients), for the compose MCP server on :8788.
#   • AUTH_MCP_INSPECTOR_CLIENT_ID: one of those, not read by compose;
#     contract/scripts/dev-test.sh --target deployed signs the seeded admin in
#     through it, the token an MCP Inspector session would hold.
#   • CASE_DOCUMENT_BUCKET / JOB_QUEUE_URL / FILING_QUEUE_URL are removed: the
#     bucket and queues they named no longer exist, and the compose stack
#     uses its local twins.
backend_env="$BACKEND_ENV"
upsert_env "$backend_env" INSOLVIA_ENV "local"
upsert_env "$backend_env" AUTH_ISSUER_URL "$issuer_url"
upsert_env "$backend_env" AUTH_CLIENT_ID "$web_client_id"
upsert_env "$backend_env" AUTH_PORTAL_CLIENT_ID "$portal_client_id"
upsert_env "$backend_env" AUTH_USER_POOL_ID "$pool_id"
upsert_env "$backend_env" GOOGLE_CLIENT_ID "$google_admin_client_id"
upsert_env "$backend_env" INSOLVIA_SEED "false"
upsert_env "$backend_env" AUTH_CLIENT_IDS "$mcp_auth_client_ids"
upsert_env "$backend_env" AUTH_MCP_INSPECTOR_CLIENT_ID "$mcp_inspector_client_id"
for key in CASE_DOCUMENT_BUCKET JOB_QUEUE_URL FILING_QUEUE_URL; do
  remove_env "$backend_env" "$key"
done
upsert_env "$backend_env" AWS_DEFAULT_REGION "$AWS_REGION_VALUE"

# ── Wire the Expo app at the same pool ──────────────────────────
# The app reads these two at BUILD time, not runtime: Expo inlines only
# `EXPO_PUBLIC_*`-prefixed variables into the bundle, and it loads them from
# apps/insolvia_app/.env automatically. Without them the app renders its
# "sign-in is not configured" screen — a soft failure by design, so it is easy
# to mistake for the app simply not having sign-in yet.
#
# The values are the SAME pool the API above verifies against, which is the
# point: a local sign-in mints a token this machine's own API accepts. Neither
# is a secret — both appear in every sign-in redirect — which is what makes the
# EXPO_PUBLIC_ prefix legitimate here (nothing secret may ever carry it).
#
# Metro does not key its cache on environment variables, so `npm run build`
# passes --clear; if you edit this file by hand, restart the dev server.
app_env="$APP_DIR/.env"
upsert_env "$app_env" EXPO_PUBLIC_INSOLVIA_ENV "local"
upsert_env "$app_env" EXPO_PUBLIC_COGNITO_DOMAIN "$auth_domain"
upsert_env "$app_env" EXPO_PUBLIC_COGNITO_CLIENT_ID "$web_client_id"
# The portal's own client (ADR 0023), read by the app's /portal route group
# (src/config/environment.ts, resolvePortalAuthConfig). Without it /portal
# says "sign-in is not configured" and the staff app is unaffected.
upsert_env "$app_env" EXPO_PUBLIC_COGNITO_PORTAL_CLIENT_ID "$portal_client_id"

ok "The shared dev pool is wired; backend/.env and apps/insolvia_app/.env were updated."

printf '\nThe shared dev pool:\n'
printf '  User pool id:      %s\n' "$pool_id"
printf '  Web client id:     %s\n' "$web_client_id"
printf '  Portal client id:  %s\n' "$portal_client_id"
printf '  Hosted domain:     %s\n' "$auth_domain"
printf '  Issuer:            %s\n' "$issuer_url"
printf '\nNext: ./scripts/dev-aws-seed.sh (your account and its firm, in the local database),\n'
printf 'then ./scripts/dev-up.sh. A running backend picks up backend/.env on its next up.\n'
