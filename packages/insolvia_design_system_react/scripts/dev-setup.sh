#!/usr/bin/env bash
#
# React design-system developer bootstrap: shared tools (Node >= 24), then
# `npm ci` in the package.
#
# The package's own dependencies all come from the public registry — nothing
# @insolvia-ai-scoped — so no GitHub Packages token is needed to develop it
# (the committed .npmrc's ${NODE_AUTH_TOKEN} reference only matters when npm
# actually talks to npm.pkg.github.com, i.e. on publish and for consumers).
#
# IDEMPOTENT: the shared base checks every tool before install, and `npm ci`
# is safe to re-run.
#
# Usage:
#   ./packages/insolvia_design_system_react/scripts/dev-setup.sh            # full setup
#   ./packages/insolvia_design_system_react/scripts/dev-setup.sh --check    # report only
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PKG_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

CHECK_ONLY=0
[[ "${1:-}" == "--check" ]] && CHECK_ONLY=1

log()  { printf '\033[1;34m[react-ds-setup]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*"; }

log "checking shared developer dependencies..."
if [[ "$CHECK_ONLY" -eq 1 ]]; then
  "$REPO_ROOT/scripts/dev-setup.sh" --check
  if [[ -d "$PKG_DIR/node_modules" ]]; then
    ok "node_modules present (would still: npm ci)."
  else
    warn "node_modules missing (would: npm ci)."
  fi
  exit 0
fi
"$REPO_ROOT/scripts/dev-setup.sh"

log "installing npm dependencies..."
(cd "$PKG_DIR" && npm ci)

ok "React design system is ready. Storybook: ./packages/insolvia_design_system_react/scripts/dev-up.sh"
