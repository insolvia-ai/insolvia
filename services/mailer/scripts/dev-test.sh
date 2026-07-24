#!/usr/bin/env bash
#
# Run the mailer's lint + format + test gate locally — the same commands, in
# the same order, as the `Mailer service` job in
# .github/workflows/mailer-pr.yml:
#   ruff check .  →  ruff format --check .  →  pytest
# (CI additionally builds the Lambda image; run that separately with
#  `docker build --target lambda services/mailer` when touching packaging.)
#
# Uses the venv created by dev-setup.sh so the tool versions match the pinned
# requirements-dev.txt, not whatever is on the machine.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MAILER_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
VENV="$MAILER_DIR/.venv"

if [[ ! -x "$VENV/bin/python" ]]; then
  printf '\033[1;33m[warn]\033[0m venv missing — run ./services/mailer/scripts/dev-setup.sh first.\n' >&2
  exit 1
fi

cd "$MAILER_DIR"
"$VENV/bin/ruff" check .
"$VENV/bin/ruff" format --check .
"$VENV/bin/pytest"
printf '\033[1;32m[ ok ]\033[0m lint, format, and tests all green.\n'
