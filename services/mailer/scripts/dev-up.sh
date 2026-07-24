#!/usr/bin/env bash
#
# Run the mailer locally: `docker compose up --build` starts Mailpit plus the
# mailer's gunicorn dev server (services/mailer/Dockerfile's `development`
# target, CMD insolvia_mailer.entrypoints.development_server:app).
#
# Unlike services/api, this loop needs NO AWS credentials: the dev server's
# only external dependency is the Mailpit container in this same compose
# file, and Mailpit has no outbound relay configured — nothing sent here can
# leave the machine as a real email.
#
# Mailer API : http://127.0.0.1:8026
# Mailpit    : http://127.0.0.1:8025
#
# Try it:
#   curl -i http://127.0.0.1:8026/v1/services/insolvia_api/messages \
#     -H 'content-type: application/json' \
#     --data @contracts/examples/message.json
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MAILER_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

if ! command -v docker >/dev/null 2>&1; then
  printf '\033[1;33m[warn]\033[0m docker not found — install Docker Desktop (macOS) or docker engine (Linux).\n' >&2
  exit 1
fi

cd "$MAILER_DIR"
exec docker compose up --build "$@"
