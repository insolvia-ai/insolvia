#!/usr/bin/env bash
#
# Reset this machine's AWS-backed Insolvia development data: delete and
# recreate the waitlist, case, access-log and firm tables (cheaper and simpler
# than item-level scans for throwaway data) and, unless --skip-cognito, delete
# every user in this machine's Cognito pool. The resources themselves survive;
# only data is wiped. Every resource is asserted against this machine's
# expected names before anything is touched.
#
# AFTERWARDS YOU HAVE NEITHER AN ACCOUNT NOR A FIRM. Re-run
# scripts/dev-aws-create-user.sh and then scripts/dev-aws-seed.sh, in
# that order — the second reads the subject the first creates.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=dev-aws-common.sh
source "$SCRIPT_DIR/dev-aws-common.sh"

AUTO_APPROVE=0
DRY_RUN=0
SKIP_COGNITO=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --profile) [[ $# -ge 2 ]] || die "--profile requires a value."; AWS_PROFILE_VALUE="$2"; shift ;;
    --region) [[ $# -ge 2 ]] || die "--region requires a value."; AWS_REGION_VALUE="$2"; shift ;;
    --yes|-y) AUTO_APPROVE=1 ;;
    --dry-run) DRY_RUN=1 ;;
    --skip-cognito) SKIP_COGNITO=1 ;;
    --help|-h)
      printf 'Usage: %s [--profile NAME] [--region REGION] [--yes] [--dry-run] [--skip-cognito]\n' "$0"
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
outputs="$(terraform_output_json)"
output_machine_id="$(jq -r '.machine_id.value' <<<"$outputs")"
[[ "$output_machine_id" == "$MACHINE_ID" ]] ||
  die "Terraform state belongs to machine '$output_machine_id', not '$MACHINE_ID'."

# Paranoid ownership checks: every name must carry THIS machine's short id
# and match what infra/envs/dev provisions, or the reset refuses to run.
table="$(jq -r '.waitlist_table_name.value' <<<"$outputs")"
case_table="$(jq -r '.case_table_name.value' <<<"$outputs")"
access_log_table="$(jq -r '.case_access_log_table_name.value' <<<"$outputs")"
firm_table="$(jq -r '.firm_table_name.value' <<<"$outputs")"
pool_id="$(jq -r '.auth_user_pool_id.value' <<<"$outputs")"
[[ "$table" == "$WAITLIST_TABLE_NAME_EXPECTED" ]] ||
  die "Refusing reset: unexpected DynamoDB table '$table' (expected '$WAITLIST_TABLE_NAME_EXPECTED')."
[[ "$case_table" == "$CASE_TABLE_NAME_EXPECTED" ]] ||
  die "Refusing reset: unexpected case table '$case_table' (expected '$CASE_TABLE_NAME_EXPECTED')."
[[ "$access_log_table" == "$CASE_ACCESS_LOG_TABLE_NAME_EXPECTED" ]] ||
  die "Refusing reset: unexpected access-log table '$access_log_table' (expected '$CASE_ACCESS_LOG_TABLE_NAME_EXPECTED')."
[[ "$firm_table" == "$FIRM_TABLE_NAME_EXPECTED" ]] ||
  die "Refusing reset: unexpected firm table '$firm_table' (expected '$FIRM_TABLE_NAME_EXPECTED')."
pool_name="$(aws_dev cognito-idp describe-user-pool --user-pool-id "$pool_id" --query 'UserPool.Name' --output text)"
[[ "$pool_name" == "$USER_POOL_NAME_EXPECTED" ]] ||
  die "Refusing reset: Cognito pool is named '$pool_name', not '$USER_POOL_NAME_EXPECTED'."

printf '\nThis will clear development data owned by:\n'
printf '  AWS account: %s\n' "$AWS_ACCOUNT_ID"
printf '  Machine ID:  %s\n' "$MACHINE_ID"
printf '  Table:       %s (delete + recreate)\n' "$table"
printf '  Case table:  %s (delete + recreate)\n' "$case_table"
printf '  Access log:  %s (delete + recreate)\n' "$access_log_table"
printf '  Firm table:  %s (delete + recreate)\n' "$firm_table"
if [[ "$SKIP_COGNITO" -eq 1 ]]; then
  printf '  Cognito:     skipped\n\n'
