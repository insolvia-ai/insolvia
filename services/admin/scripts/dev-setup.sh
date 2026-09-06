#!/usr/bin/env bash
#
# Admin service developer bootstrap: a Python 3.12 venv with runtime + dev
# dependencies — the same set admin-pr.yml installs. Python 3.12 matches
# pyproject's requires-python and the Lambda base image.
#
# Deliberately does NOT chain into scripts/dev-aws-setup.sh the way the API's
# dev-setup does: the admin service's suite and bare dev server run entirely
# in-memory, and the per-machine AWS layer it will eventually talk to (the
# dev admin audit table, services/admin/.env wiring) arrives with the
# service's infra (#213). Run the API's dev-setup for the AWS layer today.
#
# IDEMPOTENT: an existing venv is reused; pip re-resolves pins to a no-op.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADMIN_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
VENV="$ADMIN_DIR/.venv"

PYTHON="${PYTHON:-python3.12}"
command -v "$PYTHON" >/dev/null 2>&1 ||
  { printf '\033[1;31m[error]\033[0m %s not found — install Python 3.12 (brew install python@3.12).\n' "$PYTHON" >&2; exit 1; }

if [[ ! -x "$VENV/bin/python" ]]; then
  "$PYTHON" -m venv "$VENV"
fi
# FROM THE SERVICE DIRECTORY, not from wherever this script was invoked:
# requirements.txt installs packages/insolvia_core by the RELATIVE path
# `../../packages/insolvia_core`, and pip resolves that against the current
# working directory, not the requirements file. Run from the repo root it
# fails with "Invalid requirement ... looks like a path" — which is how CI
# (working-directory: services/<x>) never saw this and a laptop did.
(cd "$ADMIN_DIR" && "$VENV/bin/pip" install --quiet -r requirements.txt -r requirements-dev.txt)
printf '\033[1;32m[ ok ]\033[0m services/admin venv ready. Gate: ./services/admin/scripts/dev-test.sh\n'
