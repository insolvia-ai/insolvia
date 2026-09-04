#!/usr/bin/env bash
#
# Run the MCP service's lint + format + type + test gate locally — the same
# commands, in the same order, as the `MCP service` job in
# .github/workflows/mcp-pr.yml:
#   ruff check .  →  ruff format --check .  →  mypy  →  pytest
# (CI additionally builds the Lambda image; run that separately with
#  `docker build --target lambda -f services/mcp/Dockerfile .` FROM THE REPO
#  ROOT when touching packaging — the context must see packages/insolvia_core.)
#
# Uses the venv created by dev-setup.sh so the tool versions match the pinned
# requirements-dev.txt, not whatever is on the machine.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MCP_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
VENV="$MCP_DIR/.venv"

if [[ ! -x "$VENV/bin/python" ]]; then
  printf '\033[1;33m[warn]\033[0m venv missing — run ./services/mcp/scripts/dev-setup.sh first.\n' >&2
  exit 1
fi

cd "$MCP_DIR"
"$VENV/bin/ruff" check .
"$VENV/bin/ruff" format --check .
"$VENV/bin/mypy"
"$VENV/bin/pytest"
printf '\033[1;32m[ ok ]\033[0m lint, format, and tests all green.\n'
