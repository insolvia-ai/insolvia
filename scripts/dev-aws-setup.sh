#!/usr/bin/env bash
#
# Provision this machine's isolated Insolvia development resources in AWS and
# wire services/api at them. This IS local development's database (no local
# emulator): the compose stack's API talks to the real
# per-machine waitlist table this script creates (plus a Cognito pool for
# upcoming auth work). services/api/scripts/dev-setup.sh chains into this
# script unconditionally.
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
  state_json="$(aws_dev s3 cp "s3://$STATE_BUCKET/$STATE_KEY" -)" ||
    die "Terraform state is missing. Run ./scripts/dev-aws-setup.sh --profile $AWS_PROFILE_VALUE."
  [[ "$(jq -r '.outputs.machine_id.value // empty' <<<"$state_json")" == "$MACHINE_ID" ]] ||
    die "Terraform state does not match this machine ID."
  table="$(jq -r '.outputs.waitlist_table_name.value // empty' <<<"$state_json")"
  case_table="$(jq -r '.outputs.case_table_name.value // empty' <<<"$state_json")"
  access_log_table="$(jq -r '.outputs.case_access_log_table_name.value // empty' <<<"$state_json")"
  firm_table="$(jq -r '.outputs.firm_table_name.value // empty' <<<"$state_json")"
  pool_id="$(jq -r '.outputs.auth_user_pool_id.value // empty' <<<"$state_json")"
  document_bucket="$(jq -r '.outputs.case_document_bucket.value // empty' <<<"$state_json")"
  [[ -n "$table" && -n "$case_table" && -n "$access_log_table" && -n "$firm_table" && -n "$document_bucket" && -n "$pool_id" ]] || die "Terraform state is missing required development outputs."
  aws_dev dynamodb describe-table --table-name "$table" >/dev/null ||
    die "Development DynamoDB table '$table' is unavailable."
  aws_dev dynamodb describe-table --table-name "$case_table" >/dev/null ||
    die "Development case table '$case_table' is unavailable."
  aws_dev dynamodb describe-table --table-name "$access_log_table" >/dev/null ||
    die "Development case access-log table '$access_log_table' is unavailable."
  aws_dev dynamodb describe-table --table-name "$firm_table" >/dev/null ||
    die "Development firm table '$firm_table' is unavailable."
  aws_dev s3api head-bucket --bucket "$document_bucket" >/dev/null ||
    die "Development case document bucket '$document_bucket' is unavailable."
  aws_dev cognito-idp describe-user-pool --user-pool-id "$pool_id" >/dev/null ||
    die "Development Cognito pool '$pool_id' is unavailable."
  if [[ ! -f "$API_DIR/.env" ]] || ! grep -q "^WAITLIST_TABLE_NAME=$table\$" "$API_DIR/.env" ||
    ! grep -q "^CASE_TABLE_NAME=$case_table\$" "$API_DIR/.env" ||
    ! grep -q "^CASE_ACCESS_LOG_TABLE_NAME=$access_log_table\$" "$API_DIR/.env" ||
    ! grep -q "^FIRM_TABLE_NAME=$firm_table\$" "$API_DIR/.env" ||
    ! grep -q "^CASE_DOCUMENT_BUCKET=$document_bucket\$" "$API_DIR/.env" ||
    ! grep -q "^AUTH_USER_POOL_ID=$pool_id\$" "$API_DIR/.env"; then
    die "services/api/.env is missing or stale. Run setup without --check."
  fi
  ok "Per-machine AWS resources and services/api/.env are ready."
  exit 0
fi

terraform_init
apply_args=(apply -input=false "${TF_VARS[@]}")
[[ "$AUTO_APPROVE" -eq 1 ]] && apply_args+=(-auto-approve)
terraform -chdir="$TF_DIR" "${apply_args[@]}"

outputs="$(terraform_output_json)"
table="$(jq -r '.waitlist_table_name.value' <<<"$outputs")"
case_table="$(jq -r '.case_table_name.value' <<<"$outputs")"
access_log_table="$(jq -r '.case_access_log_table_name.value' <<<"$outputs")"
firm_table="$(jq -r '.firm_table_name.value' <<<"$outputs")"
document_bucket="$(jq -r '.case_document_bucket.value' <<<"$outputs")"
pool_id="$(jq -r '.auth_user_pool_id.value' <<<"$outputs")"
web_client_id="$(jq -r '.auth_web_client_id.value' <<<"$outputs")"
auth_domain="$(jq -r '.auth_domain.value' <<<"$outputs")"
issuer_url="$(jq -r '.auth_issuer_url.value' <<<"$outputs")"
admin_audit_table="$(jq -r '.admin_audit_table_name.value' <<<"$outputs")"
google_admin_client_id="$(jq -r '.google_admin_client_id.value' <<<"$outputs")"

