#!/usr/bin/env bash
#
# Run the Insolvia app in a browser with fast refresh.
#
# Port 3000 is pinned, not preferred: infra/envs/{dev,staging} register
# http://localhost:3000 as an EXACT-MATCH Cognito allowed origin, and Expo's own
# default is 8081, which that origin list rejects. Change it in both places or
# not at all.
#
# Web is the only target built today. Any `expo start` flag passes through, so
# `--clear` (reset the Metro cache) and `--https` work as usual.
#
# Environment defaults to `local`; override with
#   EXPO_PUBLIC_INSOLVIA_ENV=staging ./apps/insolvia_app/scripts/dev-up.sh
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

cd "$APP_DIR"
exec npx expo start --web --port 3000 "$@"
