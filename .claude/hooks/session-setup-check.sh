#!/usr/bin/env bash
# SessionStart hook: report dev-toolchain readiness into the agent's context.
#
# Runs the repo's own `scripts/dev-setup.sh --check` (fast, ~0.4s, installs
# nothing) and injects a CONCISE status line — one line when everything is
# present, or the specific missing tools + the fix when not. It must never block
# session start, so it always exits 0. `--check` itself exits 0 whether or not
# tools are missing and signals gaps with `MISSING` lines, so we grep for those
# rather than trusting the exit code.
set -uo pipefail

root="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
setup="$root/scripts/dev-setup.sh"

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
