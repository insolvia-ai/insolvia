#!/usr/bin/env bash
# Per-machine `.env` files for THIS checkout — including git worktrees.
#
# `scripts/dev-aws-setup.sh` writes this machine's dev-AWS wiring into four
# gitignored files (`services/api/.env`, `services/admin/.env`,
# `services/mcp/.env`, `apps/insolvia_app/.env`), and every `dev-up.sh` reads
# one of them. They are per-machine state, so they are ignored — and a git
# worktree checks out TRACKED files only, so it starts with none of them. The
# symptom is not an error: the app's dev server boots with no Cognito config
# and announces "Sign-in is not configured", the API's compose stack refuses
# to start, and the agent in that worktree goes looking for a setup problem
# that the primary checkout solved weeks ago.
#
# This script closes that gap the way `dev-skills.sh` closes the same gap for
# agent skills: in a worktree it SYMLINKS each file from the primary checkout.
# A symlink rather than a copy on purpose — `dev-aws-setup.sh` rewrites these
# files and `dev-aws-destroy.sh` strips keys out of them, always in the primary
# checkout, and a copy would go quietly stale on the next provision. The link
# lands on the same path the `.gitignore` line covers, so it never shows up as
# untracked. In the primary checkout there is nothing to link from; it reports.
#
# WHICH FILES: the anchored `/<path>/.env` lines in the root `.gitignore`, read
# at run time. That file is where a per-path env target is declared (a new one
# needs its own line before its first commit), so it is the one owner of the
# list; a second copy here would drift.
#
# Modes:
#   link     Symlink each missing file from the primary checkout. Offline,
#            milliseconds. Never overwrites a real file; replaces a dangling
#            symlink. The default.
#   check    Report per file, change nothing.
#
# Exits 0 in every mode — this is called from a SessionStart hook and must
# never block a session.
#
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

MODE="link"
case "${1:-}" in
  ""|--link)  MODE="link" ;;
  --check)    MODE="check" ;;
  *) printf 'usage: %s [--link|--check]\n' "$0" >&2; exit 2 ;;
esac

QUIET="${DEV_ENV_FILES_QUIET:-0}"   # set by the SessionStart hook; suppresses per-step chatter
log()  { [[ "$QUIET" == "1" ]] || printf '\033[1;34m[dev-env-files]\033[0m %s\n' "$*"; }
ok()   { [[ "$QUIET" == "1" ]] || printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*"; }

# --- the primary checkout ---------------------------------------------------
#
# Same test as dev-skills.sh: `--git-common-dir` is the shared `.git` every
# worktree points at, so its parent is the checkout that owns it. In the
# primary checkout that resolves to the primary itself.
PRIMARY=""
if command -v git >/dev/null 2>&1; then
  common_dir="$(git -C "$REPO_ROOT" rev-parse --path-format=absolute --git-common-dir 2>/dev/null || true)"
  [[ -n "$common_dir" ]] && PRIMARY="$(cd "$(dirname "$common_dir")" 2>/dev/null && pwd || true)"
fi
IS_WORKTREE=0
[[ -n "$PRIMARY" && "$PRIMARY" != "$REPO_ROOT" ]] && IS_WORKTREE=1

# --- the list ---------------------------------------------------------------
targets() {
  # `/services/api/.env` → `services/api/.env`. Anchored lines only: an
  # unanchored `.env` pattern would be a glob, not a file.
  sed -n 's#^/\([^[:space:]#]*/\.env\)[[:space:]]*$#\1#p' "$REPO_ROOT/.gitignore"
}

state() {
  # present | dangling | missing
  local path="$REPO_ROOT/$1"
  if [[ -e "$path" ]]; then echo present
  elif [[ -L "$path" ]]; then echo dangling
  else echo missing
  fi
}

# --- linking ----------------------------------------------------------------
link_from_primary() {
  local rel linked=0 absent=0
  while IFS= read -r rel; do
    [[ -n "$rel" ]] || continue
    case "$(state "$rel")" in
      present) continue ;;
      dangling) rm -f "$REPO_ROOT/$rel" ;;
    esac
    if [[ -f "$PRIMARY/$rel" ]]; then
      ln -s "$PRIMARY/$rel" "$REPO_ROOT/$rel" && linked=$((linked + 1))
    else
      absent=$((absent + 1))     # the primary has not been provisioned either
    fi
  done < <(targets)
  [[ "$linked" -gt 0 ]] && log "linked $linked env file(s) from $PRIMARY"
  [[ "$absent" -gt 0 ]] && warn "$absent env file(s) exist in neither checkout — run ./scripts/dev-aws-setup.sh in the primary checkout ($PRIMARY)"
  return 0
}

# --- checking ---------------------------------------------------------------
check() {
  local rel missing=()
  while IFS= read -r rel; do
    [[ -n "$rel" ]] || continue
    case "$(state "$rel")" in
      present) ok "$rel" ;;
      *) missing+=("$rel") ;;
    esac
  done < <(targets)
  if [[ "${#missing[@]}" -eq 0 ]]; then
    ok "every per-machine env file is present"
  elif [[ "$IS_WORKTREE" == "1" ]]; then
    warn "MISSING ${#missing[@]} env file(s) in this worktree: ${missing[*]} — run ./scripts/dev-env-files.sh --link"
  else
    warn "MISSING ${#missing[@]} env file(s): ${missing[*]} — run ./scripts/dev-aws-setup.sh"
  fi
  return 0
}

case "$MODE" in
  link)
    if [[ "$IS_WORKTREE" == "1" ]]; then
      link_from_primary
    else
      # The primary is where dev-aws-setup writes; nothing to link from.
      [[ "$QUIET" == "1" ]] || check
    fi
    ;;
  check) check ;;
esac
exit 0
