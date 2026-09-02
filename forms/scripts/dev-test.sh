#!/usr/bin/env bash
# Validate the form field specs exactly as CI does. Stdlib-only Python — no
# venv, no dependencies; any python3 will do.
set -euo pipefail

cd "$(dirname "$0")/../.."
exec python3 forms/scripts/check.py
