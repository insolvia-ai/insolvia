#!/usr/bin/env bash
#
# MCP service developer bootstrap: shared tools, a Python 3.12 venv with
# runtime + dev dependencies (the same set mcp-pr.yml installs), then this
# machine's per-developer AWS resources.
#
# The AWS layer is not optional for MCP development — this service reads the
# SAME per-machine dev tables and Cognito pool the API does (ADR 0016: a
# second surface over the same stores), and there is no local emulator. The
# final step chains into scripts/dev-aws-setup.sh, which provisions them
# (infra/envs/dev) and wires services/mcp/.env at them.
#
# IDEMPOTENT: an existing venv is reused, pip re-resolves the pins to a
# no-op, and dev-aws-setup.sh's Terraform apply converges.
#
# Usage:
#   ./services/mcp/scripts/dev-setup.sh            # uses the default AWS profile
#   ./services/mcp/scripts/dev-setup.sh --yes
#   ./services/mcp/scripts/dev-setup.sh --check    # report only
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MCP_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

CHECK_ONLY=0
AUTO_APPROVE=0
AWS_PROFILE_VALUE="${AWS_PROFILE:-default}"
AWS_REGION_VALUE="${AWS_REGION:-${AWS_DEFAULT_REGION:-us-east-1}}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --check) CHECK_ONLY=1 ;;
    --profile) [[ $# -ge 2 ]] || { printf '%s\n' '--profile requires a value.' >&2; exit 1; }; AWS_PROFILE_VALUE="$2"; shift ;;
    --region) [[ $# -ge 2 ]] || { printf '%s\n' '--region requires a value.' >&2; exit 1; }; AWS_REGION_VALUE="$2"; shift ;;
    --yes|-y) AUTO_APPROVE=1 ;;
    --help|-h)
      printf 'Usage: %s [--profile NAME] [--region REGION] [--yes] [--check]\n' "$0"
      printf 'Runs shared tool setup, the MCP venv, then per-machine AWS setup.\n'
      exit 0
      ;;
    *) printf 'Unknown option: %s\n' "$1" >&2; exit 1 ;;
  esac
  shift
done

log()  { printf '\033[1;34m[mcp-setup]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*"; }
have() { command -v "$1" >/dev/null 2>&1; }

log "checking shared developer dependencies..."
if [[ "$CHECK_ONLY" -eq 1 ]]; then
  "$REPO_ROOT/scripts/dev-setup.sh" --check
else
  "$REPO_ROOT/scripts/dev-setup.sh"
fi

VENV="$MCP_DIR/.venv"

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
  "$VENV/bin/pip" install --quiet -r "$MCP_DIR/requirements.txt" -r "$MCP_DIR/requirements-dev.txt"
fi

# Chained unconditionally: the per-machine AWS resources are the local dev
# database and the local Cognito pool, not an add-on.
aws_args=(--profile "$AWS_PROFILE_VALUE" --region "$AWS_REGION_VALUE")
[[ "$CHECK_ONLY" -eq 1 ]] && aws_args+=(--check)
[[ "$AUTO_APPROVE" -eq 1 ]] && aws_args+=(--yes)
log "$([[ "$CHECK_ONLY" -eq 1 ]] && printf 'checking' || printf 'setting up') per-machine AWS development resources..."
"$REPO_ROOT/scripts/dev-aws-setup.sh" "${aws_args[@]}"

ok "MCP service is ready."
log "    ./services/mcp/scripts/dev-up.sh     # streamable-HTTP server against YOUR real dev tables"
log "    ./services/mcp/scripts/dev-test.sh   # ruff + mypy + pytest, same as CI"
