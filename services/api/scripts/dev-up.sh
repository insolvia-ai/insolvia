#!/usr/bin/env bash
#
# Run the API locally: `docker compose up --build` on services/api's compose
# stack, pointed at this machine's REAL per-developer AWS waitlist table
# (there is no local DynamoDB emulator).
# Then: curl http://127.0.0.1:8080/health
#
# REQUIRES the per-machine AWS layer: ./scripts/dev-aws-setup.sh provisions
# the table (infra/envs/dev) and writes services/api/.env with
# WAITLIST_TABLE_NAME + AWS_PROFILE. The container never sees that profile
# or a snapshot of keys: this script writes short-lived credentials from it
# to a 0600 file under ~/.cache/insolvia/aws-container/api (never the repo),
# keeps rewriting it from the host for as long as compose runs, and the
# container reads it through a generated `credential_process` config that
# botocore re-runs before each set expires — so a run longer than the
# session's credential lifetime keeps working
# (start_container_aws_credentials in scripts/dev-aws-common.sh says why it
# is not a ~/.aws mount). The file is removed when compose exits.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
API_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

# Sourcing resets SCRIPT_DIR (to the repo scripts dir) and defines the
# dev-aws helpers (log/die, export_temporary_aws_credentials); API_DIR and
# REPO_ROOT above are already resolved.
# shellcheck source=/dev/null
source "$REPO_ROOT/scripts/dev-aws-common.sh"

if ! command -v docker >/dev/null 2>&1; then
  die "docker not found — install Docker Desktop (macOS) or docker engine (Linux)."
fi

env_file="$API_DIR/.env"
if [[ ! -f "$env_file" ]] || ! grep -q '^WAITLIST_TABLE_NAME=' "$env_file"; then
  die "services/api/.env is missing or lacks WAITLIST_TABLE_NAME — local dev runs against this machine's real AWS table. Run ./scripts/dev-aws-setup.sh first."
fi

profile_from_env="$(sed -n 's/^AWS_PROFILE=//p' "$env_file" | tail -n 1)"
[[ -n "$profile_from_env" ]] && AWS_PROFILE_VALUE="$profile_from_env"
region_from_env="$(sed -n 's/^AWS_DEFAULT_REGION=//p' "$env_file" | tail -n 1)"
[[ -n "$region_from_env" ]] && AWS_REGION_VALUE="$region_from_env"

for command in aws jq; do require_command "$command"; done
log "Refreshing short-lived credentials from AWS profile '$AWS_PROFILE_VALUE' for the API container."
start_container_aws_credentials api
export AWS_DEFAULT_REGION="$AWS_REGION_VALUE"

cd "$API_DIR"
# Recreate so a container from before this mechanism (which held a static
# key set in its environment) never survives into this run. exec keeps this
# PID, which is what the refresh loop watches.
exec docker compose up --build --force-recreate "$@"
