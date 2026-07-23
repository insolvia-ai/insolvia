#!/usr/bin/env bash
#
# API service developer bootstrap: shared tools, a Python 3.12 venv with
# runtime + dev dependencies (pytest, ruff — the same set api-pr.yml
# installs), then this machine's per-developer AWS resources.
#
# The AWS layer is not optional for API development — it IS the dev database
# (humbugg pattern: no local emulator). The final step chains unconditionally
# into scripts/dev-aws-setup.sh, which provisions the per-machine waitlist
# table + Cognito pool (infra/envs/dev) and wires services/api/.env at them.
# (The in-memory store remains the fallback when WAITLIST_TABLE_NAME is unset
# — that is the test seam unit tests and the bare development server use, not
# the dev path.)
#
# The venv lives at services/api/.venv (gitignored via the root .gitignore).
# Python 3.12 matches pyproject's requires-python and the Lambda base image
# (public.ecr.aws/lambda/python:3.12); the shared base installs it via
# Homebrew's python@3.12 if missing.
#
# IDEMPOTENT: the shared base checks every tool before install, an existing
# venv is reused, pip re-resolves the pinned requirements to a no-op, and
# dev-aws-setup.sh's Terraform apply converges.
#
# Usage:
#   ./services/api/scripts/dev-setup.sh --profile insolvia
#   ./services/api/scripts/dev-setup.sh --profile insolvia --yes
#   ./services/api/scripts/dev-setup.sh --check    # report only
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
API_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

CHECK_ONLY=0
AUTO_APPROVE=0
AWS_PROFILE_VALUE="${AWS_PROFILE:-insolvia}"
AWS_REGION_VALUE="${AWS_REGION:-${AWS_DEFAULT_REGION:-us-east-1}}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --check) CHECK_ONLY=1 ;;
    --profile) [[ $# -ge 2 ]] || { printf '%s\n' '--profile requires a value.' >&2; exit 1; }; AWS_PROFILE_VALUE="$2"; shift ;;
    --region) [[ $# -ge 2 ]] || { printf '%s\n' '--region requires a value.' >&2; exit 1; }; AWS_REGION_VALUE="$2"; shift ;;
    --yes|-y) AUTO_APPROVE=1 ;;
    --help|-h)
      printf 'Usage: %s [--profile NAME] [--region REGION] [--yes] [--check]\n' "$0"
      printf 'Runs shared tool setup, the API venv, then per-machine AWS setup.\n'
      exit 0
      ;;
    *) printf 'Unknown option: %s\n' "$1" >&2; exit 1 ;;
  esac
  shift
done

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
  "$VENV/bin/pip" install --quiet -r "$API_DIR/requirements.txt" -r "$API_DIR/requirements-dev.txt"
fi

# Chained unconditionally, mirroring humbugg's per-service dev-setup: the
# per-machine AWS resources are the local dev database, not an add-on.
aws_args=(--profile "$AWS_PROFILE_VALUE" --region "$AWS_REGION_VALUE")
[[ "$CHECK_ONLY" -eq 1 ]] && aws_args+=(--check)
[[ "$AUTO_APPROVE" -eq 1 ]] && aws_args+=(--yes)
log "$([[ "$CHECK_ONLY" -eq 1 ]] && printf 'checking' || printf 'setting up') per-machine AWS development resources..."
"$REPO_ROOT/scripts/dev-aws-setup.sh" "${aws_args[@]}"

ok "API service is ready."
log "    ./services/api/scripts/dev-up.sh     # compose stack against YOUR real dev table"
log "    ./services/api/scripts/dev-test.sh   # ruff + pytest, same as CI"
