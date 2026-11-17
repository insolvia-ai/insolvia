#!/usr/bin/env bash
#
# Run the filing worker locally: the FAKE CM/ECF on 127.0.0.1, and the worker
# polling THIS machine's real dev filing queue as the filing worker's role.
#
#   ./services/filing/scripts/dev-up.sh                 # fault: none
#   ./services/filing/scripts/dev-up.sh --fault slow    # any of fake_cmecf.FAULTS
#
# Then approve a filing (the app's packet screen against ./services/api/
# scripts/dev-up.sh, or ./services/filing/scripts/dev-filing-proof.sh, which
# drives the whole flow itself) and watch it file against the fake. The fake's
# counters are at http://127.0.0.1:${FAKE_CMECF_PORT:-8790}/__fake/state.
#
# The fake is the only court this worker can reach: INSOLVIA_ENV=local's
# host fence allows loopback alone (core/fence.py), and FAKE_CMECF_URL is
# refused outside local. Enrol the FAKE's account (GET /__fake/account) — never
# a real PACER login — through the app's credential screen.
#
# Ctrl-C stops both. The poller's assumed-role session lasts an hour; restart
# this script after that.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FILING_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
VENV="$FILING_DIR/.venv"
PORT="${FAKE_CMECF_PORT:-8790}"
FAULT=none

while [[ $# -gt 0 ]]; do
  case "$1" in
    --fault) [[ $# -ge 2 ]] || { printf '%s\n' '--fault requires a value.' >&2; exit 1; }; FAULT="$2"; shift ;;
    --help|-h) sed -n '2,21p' "$0"; exit 0 ;;
    *) printf 'Unknown option: %s\n' "$1" >&2; exit 1 ;;
  esac
  shift
done

if [[ ! -x "$VENV/bin/python" ]]; then
  printf '\033[1;33m[warn]\033[0m venv missing — run ./services/filing/scripts/dev-setup.sh first.\n' >&2
  exit 1
fi
if [[ ! -f "$FILING_DIR/.env" ]]; then
  printf '\033[1;33m[warn]\033[0m services/filing/.env not found — run ./scripts/dev-aws-setup.sh.\n' >&2
  exit 1
fi

set -a
# shellcheck disable=SC1091
source "$FILING_DIR/.env"
set +a
export INSOLVIA_ENV=local
export FAKE_CMECF_URL="http://127.0.0.1:${PORT}"
export PYTHONPATH="$FILING_DIR/src:$FILING_DIR/../api/src:$FILING_DIR/fake"

cd "$FILING_DIR"
"$VENV/bin/python" -m fake_cmecf --port "$PORT" --fault "$FAULT" &
fake_pid=$!
trap 'kill "$fake_pid" 2>/dev/null || true' EXIT INT TERM
sleep 1
"$VENV/bin/python" -m insolvia_filing.entrypoints.filing_poller
