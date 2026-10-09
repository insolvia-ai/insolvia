#!/usr/bin/env bash
#
# Shared helpers for the per-machine AWS development scripts
# (dev-aws-setup.sh / dev-aws-reset.sh / dev-aws-destroy.sh /
# dev-aws-destroy-orphan.sh).
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
APP_DIR="$REPO_ROOT/apps/insolvia_app"
CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/insolvia"
MACHINE_ID_FILE="$CONFIG_DIR/machine-id"
STATE_BUCKET="insolvia-shared-terraform-state-us-east-1"

# The `default` AWS profile is Insolvia's (its own dedicated account —
# 521762924626). Override with --profile on any script, or AWS_PROFILE in the
# environment, if your Insolvia session lives under a different profile name.
AWS_PROFILE_VALUE="${AWS_PROFILE:-default}"
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
  WAITLIST_TABLE_NAME_EXPECTED="insolvia-$DEV_ENVIRONMENT-waitlist"
  CASE_TABLE_NAME_EXPECTED="insolvia-$DEV_ENVIRONMENT-cases"
  CASE_ACCESS_LOG_TABLE_NAME_EXPECTED="insolvia-$DEV_ENVIRONMENT-case-access-log"
  FIRM_TABLE_NAME_EXPECTED="insolvia-$DEV_ENVIRONMENT-firms"
  USER_POOL_NAME_EXPECTED="insolvia-$DEV_ENVIRONMENT-users"
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
  # LOAD-BEARING here, not a nicety: the AWS profile uses the new
  # `aws login` session format, which Terraform's AWS SDK cannot read — a bare
  # `terraform init/apply` against the profile fails to find credentials.
  # `aws configure export-credentials` refreshes the session and hands back
  # env-var credentials every SDK understands. This is for host-side tools
  # that finish within the credentials' lifetime; a long-running container
  # gets a refreshed set instead (start_container_aws_credentials, below).
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

# ── Credentials for a local container ───────────────────────────
#
# A compose stack (services/api, services/admin) cannot be handed exported
# keys: they are a snapshot, the `aws login` session behind them hands out
# credentials that live 15 minutes to an hour, and a long run then 500s with
# ExpiredTokenException. Nor can the container read the developer's
# `~/.aws` itself: the pinned boto3 predates the `login_session` profile
# format, and the botocore that understands it needs the crt extra AND writes
# the rotated refresh token back into ~/.aws/login/cache — a second writer
# racing the host CLI over a token that changes on every refresh, and a write
# a read-only mount refuses anyway.
#
# So the HOST stays the one process that talks to the login session. A
# background loop re-runs `aws configure export-credentials --format process`
# into a 0600 file in a 0700 directory under ~/.cache (never the repo), and
# the container mounts that directory read-only with a generated AWS config
# whose `credential_process` is `cat` of that file. botocore treats process
# credentials as refreshable: it re-runs the process before the Expiration
# the file carries, and so picks up each set the host writes. `cat` is all
# the image needs — no AWS CLI inside it.
#
# The compose files name the same directory: ${HOME}/.cache/insolvia/
# aws-container/<stack>, mounted at /run/insolvia-aws.

container_aws_dir() {
  printf '%s/.cache/insolvia/aws-container/%s' "$HOME" "$1"
}

write_container_aws_credentials() {
  # One refresh: never die (the loop calls this), never print a value. The
  # AWS_* env vars are dropped for the export so a stale exported set in the
  # caller's shell cannot shadow the profile (insolvia-aws-auth, mode #3).
  local dir="$1" tmp
  tmp="$(mktemp "$dir/.credentials.XXXXXX")" || return 1
  if env -u AWS_ACCESS_KEY_ID -u AWS_SECRET_ACCESS_KEY -u AWS_SESSION_TOKEN \
       -u AWS_CREDENTIAL_EXPIRATION \
       aws configure export-credentials --profile "$AWS_PROFILE_VALUE" --format process \
       >"$tmp" 2>/dev/null &&
     jq -e '.Version == 1 and (.AccessKeyId | length > 0) and (.Expiration | length > 0)' \
       "$tmp" >/dev/null 2>&1; then
    chmod 600 "$tmp"
    # A rename inside the mounted directory: the container never reads a
    # half-written file.
    mv -f "$tmp" "$dir/credentials.json"
  else
    rm -f "$tmp"
    return 1
  fi
}

start_container_aws_credentials() {
  # Prepare <stack>'s directory, write the first set (dying if the session is
  # dead, so that fails loudly here and not as a 500 later), then refresh it
  # in the background for as long as the calling process lives. Call it just
  # before `exec docker compose up`: exec keeps the PID, so the loop watches
  # compose itself, and removes the credentials when compose exits.
  local stack="$1" dir parent=$$ interval="${INSOLVIA_AWS_REFRESH_SECONDS:-60}"
  dir="$(container_aws_dir "$stack")"
  mkdir -p "$dir"
  chmod 700 "$dir"
  aws_dev sts get-caller-identity >/dev/null ||
    die "AWS profile '$AWS_PROFILE_VALUE' does not currently have a valid session. Sign in to AWS (aws login --profile $AWS_PROFILE_VALUE) and try again."
  cat >"$dir/config" <<EOF
# Generated by scripts/dev-aws-common.sh for the $stack container — no secret
# here; the credentials are the file next to it, rewritten by the host.
[default]
region = $AWS_REGION_VALUE
credential_process = cat /run/insolvia-aws/credentials.json
EOF
  write_container_aws_credentials "$dir" ||
    die "AWS CLI could not export temporary credentials for profile '$AWS_PROFILE_VALUE'."
  log "Container credentials for '$stack' expire at $(jq -r '.Expiration' "$dir/credentials.json"); the host refreshes them every ${interval}s."
  (
    trap 'rm -f "$dir/credentials.json"' EXIT
    elapsed=0
    while kill -0 "$parent" 2>/dev/null; do
      sleep 5
      elapsed=$((elapsed + 5))
      if (( elapsed >= interval )); then
        elapsed=0
        write_container_aws_credentials "$dir" ||
          warn "Could not refresh the $stack container's AWS credentials (session expired? aws login --profile $AWS_PROFILE_VALUE); retrying in ${interval}s."
      fi
    done
  ) &
}

stop_container_aws_credentials() {
  # For dev-down: the loop removes the file when compose exits, but a
  # SIGKILLed dev-up never got that far.
  rm -f "$(container_aws_dir "$1")/credentials.json"
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
# upsert/remove helpers hoisted here because dev-aws-destroy.sh also uses
# remove_env to unwind the wiring.

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
