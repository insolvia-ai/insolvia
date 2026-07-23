#!/usr/bin/env bash
#
# Flutter app developer bootstrap: shared tools (FVM + pinned Flutter, Melos),
# then a workspace-wide `fvm flutter pub get` at the repo root.
#
# The app is a pub workspace member, so one `pub get` at the root resolves it
# together with insolvia_tokens and insolvia_api_client. The design system is
# deliberately NOT a workspace member (the app pins it as a git tag — see
# docs/PACKAGE_PUBLISHING.md); it resolves standalone via
# packages/insolvia_design_system/scripts/dev-setup.sh.
#
# IDEMPOTENT: the shared base checks every tool before install, and `pub get`
# with an up-to-date lockfile is a no-op.
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
  if [[ -f "$REPO_ROOT/pubspec.lock" ]]; then
    ok "workspace resolved (pubspec.lock present; would still: fvm flutter pub get)."
  else
    warn "workspace not resolved (would: fvm flutter pub get at the repo root)."
  fi
  exit 0
fi
"$REPO_ROOT/scripts/dev-setup.sh"

log "resolving the pub workspace (repo root)..."
(cd "$REPO_ROOT" && fvm flutter pub get)

ok "app is ready. Run it with: ./apps/insolvia_app/scripts/dev-up.sh"
