#!/usr/bin/env bash
#
# Destroy this machine's isolated Insolvia development resources in AWS and
# unwind the services/api/.env wiring, returning local dev to the zero-AWS
# dynamodb-local default. The machine ID is retained so a later
# dev-aws-setup.sh recreates the SAME per-machine names and state key.
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

# Unwind the setup script's wiring — a services/api/.env still naming the
# destroyed table would break the previously-working compose default. Removing
# DYNAMODB_ENDPOINT_URL entirely restores the dynamodb-local fallback (the
# compose file's ${DYNAMODB_ENDPOINT_URL-...} default fires only when the key
# is ABSENT), and removing AWS_PROFILE stops dev-up.sh exporting credentials.
api_env="$API_DIR/.env"
remove_env "$api_env" WAITLIST_TABLE_NAME
remove_env "$api_env" AWS_PROFILE
remove_env "$api_env" DYNAMODB_ENDPOINT_URL

ok "This machine's Insolvia development resources were destroyed and services/api/.env was unwound. The machine ID was retained for safe reuse."