# ── Wire services/api at the real table ─────────────────────────
# Mechanism (chosen after reading services/api/docker-compose.yml): docker
# compose auto-reads services/api/.env for VARIABLE SUBSTITUTION — not
# container env — and the compose file's `environment:` block is written as
# ${VAR:-default} substitutions for exactly the keys below. So:
#   • WAITLIST_TABLE_NAME here points the stack at this machine's table
#     (dev-up.sh refuses to start without it).
#   • CASE_TABLE_NAME points it at this machine's case store (issue 8.2),
#     encrypted under this machine's own customer-managed key. Local work on
#     anything storing case data needs it, and having it here means a KMS or
#     IAM mistake fails on a laptop rather than after a deploy.
#   • AWS_PROFILE is not read by compose at all — it is the profile name
#     services/api/scripts/dev-up.sh uses to export short-lived credentials
#     into the container at `compose up` time. Credentials are never written
#     to this file.
#   • INSOLVIA_ENV/AWS_DEFAULT_REGION also serve anyone running the plain
#     dev server off this file: `set -a; source services/api/.env; set +a`.
#   • AUTH_ISSUER_URL/AUTH_CLIENT_ID point token verification at this
#     machine's own Cognito pool. Without them every authenticated route
#     answers 401 — the service fails closed rather than waving requests
#     through when auth is unconfigured, so these are not optional for
#     local work on anything behind sign-in. Neither is a secret: both
#     appear in every sign-in redirect.

api_env="$API_DIR/.env"
upsert_env "$api_env" WAITLIST_TABLE_NAME "$table"
upsert_env "$api_env" CASE_TABLE_NAME "$case_table"
upsert_env "$api_env" CASE_ACCESS_LOG_TABLE_NAME "$access_log_table"
upsert_env "$api_env" FIRM_TABLE_NAME "$firm_table"
upsert_env "$api_env" CASE_DOCUMENT_BUCKET "$document_bucket"
upsert_env "$api_env" INSOLVIA_ENV "local"
upsert_env "$api_env" AWS_PROFILE "$AWS_PROFILE_VALUE"
upsert_env "$api_env" AWS_DEFAULT_REGION "$AWS_REGION_VALUE"
upsert_env "$api_env" AUTH_ISSUER_URL "$issuer_url"
upsert_env "$api_env" AUTH_CLIENT_ID "$web_client_id"
# The pool the API CALLS, as against the issuer it verifies against. Both end
# in the same id and neither is derived from the other — see services/api's
# core/config.py for why parsing one out of the other is refused.
upsert_env "$api_env" AUTH_USER_POOL_ID "$pool_id"

# ── Wire services/admin at the same resources (#213) ───────────
# Same compose-substitution mechanism as the API's file above. FIRM_TABLE_NAME
# is the SAME table the API reads — the admin service is the second principal
# with access to it (ADR 0011); locally both run as the developer's own IAM
# user. FIRM_USER_POOL_ID is the pool provisioning mints first-administrator
# accounts in (the one the API verifies against — one dev pool serves both
# jobs). GOOGLE_CLIENT_ID is the dev Workspace OAuth client, a public value.
admin_env="$REPO_ROOT/services/admin/.env"
upsert_env "$admin_env" FIRM_TABLE_NAME "$firm_table"
upsert_env "$admin_env" FIRM_USER_POOL_ID "$pool_id"
upsert_env "$admin_env" ADMIN_AUDIT_TABLE_NAME "$admin_audit_table"
upsert_env "$admin_env" GOOGLE_CLIENT_ID "$google_admin_client_id"
upsert_env "$admin_env" INSOLVIA_ENV "local"
upsert_env "$admin_env" AWS_PROFILE "$AWS_PROFILE_VALUE"
upsert_env "$admin_env" AWS_DEFAULT_REGION "$AWS_REGION_VALUE"

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

ok "AWS development resources are ready; services/api/.env, services/admin/.env and apps/insolvia_app/.env were updated."

# If setup is reapplied while the API container is already running, replace it
# so it picks up the new table name and the freshly exported credentials —
# container environment variables cannot be changed in place.
compose_file="$API_DIR/docker-compose.yml"
if command -v docker >/dev/null 2>&1 &&
  docker compose version >/dev/null 2>&1 &&
  docker compose -f "$compose_file" ps --services --status running 2>/dev/null | grep -qx api; then
  log "Recreating the running API container with refreshed AWS credentials..."
  export AWS_DEFAULT_REGION="$AWS_REGION_VALUE"
  docker compose -f "$compose_file" up -d --build --force-recreate api
  ok "The running API container now targets $table."
fi

printf '\nCognito (for upcoming local auth work — nothing consumes these yet):\n'
printf '  User pool id:      %s\n' "$pool_id"
printf '  Web client id:     %s\n' "$web_client_id"
printf '  Hosted domain:     %s\n' "$auth_domain"
printf '  Issuer:            %s\n' "$issuer_url"
printf '\nStart the API against your per-machine table with:\n  ./services/api/scripts/dev-up.sh\n'
