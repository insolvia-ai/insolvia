#!/usr/bin/env bash
#
# Run the API locally: `docker compose up --build` on services/api's compose
# stack (development server + amazon/dynamodb-local + one-shot table init).
# Then: curl http://127.0.0.1:8080/health
#
# Running WITHOUT compose also works — with WAITLIST_TABLE_NAME unset the
# service falls back to the in-memory store (see docker-compose.yml).
#
# Per-machine AWS mode (opt-in — see scripts/dev-aws-setup.sh): when
# services/api/.env carries AWS_PROFILE, the waitlist table is this machine's
# real DynamoDB table, so before `up` this script exports short-lived
# credentials from that profile into the shell (compose substitutes them into
# the container; shell env beats .env) and forces a recreate so an existing
# container never keeps expired credentials. Without that .env marker the
# stack is the zero-AWS dynamodb-local default and none of this runs.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
API_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

if ! command -v docker >/dev/null 2>&1; then
  printf '\033[1;33m[warn]\033[0m docker not found — install Docker Desktop (macOS) or docker engine (Linux).\n' >&2
  exit 1
fi

compose_args=(up --build)

env_file="$API_DIR/.env"
if [[ -f "$env_file" ]] && grep -q '^AWS_PROFILE=' "$env_file"; then
  # Sourcing resets SCRIPT_DIR (to the repo scripts dir) and defines the
  # dev-aws helpers; API_DIR/REPO_ROOT above are already resolved.
  # shellcheck source=/dev/null
  source "$REPO_ROOT/scripts/dev-aws-common.sh"
  AWS_PROFILE_VALUE="$(sed -n 's/^AWS_PROFILE=//p' "$env_file" | tail -n 1)"
  region_from_env="$(sed -n 's/^AWS_DEFAULT_REGION=//p' "$env_file" | tail -n 1)"
  [[ -n "$region_from_env" ]] && AWS_REGION_VALUE="$region_from_env"
  for command in aws jq; do require_command "$command"; done
  log "services/api/.env carries AWS_PROFILE=$AWS_PROFILE_VALUE — exporting short-lived credentials for the real waitlist table."
  export_temporary_aws_credentials
  export AWS_DEFAULT_REGION="$AWS_REGION_VALUE"
  # Credentials are injected environment values: always replace any existing
  # container so it cannot hold an expired set.
  compose_args+=(--force-recreate)
fi

cd "$API_DIR"
exec docker compose "${compose_args[@]}" "$@"
