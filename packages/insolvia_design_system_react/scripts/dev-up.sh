#!/usr/bin/env bash
#
# Run Storybook for the React design system (port 6006) — the component
# workbench for the six marketing-site components.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PKG_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

if [[ ! -d "$PKG_DIR/node_modules" ]]; then
  printf '\033[1;33m[warn]\033[0m node_modules missing — run ./packages/insolvia_design_system_react/scripts/dev-setup.sh first.\n' >&2
  exit 1
fi

cd "$PKG_DIR"
exec npm run storybook
