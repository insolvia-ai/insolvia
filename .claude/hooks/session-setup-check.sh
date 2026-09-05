#!/usr/bin/env bash
# SessionStart hook: make this checkout's agent skills present, then report
# dev-toolchain readiness into the agent's context.
#
# TWO STEPS, and the first one is a repair rather than a report.
#
# 1. `dev-skills.sh --link`. A git worktree checks out TRACKED files, and both
#    halves of a skill install are gitignored — `.agents/skills/` and the
#    `.claude/skills/<name>` symlinks that point into it. So an agent in a fresh
#    worktree silently loses every third-party and design-system skill, keeping
#    only the tracked `insolvia-*` ones. Nothing errors; the skills are simply
#    absent, which is the worst way for `design-system-catalogue` to go missing.
#    `--link` symlinks them from the primary checkout: offline, milliseconds, no
#    network, and it never rewrites `skills-lock.json` (the installer does, and
#    a worktree is exactly where that stray diff gets committed by accident).
#    In the primary checkout it has nothing to link from and only reports.
#
# 2. `dev-setup.sh --check` (fast, ~0.4s, installs nothing) for the toolchain,
#    injecting one line when everything is present or the specific missing tools
#    plus the fix when not. `--check` exits 0 either way and signals gaps with
#    `MISSING` lines, so we grep for those rather than trusting the exit code.
#
# It must never block session start, so it always exits 0.
set -uo pipefail

root="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
setup="$root/scripts/dev-setup.sh"
skills="$root/scripts/dev-skills.sh"

# Step 1 — repair. Quiet: only the one-line outcome below should reach context.
if [ -x "$skills" ]; then
  skills_out="$(DEV_SKILLS_QUIET=1 "$skills" --link 2>&1 | sed -E 's/\x1b\[[0-9;]*m//g')"
  case "$skills_out" in
    *"[warn]"*) printf 'Agent skills: %s\n' "$(printf '%s' "$skills_out" | head -2 | tr '\n' ' ')" ;;
  esac
fi

if [ ! -x "$setup" ]; then
  echo "Dev toolchain: setup script not found at $setup."
  exit 0
fi

# Strip ANSI color codes so the injected context is clean text.
out="$("$setup" --check 2>&1 | sed -E 's/\x1b\[[0-9;]*m//g')"
missing="$(printf '%s\n' "$out" | grep -i 'MISSING' || true)"

if [ -n "$missing" ]; then
  printf 'Dev toolchain: NOT fully set up —\n%s\nRun ./scripts/dev-setup.sh (plus the per-package scripts/dev-setup.sh) before building.\n' "$missing"
else
  echo "Dev toolchain: shared tools present. Per-package setup may still be needed (each area's scripts/dev-setup.sh; see scripts/README.md)."
fi
exit 0
