#!/usr/bin/env bash
#
# Run the filing worker's lint + format + type + test gate locally — the same
# commands, in the same order, as the `Filing worker` job in
# .github/workflows/filing-pr.yml:
#   ruff check .  →  ruff format --check .  →  mypy  →  pytest
# (CI additionally builds the Lambda image; run that separately with
#  `docker build --target lambda -f services/filing/Dockerfile .` FROM THE
#  REPO ROOT when touching packaging — the context must see
#  packages/insolvia_core and services/api/src.)
#
# The unit tier includes the end-to-end runs against the fake CM/ECF: it
# starts in-process on 127.0.0.1, and the host fence allows nothing else.
# No AWS, no network.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FILING_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
VENV="$FILING_DIR/.venv"

if [[ ! -x "$VENV/bin/python" ]]; then
  printf '\033[1;33m[warn]\033[0m venv missing — run ./services/filing/scripts/dev-setup.sh first.\n' >&2
  exit 1
fi

cd "$FILING_DIR"
"$VENV/bin/ruff" check .
"$VENV/bin/ruff" format --check .
"$VENV/bin/mypy"
"$VENV/bin/pytest"
printf '\033[1;32m[ ok ]\033[0m lint, format, types and tests all green.\n'
