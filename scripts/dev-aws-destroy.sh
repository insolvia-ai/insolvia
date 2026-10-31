#!/usr/bin/env bash
#
# Destroy this machine's isolated Insolvia development resources in AWS and
# unwind the backend/.env and apps/insolvia_app/.env wiring. The local
# database (backend/docker-compose.yml's Postgres) is not AWS and is left
# alone — scripts/dev-aws-reset.sh wipes it. After this, the backend still
# starts and every signed-in route answers 401 until dev-aws-setup.sh runs
# again. The machine ID is retained so a later dev-aws-setup.sh recreates the
# SAME per-machine names and state key.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=dev-aws-common.sh
source "$SCRIPT_DIR/dev-aws-common.sh"

AUTO_APPROVE=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --profile) [[ $# -ge 2 ]] || die "--profile requires a value."; AWS_PROFILE_VALUE="$2"; shift ;;
    --region) [[ $# -ge 2 ]] || die "--region requires a value."; AWS_REGION_VALUE="$2"; shift ;;
    --yes|-y) AUTO_APPROVE=1 ;;
    --help|-h)
      printf 'Usage: %s [--profile NAME] [--region REGION] [--yes]\n' "$0"
      exit 0
      ;;
    *) die "Unknown option: $1" ;;
  esac
  shift
done

for command in aws jq terraform; do require_command "$command"; done
load_machine_id false
load_aws_identity
terraform_init

state_resources="$(terraform -chdir="$TF_DIR" state list)"
if [[ -n "$state_resources" ]]; then
  output_machine_id="$(terraform -chdir="$TF_DIR" output -raw machine_id)"
  [[ "$output_machine_id" == "$MACHINE_ID" ]] ||
    die "Terraform state belongs to machine '$output_machine_id', not '$MACHINE_ID'."
fi

warn "This will destroy only resources tagged for machine $MACHINE_ID in account $AWS_ACCOUNT_ID."
destroy_args=(destroy -input=false "${TF_VARS[@]}")
[[ "$AUTO_APPROVE" -eq 1 ]] && destroy_args+=(-auto-approve)
terraform -chdir="$TF_DIR" "${destroy_args[@]}"

# The shared development pool (insolvia-services', its style included) is NOT
# this machine's and is left alone: every machine signs in against it.

# Unwind the setup script's wiring: the bucket and queues it named are gone.
# The shared pool survives, but this machine's wiring to it goes with the
# rest: the API fails closed on absent auth config, so protected routes answer
# a clean 401 until dev-aws-setup.sh runs again.
for key in CASE_DOCUMENT_BUCKET JOB_QUEUE_URL FILING_QUEUE_URL \
  AUTH_ISSUER_URL AUTH_CLIENT_ID AUTH_PORTAL_CLIENT_ID AUTH_USER_POOL_ID \
  AUTH_CLIENT_IDS GOOGLE_CLIENT_ID INSOLVIA_SEED; do
  remove_env "$BACKEND_ENV" "$key"
done

# Same for the app: cleared, it renders "sign-in is not configured", which is
# the honest state after a destroy.
app_env="$APP_DIR/.env"
remove_env "$app_env" EXPO_PUBLIC_COGNITO_DOMAIN
remove_env "$app_env" EXPO_PUBLIC_COGNITO_CLIENT_ID
remove_env "$app_env" EXPO_PUBLIC_COGNITO_PORTAL_CLIENT_ID

ok "This machine's Insolvia development resources were destroyed; backend/.env and apps/insolvia_app/.env were unwound. The machine ID was retained for safe reuse."
