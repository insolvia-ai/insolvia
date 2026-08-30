#!/usr/bin/env bash
#
# Destroy an ORPHANED per-machine development environment — one left behind by
# a PREVIOUS machine-id, after ~/.config/insolvia/machine-id was lost or
# regenerated.
#
# dev-aws-destroy.sh cannot reach these: it inits Terraform with the CURRENT
# machine-id's state key. This script finds the orphan's state object in the
# state bucket by its 12-char short id (the suffix visible in the leftover
# resource names), runs `terraform destroy` against THAT state — so the whole
# environment goes (waitlist table, Cognito pool + domain + clients, whatever
# else the state tracks), not just the resource that was noticed — and then
# deletes the state object itself.
#
# Without --yes, `terraform destroy` shows its plan and asks for interactive
# confirmation, so a plain run is a safe way to see what would be removed.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=dev-aws-common.sh
source "$SCRIPT_DIR/dev-aws-common.sh"

usage() {
  printf 'Usage: %s <short-id> [--profile NAME] [--region REGION] [--yes]\n' "$0"
  printf '  <short-id>  the 12 hex chars from the orphaned resource names,\n'
  printf '              e.g. 0123456789ab for insolvia-dev-0123456789ab-waitlist\n'
}

ORPHAN_SHORT_ID=""
AUTO_APPROVE=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --profile) [[ $# -ge 2 ]] || die "--profile requires a value."; AWS_PROFILE_VALUE="$2"; shift ;;
    --region) [[ $# -ge 2 ]] || die "--region requires a value."; AWS_REGION_VALUE="$2"; shift ;;
    --yes|-y) AUTO_APPROVE=1 ;;
    --help|-h) usage; exit 0 ;;
    -*) die "Unknown option: $1" ;;
    *)
      [[ -z "$ORPHAN_SHORT_ID" ]] || die "Only one short id may be given."
      ORPHAN_SHORT_ID="$1"
      ;;
  esac
  shift
done

[[ -n "$ORPHAN_SHORT_ID" ]] || { usage >&2; exit 1; }
[[ "$ORPHAN_SHORT_ID" =~ ^[0-9a-f]{12}$ ]] ||
  die "Short id must be exactly 12 lowercase hex characters (got '$ORPHAN_SHORT_ID')."

for command in aws jq terraform; do require_command "$command"; done
load_machine_id false
[[ "$ORPHAN_SHORT_ID" != "$MACHINE_SHORT_ID" ]] ||
  die "dev-$ORPHAN_SHORT_ID is THIS machine's live development environment — use scripts/dev-aws-destroy.sh."
load_aws_identity

# ── Locate the orphan's state object by short id ────────────────
# State keys embed the FULL machine UUID
# (insolvia/dev/<account>/<uuid>/terraform.tfstate); resource names embed only
# its first 12 hex chars. Match the listing on that prefix to recover the
# full id.
matches="$(
  aws_dev s3api list-objects-v2 \
    --bucket "$STATE_BUCKET" \
    --prefix "insolvia/dev/$AWS_ACCOUNT_ID/" \
    --query 'Contents[].Key' --output json |
  jq -r --arg short "$ORPHAN_SHORT_ID" '
    (. // [])[]
    | select(test("^insolvia/dev/[0-9]{12}/[0-9a-f-]{36}/terraform\\.tfstate$"))
    | select(split("/")[3] | gsub("-"; "") | startswith($short))'
)"
[[ -n "$matches" ]] ||
  die "No state object for short id $ORPHAN_SHORT_ID under insolvia/dev/$AWS_ACCOUNT_ID/ in s3://$STATE_BUCKET. Terraform cannot drive this cleanup; any surviving dev-$ORPHAN_SHORT_ID resources need deleting by hand."
[[ "$(wc -l <<<"$matches")" -eq 1 ]] ||
  die "Short id $ORPHAN_SHORT_ID matches more than one state object — refusing to guess."
ORPHAN_STATE_KEY="$matches"
ORPHAN_MACHINE_ID="$(cut -d/ -f4 <<<"$ORPHAN_STATE_KEY")"
log "Orphaned machine id: $ORPHAN_MACHINE_ID (state: $ORPHAN_STATE_KEY)"

# ── Destroy via the orphan's own state ──────────────────────────
# Not terraform_init: that helper injects the CURRENT machine's state key.
# This leaves infra/envs/dev/.terraform configured for the orphan key
# afterwards, which is harmless — every dev-aws-* script re-inits with
# -reconfigure and the right key.
export AWS_REGION="$AWS_REGION_VALUE"
export_temporary_aws_credentials
terraform -chdir="$TF_DIR" init -reconfigure -input=false \
  -backend-config="key=$ORPHAN_STATE_KEY"

state_resources="$(terraform -chdir="$TF_DIR" state list)"
if [[ -n "$state_resources" ]]; then
  output_machine_id="$(terraform -chdir="$TF_DIR" output -raw machine_id)"
  [[ "$output_machine_id" == "$ORPHAN_MACHINE_ID" ]] ||
    die "State at $ORPHAN_STATE_KEY belongs to machine '$output_machine_id', not '$ORPHAN_MACHINE_ID'."

  log "State tracks:"
  while IFS= read -r resource; do printf '    %s\n' "$resource"; done <<<"$state_resources"

  TF_VARS=(
    "-var=aws_region=$AWS_REGION_VALUE"
    "-var=aws_principal_arn=$AWS_PRINCIPAL_ARN"
    "-var=machine_id=$ORPHAN_MACHINE_ID"
    "-var=machine_short_id=$ORPHAN_SHORT_ID"
    # Tag-only value, and destroy never writes tags — a placeholder is fine.
    "-var=machine_name=orphaned-$ORPHAN_SHORT_ID"
  )
  warn "This will destroy the orphaned dev-$ORPHAN_SHORT_ID resources in account $AWS_ACCOUNT_ID."
  destroy_args=(destroy -input=false "${TF_VARS[@]}")
  [[ "$AUTO_APPROVE" -eq 1 ]] && destroy_args+=(-auto-approve)
  terraform -chdir="$TF_DIR" "${destroy_args[@]}"
else
  log "State at $ORPHAN_STATE_KEY tracks no resources; nothing to destroy."
fi

# ── Remove the now-empty state object itself ────────────────────
# This is what makes the orphan disappear from the bucket listing; without it
# the empty state would sit there implying an environment that no longer
# exists.
aws_dev s3api delete-object --bucket "$STATE_BUCKET" --key "$ORPHAN_STATE_KEY" >/dev/null
aws_dev s3api delete-object --bucket "$STATE_BUCKET" --key "$ORPHAN_STATE_KEY.tflock" >/dev/null 2>&1 || true

ok "Orphaned environment dev-$ORPHAN_SHORT_ID destroyed and its state object removed."
