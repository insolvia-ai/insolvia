#!/usr/bin/env bash
#
# Start the admin service's compose stack on http://127.0.0.1:8090.
#
# With services/admin/.env present (written by scripts/dev-aws-setup.sh once
# the dev admin infra exists — #213), the container talks to this machine's
# real dev resources; without it, everything is in-memory and the service
# still runs. AWS credentials reach the container the same way as the API's
# dev-up.sh: a host-refreshed file under ~/.cache/insolvia/aws-container/
# admin, read through a generated credential_process config, so they do not
# expire under a long-running stack.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADMIN_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

# shellcheck source=/dev/null
source "$REPO_ROOT/scripts/dev-aws-common.sh"

if aws_dev sts get-caller-identity >/dev/null 2>&1; then
  start_container_aws_credentials admin
else
  warn "No AWS session — starting with in-memory adapters only."
  # No config, no credentials: the container's SDK finds nothing, as before.
  rm -f "$(container_aws_dir admin)/config"
  stop_container_aws_credentials admin
fi

cd "$ADMIN_DIR"
# exec keeps this PID, which is what the refresh loop watches.
exec docker compose up --build --force-recreate
