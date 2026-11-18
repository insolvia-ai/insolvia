#!/usr/bin/env bash
#
# Run the API locally: `docker compose up --build` on services/api's compose
# stack — Postgres (migrated on start), the local S3 server and the API.
# Then: curl http://127.0.0.1:8080/health
#
# Needs no AWS session (#624, ADR 0030 row 5). The one AWS service is the
# shared dev Cognito pool, and verifying its tokens needs no credentials —
# only the pool's public ids, which services/api/.env holds
# (./scripts/dev-aws-seed.sh writes them from SSM, the one step that needs an
# AWS login, once). Without them the API still starts and every protected
# route answers 401.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
API_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

die() { printf '\033[1;31m[error]\033[0m %s\n' "$*" >&2; exit 1; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*" >&2; }

command -v docker >/dev/null 2>&1 ||
  die "docker not found — install Docker Desktop (macOS) or docker engine (Linux)."

grep -q '^AUTH_ISSUER_URL=.' "$API_DIR/.env" 2>/dev/null ||
  warn "services/api/.env has no AUTH_ISSUER_URL — signed-in routes will answer 401. Run ./scripts/dev-aws-seed.sh once."

cd "$API_DIR"
exec docker compose up --build "$@"
