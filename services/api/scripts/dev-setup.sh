#!/usr/bin/env bash
#
# API service developer bootstrap: shared tools, then a Python 3.12 venv with
# runtime + dev dependencies (pytest, ruff — the same set api-pr.yml installs).
#
# The venv lives at services/api/.venv (gitignored via the root .gitignore).
# Python 3.12 matches pyproject's requires-python and the Lambda base image
# (public.ecr.aws/lambda/python:3.12); the shared base installs it via
# Homebrew's python@3.12 if missing.
#
# IDEMPOTENT: the shared base checks every tool before install, an existing
# venv is reused, and pip re-resolves the pinned requirements to a no-op.
#
# Usage:
#   ./services/api/scripts/dev-setup.sh            # full setup
#   ./services/api/scripts/dev-setup.sh --check    # report only
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
API_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

CHECK_ONLY=0
[[ "${1:-}" == "--check" ]] && CHECK_ONLY=1

log()  { printf '\033[1;34m[api-setup]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*"; }
have() { command -v "$1" >/dev/null 2>&1; }

log "checking shared developer dependencies..."
if [[ "$CHECK_ONLY" -eq 1 ]]; then
  "$REPO_ROOT/scripts/dev-setup.sh" --check
else
  "$REPO_ROOT/scripts/dev-setup.sh"
fi

VENV="$API_DIR/.venv"

if [[ "$CHECK_ONLY" -eq 1 ]]; then
  if [[ -x "$VENV/bin/python" ]]; then
    ok "venv present: $VENV ($("$VENV/bin/python" --version))"
  else
    warn "venv missing (would: python3.12 -m venv $VENV + pip install)"
  fi
  exit 0
fi

if ! have python3.12; then
  warn "python3.12 not on PATH after shared setup — cannot create the venv."
  exit 1
fi

if [[ ! -x "$VENV/bin/python" ]]; then
  log "creating venv at $VENV ..."
  python3.12 -m venv "$VENV"
else
  ok "venv already present: $VENV"
fi

log "installing Python dependencies (runtime + dev)..."
"$VENV/bin/pip" install --quiet --upgrade pip
"$VENV/bin/pip" install --quiet -r "$API_DIR/requirements.txt" -r "$API_DIR/requirements-dev.txt"

ok "API service is ready."
log "    ./services/api/scripts/dev-up.sh     # compose stack (API + dynamodb-local)"
log "    ./services/api/scripts/dev-test.sh   # ruff + pytest, same as CI"
# Deliberately NOT chained here (humbugg's dev-setup runs its dev-aws-setup
# unconditionally; Insolvia's compose+dynamodb-local default needs zero AWS,
# so the AWS layer stays opt-in):
log "optional — real per-machine AWS resources (waitlist table + Cognito pool):"
log "    ./scripts/dev-aws-setup.sh --profile insolvia    # see scripts/README.md"
