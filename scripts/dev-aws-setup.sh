#!/usr/bin/env bash
#
# Provision this machine's isolated Insolvia development resources in AWS
# (infra/envs/dev: a case-document bucket, the job and filing queues, the
# keys) and wire the local backend and the app at the ONE shared development
# pool (insolvia-services', ADR 0026 — its sign-in page's style too), read
# through SSM. The database is NOT here: it is the Postgres of
# backend/docker-compose.yml (ADR 0028). This script checks the pool exists,
# applies this machine's own stack, and writes backend/.env and
# apps/insolvia_app/.env.
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

for command in aws jq terraform; do require_command "$command"; done
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
log "Terraform state: s3://$STATE_BUCKET/$STATE_KEY"

if [[ "$CHECK_ONLY" -eq 1 ]]; then
  require_shared_pool
  state_json="$(aws_dev s3 cp "s3://$STATE_BUCKET/$STATE_KEY" -)" ||
    die "Terraform state is missing. Run ./scripts/dev-aws-setup.sh --profile $AWS_PROFILE_VALUE."
  [[ "$(jq -r '.outputs.machine_id.value // empty' <<<"$state_json")" == "$MACHINE_ID" ]] ||
    die "Terraform state does not match this machine ID."
  pool_id="$(jq -r '.outputs.auth_user_pool_id.value // empty' <<<"$state_json")"
  issuer_url="$(jq -r '.outputs.auth_issuer_url.value // empty' <<<"$state_json")"
  document_bucket="$(jq -r '.outputs.case_document_bucket.value // empty' <<<"$state_json")"
  job_queue_url="$(jq -r '.outputs.job_queue_url.value // empty' <<<"$state_json")"
  filing_queue_url="$(jq -r '.outputs.filing_queue_url.value // empty' <<<"$state_json")"
  [[ -n "$document_bucket" && -n "$pool_id" && -n "$issuer_url" && -n "$job_queue_url" && -n "$filing_queue_url" ]] ||
    die "Terraform state is missing required development outputs."
  aws_dev s3api head-bucket --bucket "$document_bucket" >/dev/null ||
    die "Development case document bucket '$document_bucket' is unavailable."
  aws_dev cognito-idp describe-user-pool --user-pool-id "$pool_id" >/dev/null ||
    die "Development Cognito pool '$pool_id' is unavailable."
  aws_dev sqs get-queue-attributes --queue-url "$job_queue_url" --attribute-names QueueArn >/dev/null ||
    die "Development job queue '$job_queue_url' is unavailable."
  aws_dev sqs get-queue-attributes --queue-url "$filing_queue_url" --attribute-names QueueArn >/dev/null ||
    die "Development filing queue '$filing_queue_url' is unavailable."
  if [[ ! -f "$BACKEND_ENV" ]] ||
    ! grep -q "^AUTH_ISSUER_URL=$issuer_url\$" "$BACKEND_ENV" ||
    ! grep -q "^AUTH_USER_POOL_ID=$pool_id\$" "$BACKEND_ENV" ||
    ! grep -q "^AUTH_CLIENT_ID=." "$BACKEND_ENV" ||
    ! grep -q "^AUTH_PORTAL_CLIENT_ID=." "$BACKEND_ENV"; then
    die "backend/.env is missing or stale. Run setup without --check."
  fi
  ok "Per-machine AWS resources and backend/.env are ready."
  exit 0
fi

# This root reads the shared pool's contract at plan time, so check it exists
# first and fail by name. On a machine set up before ADR 0026 the plan also
# DESTROYS the old per-machine pool, which the shared one replaces (run
# scripts/dev-aws-seed.sh afterwards).
require_shared_pool

terraform_init
apply_args=(apply -input=false "${TF_VARS[@]}")
[[ "$AUTO_APPROVE" -eq 1 ]] && apply_args+=(-auto-approve)
terraform -chdir="$TF_DIR" "${apply_args[@]}"

outputs="$(terraform_output_json)"
document_bucket="$(jq -r '.case_document_bucket.value' <<<"$outputs")"
pool_id="$(jq -r '.auth_user_pool_id.value' <<<"$outputs")"
web_client_id="$(jq -r '.auth_web_client_id.value' <<<"$outputs")"
portal_client_id="$(jq -r '.auth_portal_client_id.value' <<<"$outputs")"
auth_domain="$(jq -r '.auth_domain.value' <<<"$outputs")"
issuer_url="$(jq -r '.auth_issuer_url.value' <<<"$outputs")"
google_admin_client_id="$(jq -r '.google_admin_client_id.value' <<<"$outputs")"
job_queue_url="$(jq -r '.job_queue_url.value' <<<"$outputs")"
filing_queue_url="$(jq -r '.filing_queue_url.value' <<<"$outputs")"
mcp_auth_client_ids="$(jq -r '.mcp_auth_client_ids.value' <<<"$outputs")"
mcp_inspector_client_id="$(jq -r '.mcp_client_ids_by_harness.value.inspector' <<<"$outputs")"

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
#   • CASE_DOCUMENT_BUCKET / JOB_QUEUE_URL / FILING_QUEUE_URL: this machine's
#     own bucket and queues, for a `dotnet run` of the worker or a host that
#     wants the real AWS adapters (`set -a; source backend/.env; set +a`).
#     The compose stack keeps its local twins and does not read them.
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
upsert_env "$backend_env" CASE_DOCUMENT_BUCKET "$document_bucket"
upsert_env "$backend_env" JOB_QUEUE_URL "$job_queue_url"
upsert_env "$backend_env" FILING_QUEUE_URL "$filing_queue_url"
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

ok "AWS development resources are ready; backend/.env and apps/insolvia_app/.env were updated."

printf '\nThe shared dev pool:\n'
printf '  User pool id:      %s\n' "$pool_id"
printf '  Web client id:     %s\n' "$web_client_id"
printf '  Portal client id:  %s\n' "$portal_client_id"
printf '  Hosted domain:     %s\n' "$auth_domain"
printf '  Issuer:            %s\n' "$issuer_url"
printf '\nNext: ./scripts/dev-aws-seed.sh (your account and its firm, in the local database),\n'
printf 'then ./scripts/dev-up.sh. A running backend picks up backend/.env on its next up.\n'
