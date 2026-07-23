#!/usr/bin/env bash
#
# Run the marketing site's dev server (React Router v7, SSR).
#
# Without INSOLVIA_API_BASE_URL the waitlist form logs submissions server-side
# instead of calling the API (see app/lib/waitlist.server.ts). To exercise the
# real API locally, start it first (./services/api/scripts/dev-up.sh) and run:
#
#   INSOLVIA_API_BASE_URL=http://localhost:8080 ./apps/insolvia_marketing/scripts/dev-up.sh
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

if [[ ! -d "$APP_DIR/node_modules" ]]; then
  printf '\033[1;33m[warn]\033[0m node_modules missing — run ./apps/insolvia_marketing/scripts/dev-setup.sh first.\n' >&2
  exit 1
fi

if [[ -z "${INSOLVIA_API_BASE_URL:-}" ]]; then
  printf '\033[1;34m[dev-up]\033[0m INSOLVIA_API_BASE_URL is unset — waitlist submissions will be logged, not sent to an API.\n'
fi

cd "$APP_DIR"
exec npm run dev
