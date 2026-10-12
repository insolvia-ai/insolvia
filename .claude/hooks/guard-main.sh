#!/usr/bin/env bash
# PreToolUse guard: never let a git commit or push land directly on `main`.
#
# Insolvia's rule (CLAUDE.md § Rules): all changes go through a feature branch
# and a PR — no direct commits or pushes to `main`. This hook makes that rule
# mechanical instead of advisory: it inspects the Bash command Claude is about
# to run and, when the command is a commit/push and the checkout it will run
# in is on `main`, it blocks the call (exit 2) and tells Claude how to proceed.
#
# Hook contract (Claude Code PreToolUse):
#   - stdin  : JSON with { cwd, tool_name, tool_input: { command, ... }, ... }
#   - exit 0 : no objection; normal permission flow continues
#   - exit 2 : BLOCK the tool call; stderr is fed back to Claude as the reason
#   - other  : non-blocking error (tool still runs) — so we only ever exit 0/2
#
# WHICH checkout: the hook's own working directory is not the command's. In a
# worktree session (`.claude/worktrees/<name>`) the hook can be started from
# the primary checkout, which sits on `main` — asking git there blocked every
# commit on a perfectly good feature branch. So the branch is read from the
# `cwd` the payload reports (where the Bash command runs), adjusted by a
# leading `cd <dir> &&` or a `git -C <dir>` in the command itself.
#
# Tests: .claude/hooks/guard-main.test.sh (run it after any edit here).
#
# This runs for every Bash call, so it must stay fast and dependency-light.
set -euo pipefail

input="$(cat)"

# Extract a top-level string field. Prefer jq; fall back to a portable sed if
# jq is absent so a missing dependency can never silently disable the guard.
field() {
  if command -v jq >/dev/null 2>&1; then
    printf '%s' "$input" | jq -r "$1 // empty"
  else
    printf '%s' "$input" | tr -d '\n' \
      | sed -n "s/.*\"$2\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p"
  fi
}
command_str="$(field '.tool_input.command' command)"
cwd="$(field '.cwd' cwd)"

# Nothing to police if there's no command (non-Bash tool, empty input).
[ -n "$command_str" ] || exit 0

# Only care about `git [global options] commit|push` at the start of a shell
# command — not every command that happens to contain the words (a path like
# `.git/hooks/pre-push`, a grep for "commit"). Global options cover -C <dir>,
# -c <k=v> and --flags; a leading `VAR=value` env prefix is allowed too.
starts='(^|[;&|(`]|\$\()[[:space:]]*'
envs='([A-Za-z_][A-Za-z0-9_]*=[^[:space:]]*[[:space:]]+)*'
opts='([[:space:]]+(-[Cc][[:space:]]+[^[:space:]]+|--[^[:space:]]+))*'
if ! printf '%s' "$command_str" | tr '\n' ';' \
  | grep -Eq "${starts}${envs}git${opts}[[:space:]]+(commit|push)([[:space:]]|$|[;&|)])"; then
  exit 0
fi

# Where will git run? Start from the payload's cwd (else our own), then apply
# a leading `cd <dir> &&` and a `git -C <dir>`, each relative to the last.
dir="${cwd:-$PWD}"
resolve() {
  local d="${1%\"}"; d="${d#\"}"; d="${d%\'}"; d="${d#\'}"
  case "$d" in
    "~"|"~/"*) d="$HOME${d#\~}" ;;
  esac
  case "$d" in
    /*) printf '%s' "$d" ;;
    *)  printf '%s/%s' "$dir" "$d" ;;
  esac
}
cd_target="$(printf '%s' "$command_str" \
  | sed -nE 's/^[[:space:]]*cd[[:space:]]+("[^"]+"|'"'"'[^'"'"']+'"'"'|[^[:space:];&]+)[[:space:]]*&&.*/\1/p')"
[ -z "$cd_target" ] || dir="$(resolve "$cd_target")"
c_target="$(printf '%s' "$command_str" \
  | sed -nE 's/.*git[[:space:]]+-C[[:space:]]+("[^"]+"|'"'"'[^'"'"']+'"'"'|[^[:space:]]+).*/\1/p' | head -n1)"
[ -z "$c_target" ] || dir="$(resolve "$c_target")"

# What branch is that checkout on? If git can't tell us (not a repo, missing
# dir, broken git), fail safe by assuming main so the guard errs toward blocking.
branch="$(git -C "$dir" rev-parse --abbrev-ref HEAD 2>/dev/null || echo main)"

if [ "$branch" = "main" ]; then
  cat >&2 <<MSG
Blocked: direct commits/pushes to \`main\` are not allowed (CLAUDE.md § Rules).
(Checked: $dir is on \`main\`, or git could not read its branch.)

Do this instead:
  1. git checkout -b claude/<feature-name>-<id>
  2. commit your work on that branch
  3. push the branch and open a PR (gh pr create)

\`main\` only advances by merging an approved PR. Do not dodge this guard (e.g.
running git through a shell variable) — if it blocks on a feature branch, the
hook is wrong; fix it here.
MSG
  exit 2
fi

exit 0
