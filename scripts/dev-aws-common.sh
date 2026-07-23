#!/usr/bin/env bash
#
# Shared helpers for the per-machine AWS development scripts
# (dev-aws-setup.sh / dev-aws-reset.sh / dev-aws-destroy.sh) — the Insolvia
# adaptation of humbugg's dev-aws pattern.
#
# Identity model: a persistent UUID per OS user per machine, generated once
# into ~/.config/insolvia/machine-id. Its first 12 hex chars become the
# environment suffix (dev-<short-id>) baked into every resource name and this
# machine's own Terraform state key, so two developers can never collide on
# names or state.
#
# Sourced, never executed — callers own `set -euo pipefail`.

# Several variables here (RESOURCE_PREFIX, WAITLIST_TABLE_NAME_EXPECTED, ...)
# are consumed by the sourcing scripts, not this file.
# shellcheck disable=SC2034

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TF_DIR="$REPO_ROOT/infra/envs/dev"
API_DIR="$REPO_ROOT/services/api"
CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/insolvia"
MACHINE_ID_FILE="$CONFIG_DIR/machine-id"
STATE_BUCKET="insolvia-terraform-state"

# `insolvia` is this repo's documented AWS profile (own dedicated account), so
# it is the default rather than `default`. Override with --profile on any
# script, or AWS_PROFILE in the environment.
AWS_PROFILE_VALUE="${AWS_PROFILE:-insolvia}"
AWS_REGION_VALUE="${AWS_REGION:-${AWS_DEFAULT_REGION:-us-east-1}}"

log()  { printf '\033[1;34m[dev-aws]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[error]\033[0m %s\n' "$*" >&2; exit 1; }

require_command() {
  command -v "$1" >/dev/null 2>&1 || die "Required command '$1' is not installed."
}

aws_dev() {
  aws --no-cli-pager --profile "$AWS_PROFILE_VALUE" --region "$AWS_REGION_VALUE" "$@"
}

