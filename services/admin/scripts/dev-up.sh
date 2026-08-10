#!/usr/bin/env bash
#
# Start the admin service's compose stack on http://127.0.0.1:8090.
#
# With services/admin/.env present (written by scripts/dev-aws-setup.sh once
# the dev admin infra exists — #213), the container talks to this machine's
# real dev resources; without it, everything is in-memory and the service
# still runs. AWS credentials are exported fresh into the shell and never
# written to a file — same mechanism as the API's dev-up.sh.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADMIN_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

# shellcheck source=/dev/null
source "$REPO_ROOT/scripts/dev-aws-common.sh"

if aws_dev sts get-caller-identity >/dev/null 2>&1; then
  export_temporary_aws_credentials
else
  warn "No AWS session — starting with in-memory adapters only."
fi

cd "$ADMIN_DIR"
docker compose up --build --force-recreate
