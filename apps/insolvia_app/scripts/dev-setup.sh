#!/usr/bin/env bash
#
# App developer bootstrap: shared tools, then an install of the npm workspace at
# the repo root.
#
# The app is an npm workspace member, so one install at the root resolves it
# together with @insolvia-ai/tokens and @insolvia-ai/api-client and symlinks both
# into node_modules. There is nothing app-specific to install here.
#
# Nothing native is built (web only), so no Xcode, CocoaPods or Android SDK is
# needed. `expo prebuild` is what re-introduces them if and when mobile starts.
#
# IDEMPOTENT: the shared base checks every tool before installing, and `npm ci`
# with an up-to-date lockfile is cheap.
#
# Usage:
#   ./apps/insolvia_app/scripts/dev-setup.sh            # full setup
#   ./apps/insolvia_app/scripts/dev-setup.sh --check    # report only
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

CHECK_ONLY=0
[[ "${1:-}" == "--check" ]] && CHECK_ONLY=1

log()  { printf '\033[1;34m[app-setup]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*"; }

log "checking shared developer dependencies..."
if [[ "$CHECK_ONLY" -eq 1 ]]; then
  "$REPO_ROOT/scripts/dev-setup.sh" --check
  if [[ -d "$REPO_ROOT/node_modules/expo" ]]; then
    ok "workspace installed (node_modules/expo present; would still: npm ci)."
  else
    warn "workspace not installed (would: npm ci at the repo root)."
  fi
  exit 0
fi
"$REPO_ROOT/scripts/dev-setup.sh"

log "installing the npm workspace (repo root)..."
(cd "$REPO_ROOT" && npm ci)

ok "app is ready. Run it with: ./apps/insolvia_app/scripts/dev-up.sh"
