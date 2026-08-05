#!/usr/bin/env bash
#
# Stop the mailer's local stack — the counterpart to dev-up.sh.
#
# Takes Mailpit down with it, which also discards every message captured in
# this session. That is the intent: Mailpit has no outbound relay, so its
# contents are dev scratch rather than anything to preserve.
#
# Idempotent and safe to run when nothing is up.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MAILER_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

if ! command -v docker >/dev/null 2>&1; then
  printf '\033[1;34m[dev-down]\033[0m docker not found — nothing to stop.\n'
  exit 0
fi

cd "$MAILER_DIR"
docker compose down --remove-orphans "$@"
printf '\033[1;32m[ ok ]\033[0m Mailer stack is down.\n'
