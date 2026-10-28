#!/usr/bin/env bash
# The forms parity gate (plan 18.16): pypdf 6.16.2 against Insolvia.Forms.
#
#   backend/tests/Insolvia.Forms.Tests/Parity/run.sh [out-dir]
#
# Needs a Python 3.12 with services/api's requirements plus pypdfium2 and
# pillow (CI: backend-pr.yml's "Forms parity" step; locally a hand-made venv,
# never services/*/scripts/dev-setup.sh, which touches AWS):
#
#   python3.12 -m venv /tmp/parity-venv
#   (cd services/api && /tmp/parity-venv/bin/pip install -r requirements.txt -r ../../backend/tests/Insolvia.Forms.Tests/Parity/requirements.txt)
#   PYTHON=/tmp/parity-venv/bin/python backend/tests/Insolvia.Forms.Tests/Parity/run.sh
#
# Writes the renders and report.json under the out dir (default: a fresh
# temp dir) and exits non-zero on any difference beyond quirk 1
# (src/Insolvia.Forms/README.md).
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
out="${1:-$(mktemp -d)}"
python="${PYTHON:-python3}"
export PYTHONPATH="$repo/services/api/src:$repo/packages/insolvia_core/src"

cd "$repo"
rm -rf "$out/inputs" "$out/python" "$out/dotnet"
"$python" backend/tests/Insolvia.Forms.Tests/Parity/dump.py "$out"
INSOLVIA_FORMS_PARITY_DIR="$out" dotnet test backend/tests/Insolvia.Forms.Tests \
  --filter "FullyQualifiedName~ParityExportTests" --logger "console;verbosity=minimal"
"$python" backend/tests/Insolvia.Forms.Tests/Parity/compare.py "$out"
