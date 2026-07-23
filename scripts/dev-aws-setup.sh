#!/usr/bin/env bash
#
# Provision this machine's isolated Insolvia development resources in AWS and
# wire services/api at them. Opt-in: the compose stack's dynamodb-local
# default needs zero AWS — run this only when you want local dev against a
# real per-machine table (and a Cognito pool for upcoming auth work).
#
#   ./scripts/dev-aws-setup.sh --profile insolvia
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
  pool_id="$(jq -r '.outputs.auth_user_pool_id.value // empty' <<<"$state_json")"
  [[ -n "$table" && -n "$pool_id" ]] || die "Terraform state is missing required development outputs."
  aws_dev dynamodb describe-table --table-name "$table" >/dev/null ||
    die "Development DynamoDB table '$table' is unavailable."
  aws_dev cognito-idp describe-user-pool --user-pool-id "$pool_id" >/dev/null ||
    die "Development Cognito pool '$pool_id' is unavailable."
  if [[ ! -f "$API_DIR/.env" ]] || ! grep -q "^WAITLIST_TABLE_NAME=$table\$" "$API_DIR/.env"; then
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
pool_id="$(jq -r '.auth_user_pool_id.value' <<<"$outputs")"
web_client_id="$(jq -r '.auth_web_client_id.value' <<<"$outputs")"
desktop_client_id="$(jq -r '.auth_desktop_client_id.value' <<<"$outputs")"
auth_domain="$(jq -r '.auth_domain.value' <<<"$outputs")"
issuer_url="$(jq -r '.auth_issuer_url.value' <<<"$outputs")"

# ── Wire services/api at the real table ─────────────────────────
# Mechanism (chosen after reading services/api/docker-compose.yml): docker
# compose auto-reads services/api/.env for VARIABLE SUBSTITUTION — not
# container env — and the compose file's `environment:` block is written as
# ${VAR:-default} substitutions for exactly the keys below. So:
#   • WAITLIST_TABLE_NAME here overrides the insolvia-waitlist-local default.
#   • DYNAMODB_ENDPOINT_URL is upserted to EMPTY rather than removed, because
#     the compose file uses the colon-less ${DYNAMODB_ENDPOINT_URL-...} form:
#     an ABSENT key falls back to dynamodb-local, an EMPTY one means "no
#     endpoint override — real AWS" (load_config turns "" into None).
#   • AWS_PROFILE is not read by compose at all — it is the marker (and the
#     profile name) services/api/scripts/dev-up.sh uses to export short-lived
#     credentials into the container at `compose up` time. Credentials are
#     never written to this file.
#   • INSOLVIA_ENV/AWS_DEFAULT_REGION also serve anyone running the plain
#     dev server off this file: `set -a; source services/api/.env; set +a`.

api_env="$API_DIR/.env"
upsert_env "$api_env" WAITLIST_TABLE_NAME "$table"
upsert_env "$api_env" INSOLVIA_ENV "local"
upsert_env "$api_env" AWS_PROFILE "$AWS_PROFILE_VALUE"
upsert_env "$api_env" AWS_DEFAULT_REGION "$AWS_REGION_VALUE"
upsert_env "$api_env" DYNAMODB_ENDPOINT_URL ""

ok "AWS development resources are ready and services/api/.env was updated."

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
printf '  Desktop client id: %s\n' "$desktop_client_id"
printf '  Hosted domain:     %s\n' "$auth_domain"
printf '  Issuer:            %s\n' "$issuer_url"
printf '\nStart the API against your per-machine table with:\n  ./services/api/scripts/dev-up.sh\n'
