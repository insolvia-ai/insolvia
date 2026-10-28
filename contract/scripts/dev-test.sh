#!/usr/bin/env bash
# The contract suite against the local Python contract stack — offline, no
# AWS, no sign-in anywhere.
#
#   contract/scripts/dev-test.sh            # run everything
#   contract/scripts/dev-test.sh api        # extra args go to vitest
#
# 1. builds contract/.venv on first use (the three services' runtime deps);
# 2. mints a throwaway RS256 key for this run;
# 3. starts contract/stack/contract_stack.py in the background and waits for
#    all three services to answer;
# 4. runs the Vitest suite against it, and stops the stack however that ends.
#
# To aim the suite at some other stack (the .NET one, later), skip this
# script: set the CONTRACT_* variables contract/src/support/env.ts names and
# run `npm run contract --workspace @insolvia-ai/contract`.
set -euo pipefail

CONTRACT="$(cd "$(dirname "$0")/.." && pwd)"
REPO="$(cd "$CONTRACT/.." && pwd)"
VENV="$CONTRACT/.venv"
PYTHON="${PYTHON:-python3.12}"

if [[ ! -x "$VENV/bin/python" ]]; then
  echo "creating $VENV"
  "$PYTHON" -m venv "$VENV"
  "$VENV/bin/pip" install --quiet --upgrade pip
fi
# Converges every run (fast when nothing moved). From contract/stack/: the
# requirements' relative paths resolve against the working directory.
(cd "$CONTRACT/stack" && "$VENV/bin/pip" install --quiet -r requirements.txt)

RUN="$(mktemp -d "${TMPDIR:-/tmp}/insolvia-contract.XXXXXX")"
STACK_PID=""
cleanup() {
  if [[ -n "$STACK_PID" ]]; then kill "$STACK_PID" 2>/dev/null || true; wait "$STACK_PID" 2>/dev/null || true; fi
  rm -rf "$RUN"
}
trap cleanup EXIT

node "$CONTRACT/scripts/make-signing-key.ts" "$RUN" >/dev/null

export CONTRACT_API_PORT="${CONTRACT_API_PORT:-18080}"
export CONTRACT_ADMIN_PORT="${CONTRACT_ADMIN_PORT:-18090}"
export CONTRACT_MCP_PORT="${CONTRACT_MCP_PORT:-18070}"
export CONTRACT_AUX_PORT="${CONTRACT_AUX_PORT:-18060}"

CONTRACT_JWKS="$RUN/jwks.json" "$VENV/bin/python" "$CONTRACT/stack/contract_stack.py" \
  >"$RUN/stack.log" 2>&1 &
STACK_PID=$!

export CONTRACT_API_URL="http://127.0.0.1:$CONTRACT_API_PORT"
export CONTRACT_ADMIN_URL="http://127.0.0.1:$CONTRACT_ADMIN_PORT"
export CONTRACT_MCP_URL="http://127.0.0.1:$CONTRACT_MCP_PORT/mcp"
export CONTRACT_ISSUER_URL="http://127.0.0.1:$CONTRACT_AUX_PORT/contract-pool"
export CONTRACT_SIGNING_KEY="$RUN/signing-key.pem"

ready() {
  curl -fsS -o /dev/null "$CONTRACT_API_URL/health" &&
    curl -fsS -o /dev/null "$CONTRACT_ADMIN_URL/health" &&
    curl -fsS -o /dev/null "http://127.0.0.1:$CONTRACT_MCP_PORT/.well-known/oauth-protected-resource/mcp"
}
for _ in $(seq 1 100); do
  if ready 2>/dev/null; then break; fi
  if ! kill -0 "$STACK_PID" 2>/dev/null; then
    echo "the contract stack exited before it was ready:" >&2
    cat "$RUN/stack.log" >&2
    exit 1
  fi
  sleep 0.2
done
if ! ready 2>/dev/null; then
  echo "the contract stack did not become ready:" >&2
  cat "$RUN/stack.log" >&2
  exit 1
fi

cd "$REPO"
status=0
npm run contract --workspace @insolvia-ai/contract -- "$@" || status=$?
if [[ $status -ne 0 && -n "${CONTRACT_SHOW_STACK_LOG:-}" ]]; then
  cat "$RUN/stack.log" >&2
fi
exit $status
