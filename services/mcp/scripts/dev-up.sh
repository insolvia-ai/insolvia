#!/usr/bin/env bash
#
# Run the MCP server locally, against this machine's REAL dev AWS resources —
# the same per-developer tables and Cognito pool the API's dev stack uses
# (there is no emulator; infra/envs/dev is the dev database).
#
# Reads services/mcp/.env (written by scripts/dev-aws-setup.sh): the table
# names, the dev pool's issuer URL, and the MCP app client id. Without the
# file, the server still starts with in-memory stores and NO auth — every
# call answers 401, which is the honest fail-closed shape, and only the
# protocol plumbing can be poked at.
#
# Then connect an MCP client. The reference check is the MCP inspector:
#
#   npx @modelcontextprotocol/inspector
#
# and point it at http://127.0.0.1:8788/mcp (transport: streamable HTTP).
# The inspector discovers the dev pool through the server's
# protected-resource metadata and runs the OAuth flow against it — sign in
# with the dev account scripts/dev-aws-seed.sh created (password context in
# ~/.config/insolvia/dev.env). See services/mcp/README.md for the
# step-by-step, including the manual-token fallback for clients that cannot
# run the browser flow.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MCP_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
VENV="$MCP_DIR/.venv"
PORT="${MCP_PORT:-8788}"

if [[ ! -x "$VENV/bin/python" ]]; then
  printf '\033[1;33m[warn]\033[0m venv missing — run ./services/mcp/scripts/dev-setup.sh first.\n' >&2
  exit 1
fi

if [[ -f "$MCP_DIR/.env" ]]; then
  # The file is KEY=VALUE lines written by dev-aws-setup.sh — identifiers,
  # never secrets.
  set -a
  # shellcheck disable=SC1091
  source "$MCP_DIR/.env"
  set +a
else
  printf '\033[1;33m[warn]\033[0m services/mcp/.env not found — starting with in-memory stores and auth failing closed.\n' >&2
  printf '\033[1;33m[warn]\033[0m run ./scripts/dev-aws-setup.sh to wire this machine%s real dev resources.\n' "'s" >&2
fi

export INSOLVIA_ENV=local
export MCP_RESOURCE_URL="http://127.0.0.1:${PORT}/mcp"

cd "$MCP_DIR"
exec "$VENV/bin/python" -m uvicorn \
  --host 127.0.0.1 --port "$PORT" \
  --app-dir src \
  insolvia_mcp.entrypoints.development_server:app
