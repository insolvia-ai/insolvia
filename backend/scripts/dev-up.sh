#!/usr/bin/env bash
#
# Run the local .NET backend: `docker compose up --build` on
# backend/docker-compose.yml — Postgres 17, the migrator, then the API on
# :8080 and the staff admin service on :8090.
#
#   ./backend/scripts/dev-up.sh
#   curl http://127.0.0.1:8080/health
#
# Compose reads backend/.env (scripts/dev-aws-setup.sh writes it) for the
# shared dev pool's ids and the admin portal's Google client id; the compose
# file's header says what each value does and what happens without it. No AWS
# credentials enter the containers: token verification needs only the pool's
# public JWKS, and the blob store and sealing keys are local twins.
#
# scripts/dev-up.sh starts this beside the app, the admin portal and
# marketing; scripts/dev-down.sh (or ./backend/scripts/dev-down.sh) stops it.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

if ! command -v docker >/dev/null 2>&1; then
  printf '\033[1;31m[fail]\033[0m docker not found — install Docker Desktop (macOS) or docker engine (Linux).\n' >&2
  exit 1
fi

[[ -f "$BACKEND_DIR/.env" ]] ||
  printf '\033[1;33m[warn]\033[0m backend/.env is missing — protected routes will answer 401. Run ./scripts/dev-aws-setup.sh to wire the shared dev pool.\n' >&2

cd "$BACKEND_DIR"
# exec keeps this PID, which is what scripts/dev-up.sh watches.
exec docker compose up --build "$@"
