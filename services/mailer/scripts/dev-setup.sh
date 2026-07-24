#!/usr/bin/env bash
#
# Mailer service developer bootstrap: shared tools, then a Python 3.12 venv
# with runtime + dev dependencies (pytest, ruff — the same set mailer-pr.yml
# installs).
#
# Unlike services/api, there is no per-machine AWS layer here: local mailer
# development is the pure-Mailpit loop (./scripts/dev-up.sh), which needs no
# AWS credentials at all — the dev server only ever talks to the Mailpit
# container in docker-compose.yml. AWS wiring for the deployed service lands
# in a later PR's infra/ change, not here.
#
# The venv lives at services/mailer/.venv (gitignored via the root
# .gitignore's Python section). Python 3.12 matches pyproject's
# requires-python and the Lambda base image
# (public.ecr.aws/lambda/python:3.12); the shared base installs it via
# Homebrew's python@3.12 if missing.
#
# IDEMPOTENT: the shared base checks every tool before install, an existing
# venv is reused, and pip re-resolves the pinned requirements to a no-op.
#
# Usage:
#   ./services/mailer/scripts/dev-setup.sh
#   ./services/mailer/scripts/dev-setup.sh --check    # report only
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MAILER_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

CHECK_ONLY=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --check) CHECK_ONLY=1 ;;
    --help|-h)
      printf 'Usage: %s [--check]\n' "$0"
      printf 'Runs shared tool setup, then the mailer venv.\n'
      exit 0
      ;;
    *) printf 'Unknown option: %s\n' "$1" >&2; exit 1 ;;
  esac
  shift
done

log()  { printf '\033[1;34m[mailer-setup]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*"; }
have() { command -v "$1" >/dev/null 2>&1; }

log "checking shared developer dependencies..."
if [[ "$CHECK_ONLY" -eq 1 ]]; then
  "$REPO_ROOT/scripts/dev-setup.sh" --check
else
  "$REPO_ROOT/scripts/dev-setup.sh"
fi

VENV="$MAILER_DIR/.venv"

if [[ "$CHECK_ONLY" -eq 1 ]]; then
  if [[ -x "$VENV/bin/python" ]]; then
    ok "venv present: $VENV ($("$VENV/bin/python" --version))"
  else
    warn "venv missing (would: python3.12 -m venv $VENV + pip install)"
  fi
else
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
  "$VENV/bin/pip" install --quiet -r "$MAILER_DIR/requirements.txt" -r "$MAILER_DIR/requirements-dev.txt"
fi

ok "Mailer service is ready."
log "    ./services/mailer/scripts/dev-up.sh     # mailpit + mailer dev server, no AWS creds needed"
log "    ./services/mailer/scripts/dev-test.sh   # ruff + pytest, same as CI"
