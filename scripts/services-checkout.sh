#!/usr/bin/env bash
#
# Sourced, not run. Prints where this machine's checkout of
# insolvia-ai/insolvia-services is, or nothing when there is none:
#
#   . "$REPO_ROOT/scripts/services-checkout.sh"
#   dir="$(services_checkout "$REPO_ROOT")"
#
# WHY THIS EXISTS. The mailer (and shared infra) moved to insolvia-services on
# 2026-10-11. Locally the API still sends through it — `./scripts/dev-up.sh`
# brings it up next to everything else, with Mailpit — so this repo needs to
# find that one at a predictable place without either repo vendoring the other.
#
# WHERE IT LOOKS, first match wins:
#   1. $INSOLVIA_SERVICES_DIR, when set (any layout).
#   2. A sibling of the PRIMARY insolvia checkout: <parent>/insolvia-services.
#      "Primary", because a worktree lives at <primary>/.claude/worktrees/<name>
#      and its own parent is not where the sibling is — so this resolves
#      through git's common dir, which every worktree shares.
#
# Bash 3.2 compatible (macOS), like the scripts that source it.

services_checkout() {
  local repo_root="$1" common primary candidate

  if [[ -n "${INSOLVIA_SERVICES_DIR:-}" ]]; then
    [[ -d "$INSOLVIA_SERVICES_DIR/services/mailer" ]] && printf '%s' "$INSOLVIA_SERVICES_DIR"
    return 0
  fi

  common="$(git -C "$repo_root" rev-parse --path-format=absolute --git-common-dir 2>/dev/null)" || return 0
  primary="$(dirname "$common")"
  candidate="$(dirname "$primary")/insolvia-services"
  [[ -d "$candidate/services/mailer" ]] && printf '%s' "$candidate"
  return 0
}
