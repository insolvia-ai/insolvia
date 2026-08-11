#!/usr/bin/env bash
#
# Stop the admin service's local stack — the counterpart to dev-up.sh.
#
# This exists because killing the process that ran `docker compose up` does
# NOT reliably stop its containers: one is left holding port 8090, and the
# next `dev-up.sh` fails to bind with an error that names the port rather than
# the cause. `compose down` is the only thing that actually finishes the job.
#
# Idempotent and safe to run when nothing is up: teardown that can fail is
# teardown you stop trusting.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADMIN_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

if ! command -v docker >/dev/null 2>&1; then
  printf '\033[1;34m[dev-down]\033[0m docker not found — nothing to stop.\n'
  exit 0
fi

cd "$ADMIN_DIR"
docker compose down --remove-orphans "$@"
printf '\033[1;32m[ ok ]\033[0m Admin service stack is down.\n'
