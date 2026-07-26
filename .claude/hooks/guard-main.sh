#!/usr/bin/env bash
# PreToolUse guard: never let a git commit or push land directly on `main`.
#
# Insolvia's rule (CLAUDE.md § Rules): all changes go through a feature branch
# and a PR — no direct commits or pushes to `main`. This hook makes that rule
# mechanical instead of advisory: it inspects the Bash command Claude is about
# to run and, when the current branch is `main` and the command is a
# commit/push, it blocks the call (exit 2) and tells Claude how to proceed.
#
# Hook contract (Claude Code PreToolUse):
#   - stdin  : JSON with { tool_name, tool_input: { command, ... }, ... }
#   - exit 0 : no objection; normal permission flow continues
#   - exit 2 : BLOCK the tool call; stderr is fed back to Claude as the reason
#   - other  : non-blocking error (tool still runs) — so we only ever exit 0/2
#
# This runs for every Bash call, so it must stay fast and dependency-light.
set -euo pipefail

input="$(cat)"

# Extract the command. Prefer jq; fall back to a portable sed if jq is absent
# so a missing dependency can never silently disable the guard.
if command -v jq >/dev/null 2>&1; then
  command_str="$(printf '%s' "$input" | jq -r '.tool_input.command // empty')"
else
  command_str="$(printf '%s' "$input" \
    | tr -d '\n' \
    | sed -n 's/.*"command"[[:space:]]*:[[:space:]]*"\(.*\)".*/\1/p')"
fi

# Nothing to police if there's no command (non-Bash tool, empty input).
[ -n "$command_str" ] || exit 0

# Only care about git commit / git push.
case "$command_str" in
  *git\ *commit*|*git\ *push*) : ;;
  *) exit 0 ;;
esac

# What branch are we on? If git can't tell us (detached, not a repo), fail
# safe by assuming main so the guard errs toward blocking.
branch="$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo main)"

if [ "$branch" = "main" ]; then
  cat >&2 <<'MSG'
Blocked: direct commits/pushes to `main` are not allowed (CLAUDE.md § Rules).

Do this instead:
  1. git checkout -b claude/<feature-name>-<id>
  2. commit your work on that branch
  3. push the branch and open a PR (gh pr create)

`main` only advances by merging an approved PR.
MSG
  exit 2
fi

exit 0