load_machine_id() {
  local create_if_missing="${1:-false}"
  if [[ ! -f "$MACHINE_ID_FILE" ]]; then
    [[ "$create_if_missing" == "true" ]] ||
      die "No machine ID exists. Run ./scripts/dev-aws-setup.sh first."
    mkdir -p "$CONFIG_DIR"
    chmod 700 "$CONFIG_DIR"
    if command -v uuidgen >/dev/null 2>&1; then
      uuidgen | tr '[:upper:]' '[:lower:]' > "$MACHINE_ID_FILE"
    elif command -v openssl >/dev/null 2>&1; then
      local hex
      hex="$(openssl rand -hex 16)"
      printf '%s-%s-%s-%s-%s\n' \
        "${hex:0:8}" "${hex:8:4}" "${hex:12:4}" "${hex:16:4}" "${hex:20:12}" \
        > "$MACHINE_ID_FILE"
    else
      die "Either uuidgen or openssl is required to generate the machine ID."
    fi
    chmod 600 "$MACHINE_ID_FILE"
    ok "Generated machine ID at $MACHINE_ID_FILE."
  fi

  MACHINE_ID="$(tr -d '[:space:]' < "$MACHINE_ID_FILE" | tr '[:upper:]' '[:lower:]')"
  [[ "$MACHINE_ID" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] ||
    die "Invalid machine ID in $MACHINE_ID_FILE."
  MACHINE_SHORT_ID="$(printf '%s' "$MACHINE_ID" | tr -d '-' | cut -c1-12)"
}

load_aws_identity() {
  local identity
  identity="$(aws_dev sts get-caller-identity --output json)" ||
    die "Could not authenticate with AWS profile '$AWS_PROFILE_VALUE'."
  AWS_ACCOUNT_ID="$(jq -r '.Account' <<<"$identity")"
  AWS_PRINCIPAL_ARN="$(jq -r '.Arn' <<<"$identity")"
  [[ "$AWS_ACCOUNT_ID" =~ ^[0-9]{12}$ ]] || die "AWS returned an invalid account ID."
  MACHINE_NAME="$(hostname -s 2>/dev/null || hostname)"
  RESOURCE_PREFIX="insolvia-dev-$MACHINE_SHORT_ID"
  DEV_ENVIRONMENT="dev-$MACHINE_SHORT_ID"
  # What infra/envs/dev actually names things — asserted before any
  # destructive action ever touches a resource.
  WAITLIST_TABLE_NAME_EXPECTED="insolvia-waitlist-$DEV_ENVIRONMENT"
  USER_POOL_NAME_EXPECTED="insolvia-users-$DEV_ENVIRONMENT"
  STATE_KEY="insolvia/dev/$AWS_ACCOUNT_ID/$MACHINE_ID/terraform.tfstate"
}

set_terraform_vars() {
  TF_VARS=(
    "-var=aws_region=$AWS_REGION_VALUE"
    "-var=aws_principal_arn=$AWS_PRINCIPAL_ARN"
    "-var=machine_id=$MACHINE_ID"
    "-var=machine_short_id=$MACHINE_SHORT_ID"
    "-var=machine_name=$MACHINE_NAME"
  )
}

export_temporary_aws_credentials() {
  # Resolve the profile into plain short-lived env credentials. This step is
  # LOAD-BEARING here, not a nicety: the `insolvia` profile uses the new
  # `aws login` session format, which Terraform's AWS SDK cannot read — a bare
  # `terraform init/apply` against the profile fails to find credentials.
  # `aws configure export-credentials` refreshes the session and hands back
  # env-var credentials every SDK understands. The same exported set is what
  # the compose stack injects into the API container (see
  # services/api/scripts/dev-up.sh).
  local credentials
  aws_dev sts get-caller-identity >/dev/null ||
    die "AWS profile '$AWS_PROFILE_VALUE' does not currently have a valid session. Sign in to AWS (aws login --profile $AWS_PROFILE_VALUE) and try again."
  credentials="$(aws configure export-credentials --profile "$AWS_PROFILE_VALUE" --format process)" ||
    die "AWS CLI could not export temporary credentials for profile '$AWS_PROFILE_VALUE'."
  AWS_ACCESS_KEY_ID="$(jq -r '.AccessKeyId' <<<"$credentials")"
  AWS_SECRET_ACCESS_KEY="$(jq -r '.SecretAccessKey' <<<"$credentials")"
  AWS_SESSION_TOKEN="$(jq -r '.SessionToken // empty' <<<"$credentials")"
  AWS_CREDENTIAL_EXPIRATION="$(jq -r '.Expiration // empty' <<<"$credentials")"
  export AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY AWS_SESSION_TOKEN AWS_CREDENTIAL_EXPIRATION
  aws --no-cli-pager --region "$AWS_REGION_VALUE" sts get-caller-identity >/dev/null ||
    die "AWS CLI exported invalid or expired credentials for profile '$AWS_PROFILE_VALUE'. Sign in to AWS and try again."
  if [[ -n "$AWS_CREDENTIAL_EXPIRATION" ]]; then
    log "AWS credentials refreshed; they expire at $AWS_CREDENTIAL_EXPIRATION."
  else
    log "AWS credentials refreshed."
  fi
}

terraform_init() {
  export AWS_REGION="$AWS_REGION_VALUE"
  export_temporary_aws_credentials
  terraform -chdir="$TF_DIR" init -reconfigure -input=false \
    -backend-config="key=$STATE_KEY"
  set_terraform_vars
}

terraform_output_json() {
  terraform -chdir="$TF_DIR" output -json
}

# ── Local env-file editing ──────────────────────────────────────
# Same upsert/remove helpers as humbugg's dev-aws-setup.sh, hoisted here
# because dev-aws-destroy.sh also uses remove_env to unwind the wiring.

upsert_env() {
  local file="$1" key="$2" value="$3" temp
  mkdir -p "$(dirname "$file")"
  touch "$file"
  temp="$(mktemp)"
  awk -v key="$key" -v value="$value" '
    BEGIN { found = 0 }
    $0 ~ "^" key "=" {
      if (!found) print key "=" value
      found = 1
      next
    }
    { print }
    END { if (!found) print key "=" value }
  ' "$file" > "$temp"
  mv "$temp" "$file"
}

remove_env() {
  local file="$1" key="$2" temp
  [[ -f "$file" ]] || return 0
  temp="$(mktemp)"
  awk -v key="$key" '$0 !~ "^" key "=" { print }' "$file" > "$temp"
  mv "$temp" "$file"
}
