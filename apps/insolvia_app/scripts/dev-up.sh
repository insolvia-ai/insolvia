#!/usr/bin/env bash
#
# Run the Insolvia app in a browser with fast refresh.
#
# Port 3000 is pinned, not preferred: infra/envs/{dev,staging} register
# http://localhost:3000 as an EXACT-MATCH Cognito allowed origin, and Expo's own
# default is 8081, which that origin list rejects. Change it in both places or
# not at all.
#
# Web is the only target built today. Any `expo start` flag passes through, so
# `--clear` (reset the Metro cache) and `--https` work as usual.
#
# Environment defaults to `local`; override with
#   EXPO_PUBLIC_INSOLVIA_ENV=staging ./apps/insolvia_app/scripts/dev-up.sh
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$APP_DIR/../.." && pwd)"

# The app resolves through the root workspace, and a root node_modules behind
# package-lock.json replays UI bugs whose fix is already in the tree (the
# Select overlay fix shipped in design-system 0.14.1 and "regressed" on a
# machine still running its 0.13.0 install). scripts/dev-up.sh preflights this
# too, but THIS script is how the app runs on its own — so it carries the same
# check. Warn-only, and silent when the install is missing outright: "run npm
# install" is the wrong remedy then (dev-setup.sh is), and Expo's own startup
# failure names that case well enough.
stale=""
[[ -d "$REPO_ROOT/node_modules" ]] && stale="$(node -e '
  const [lockPath, nmDir, ...pkgs] = process.argv.slice(1);
  const locked = require(lockPath).packages || {};
  for (const pkg of pkgs) {
    const want = (locked["node_modules/" + pkg] || {}).version;
    if (!want) continue;
    let have;
    try { have = require(nmDir + "/" + pkg + "/package.json").version; }
    catch { have = "nothing"; }
    if (have !== want) console.log(pkg + ": installed " + have + ", lockfile pins " + want);
  }
' "$REPO_ROOT/package-lock.json" "$REPO_ROOT/node_modules" \
  @insolvia-ai/design-system @insolvia-ai/tokens 2>/dev/null)" || stale=""
if [[ -n "$stale" ]]; then
  while IFS= read -r line; do
    printf '\033[1;33m[warn]\033[0m root node_modules is stale: %s — run npm install at the repo root.\n' "$line" >&2
  done <<<"$stale"
fi

cd "$APP_DIR"
exec npx expo start --web --port 3000 "$@"
