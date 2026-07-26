#!/usr/bin/env bash
#
# Flutter design-system developer bootstrap: shared tools, then a STANDALONE
# `fvm flutter pub get` inside the package.
#
# This package is deliberately NOT a pub workspace member — pub silently
# rewrites a workspace member's dependency back to the local path, which would
# defeat the app's tag-pinned git dependency on it (see the root pubspec.yaml
# and docs/PACKAGE_PUBLISHING.md). So the root workspace `pub get` does not
# cover it; this script's in-package resolve is the local equivalent of what
# design-system-pr.yml does in CI.
#
# Usage:
#   ./packages/insolvia_design_system/scripts/dev-setup.sh            # full setup
#   ./packages/insolvia_design_system/scripts/dev-setup.sh --check    # report only
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PKG_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

CHECK_ONLY=0
[[ "${1:-}" == "--check" ]] && CHECK_ONLY=1

log()  { printf '\033[1;34m[design-system-setup]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*"; }

log "checking shared developer dependencies..."
if [[ "$CHECK_ONLY" -eq 1 ]]; then
  "$REPO_ROOT/scripts/dev-setup.sh" --check
  if [[ -f "$PKG_DIR/pubspec.lock" ]]; then
    ok "package resolved (pubspec.lock present; would still: fvm flutter pub get)."
  else
    warn "package not resolved (would: fvm flutter pub get in the package)."
  fi
  exit 0
fi
"$REPO_ROOT/scripts/dev-setup.sh"

log "resolving the package standalone (outside the workspace)..."
(cd "$PKG_DIR" && fvm flutter pub get)

ok "design system is ready. Test it with: (cd packages/insolvia_design_system && fvm flutter test)"
