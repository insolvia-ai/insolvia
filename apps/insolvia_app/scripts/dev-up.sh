#!/usr/bin/env bash
#
# Run the Insolvia app with the FVM-pinned Flutter. Device selection is left to
# Flutter — it prompts when more than one device is available; pass `-d chrome`
# / `-d macos` (or any `flutter run` flag) to choose directly.
#
# Environment defaults to `local`; override with
#   ./apps/insolvia_app/scripts/dev-up.sh --dart-define=INSOLVIA_ENV=staging
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

cd "$APP_DIR"
exec fvm flutter run "$@"
