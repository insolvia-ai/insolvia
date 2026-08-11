#!/usr/bin/env bash
#
# Run the admin portal's dev server (Vite) on http://localhost:3100.
#
# The port is pinned and strict: the dev Google OAuth client registers
# http://localhost:3100 as an EXACT redirect origin (the committed client id
# in src/config/environment.ts), so Vite quietly picking 3101 would break
# sign-in with a Google error page rather than anything local.
#
# The dev build targets `local`, whose apiBaseUrl is http://127.0.0.1:8090 —
# start the admin service first for anything beyond the sign-in screen:
#
#   ./services/admin/scripts/dev-up.sh
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

if [[ ! -d "$APP_DIR/node_modules" ]]; then
  printf '\033[1;33m[warn]\033[0m node_modules missing — run ./apps/insolvia_admin/scripts/dev-setup.sh first.\n' >&2
  exit 1
fi

cd "$APP_DIR"
exec npm run dev