else
  printf '  Cognito:     %s (users only)\n\n' "$pool_id"
fi

if [[ "$DRY_RUN" -eq 1 ]]; then
  ok "Dry run complete; nothing was changed."
  exit 0
fi
if [[ "$AUTO_APPROVE" -eq 0 ]]; then
  [[ -t 0 ]] || die "Non-interactive reset requires --yes."
  read -r -p "Type RESET to continue: " confirmation
  [[ "$confirmation" == "RESET" ]] || die "Reset canceled."
fi

# Pause a running API container while its table disappears (docker is
# optional here — the plain no-compose dev server is a supported path, so the
# reset works without docker installed).
compose_file="$API_DIR/docker-compose.yml"
api_was_running=0
if command -v docker >/dev/null 2>&1 &&
  docker compose version >/dev/null 2>&1 &&
  docker compose -f "$compose_file" ps --services --status running 2>/dev/null | grep -qx api; then
  api_was_running=1
  docker compose -f "$compose_file" stop api >/dev/null
fi

log "Deleting $table..."
aws_dev dynamodb delete-table --table-name "$table" >/dev/null
aws_dev dynamodb wait table-not-exists --table-name "$table"

# The case store goes the same way. Its customer-managed key is deliberately
# NOT touched: the key is Terraform-owned and survives a reset, so recreating
# the table below re-grants against the same key rather than churning a new
# one (and leaving the old one pending deletion) on every wipe.
log "Deleting $case_table..."
aws_dev dynamodb delete-table --table-name "$case_table" >/dev/null
aws_dev dynamodb wait table-not-exists --table-name "$case_table"

# The access log goes with the cases it describes. Keeping it would leave
# entries pointing at case ids that no longer exist, which is worse than
# nothing — and on a laptop it is synthetic either way.
log "Deleting $access_log_table..."
aws_dev dynamodb delete-table --table-name "$access_log_table" >/dev/null
aws_dev dynamodb wait table-not-exists --table-name "$access_log_table"

# The firms go too, and this table is the one with a foot in both halves of the
# reset: its user rows are keyed by a Cognito subject, and its firm rows own the
# cases deleted just above. Keeping it while deleting the pool's users would
# leave a firm whose every member is an id that no longer resolves — nobody can
# sign in to administer it, and the next `dev-aws-seed.sh` meets a
# subject it has never seen and creates a SECOND firm beside the derelict one.
#
# UNCONDITIONAL, including under --skip-cognito. That flag preserves accounts,
# not their tenancy: the cases above are gone either way, so a surviving firm
# would be an empty shell, and re-running `dev-aws-seed.sh` for the
# accounts that still exist is one command. The reverse default — firms
# outliving the cases they own — is the state with no obvious fix.
log "Deleting $firm_table..."
aws_dev dynamodb delete-table --table-name "$firm_table" >/dev/null
aws_dev dynamodb wait table-not-exists --table-name "$firm_table"

if [[ "$SKIP_COGNITO" -eq 0 ]]; then
  users_json="$(aws_dev cognito-idp list-users --user-pool-id "$pool_id" --output json)"
  while IFS= read -r username; do
    [[ -n "$username" ]] || continue
    aws_dev cognito-idp admin-delete-user --user-pool-id "$pool_id" --username "$username"
  done < <(jq -r '.Users[].Username' <<<"$users_json")
  ok "Removed $(jq '.Users | length' <<<"$users_json") Cognito user(s)."
fi

log "Recreating the empty DynamoDB tables through Terraform..."
terraform -chdir="$TF_DIR" apply -input=false -auto-approve "${TF_VARS[@]}"

if [[ "$api_was_running" -eq 1 ]]; then
  docker compose -f "$compose_file" up -d api >/dev/null
fi
ok "This machine's AWS development data has been reset."
