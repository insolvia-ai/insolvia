#!/usr/bin/env bash
#
# Stop the local .NET backend — the counterpart to dev-up.sh.
#
# Killing the process that ran `docker compose up` does not reliably stop its
# containers: one is left holding 8080 or 8090, and the next dev-up fails to
# bind. `compose down` finishes the job. The database volume survives; wipe it
# with `./scripts/dev-aws-reset.sh` (or `docker compose -f
# backend/docker-compose.yml down -v`).
#
# Idempotent and safe to run when nothing is up.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

if ! command -v docker >/dev/null 2>&1; then
  printf '\033[1;34m[dev-down]\033[0m docker not found — nothing to stop.\n'
  exit 0
fi

cd "$BACKEND_DIR"
docker compose down --remove-orphans "$@"
printf '\033[1;32m[ ok ]\033[0m backend stack is down.\n'
