#!/usr/bin/env bash
#
# Marketing-site developer bootstrap: shared tools, GitHub Packages auth, npm ci.
#
# The site depends on @insolvia-ai/design-system from GitHub Packages, which
# requires a read:packages token even though the package is public — so auth
# runs before the install (see scripts/github-packages-auth.sh for the token
# resolution order). Everything cross-cutting (Node >= 24, Terraform, ...)
# comes from the shared base.
#
# IDEMPOTENT: the shared base checks every tool before install, the auth step
# changes nothing when a working token exists, and `npm ci` is safe to re-run.
#
# Usage:
#   ./apps/insolvia_marketing/scripts/dev-setup.sh            # full setup
#   ./apps/insolvia_marketing/scripts/dev-setup.sh --check    # report only
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

CHECK_ONLY=0
[[ "${1:-}" == "--check" ]] && CHECK_ONLY=1

log()  { printf '\033[1;34m[marketing-setup]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*"; }

log "checking shared developer dependencies..."
if [[ "$CHECK_ONLY" -eq 1 ]]; then
  "$REPO_ROOT/scripts/dev-setup.sh" --check
else
  "$REPO_ROOT/scripts/dev-setup.sh"
fi

if [[ "$CHECK_ONLY" -eq 1 ]]; then
  # Verify-only: does any available token read the package?
  if "$REPO_ROOT/scripts/github-packages-auth.sh" --check; then
    ok "GitHub Packages auth OK."
  else
    warn "GitHub Packages auth is not set up — 'npm ci' would fail."
  fi
  if [[ -d "$APP_DIR/node_modules" ]]; then
    ok "node_modules present (would still: npm ci)."
  else
    warn "node_modules missing (would: npm ci)."
  fi
  exit 0
fi

log "ensuring GitHub Packages read access (@insolvia-ai/design-system)..."
eval "$("$REPO_ROOT/scripts/github-packages-auth.sh" --export)"

log "installing npm dependencies..."
(cd "$APP_DIR" && npm ci)

ok "marketing site is ready. Start it with: ./apps/insolvia_marketing/scripts/dev-up.sh"
