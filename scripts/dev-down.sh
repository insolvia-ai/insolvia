#!/usr/bin/env bash
#
# Stop everything scripts/dev-up.sh starts, from a fresh terminal.
#
#   ./scripts/dev-down.sh
#
# dev-up.sh already tears the system down when Ctrl-C reaches it — its exit
# trap calls this script. This entry point exists for every time Ctrl-C never
# gets the chance: the terminal window is gone, the process was killed, or the
# containers and ports were claimed by a dev-up.sh run from ANOTHER checkout
# of this repo. All of that state is machine-global, not per-checkout — the
# ports are fixed (the app's 3000 is a registered Cognito origin) and the
# compose project names derive from directory basenames, which every checkout
# shares — so one machine runs one stack, whichever checkout started it, and
# this script reclaims it from any checkout.
#
# It decides nothing per area: each area's own dev-down.sh knows how to stop
# its thing (compose containers outlive their `up`; a stray npx grandchild
# keeps holding 3000), and this file only owns the loop over them. Every one
# of them is idempotent, so running this when nothing is up is safe and quiet.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Bash 3.2 is what macOS ships, so no associative arrays — same as dev-up.sh.
SERVICES="api admin-api mailer app portal marketing"

c_reset=$'\033[0m'
c_red=$'\033[1;31m'
c_yellow=$'\033[1;33m'
c_green=$'\033[1;32m'

warn() { printf '%s[warn]%s %s\n' "$c_yellow" "$c_reset" "$1" >&2; }
ok() { printf '%s[ ok ]%s %s\n' "$c_green" "$c_reset" "$1"; }
die() {
  printf '%s[fail]%s %s\n' "$c_red" "$c_reset" "$1" >&2
  exit 1
}

service_dir() {
  case "$1" in
  api) printf '%s/services/api' "$REPO_ROOT" ;;
  admin-api) printf '%s/services/admin' "$REPO_ROOT" ;;
  mailer) printf '%s/services/mailer' "$REPO_ROOT" ;;
  app) printf '%s/apps/insolvia_app' "$REPO_ROOT" ;;
  portal) printf '%s/apps/insolvia_admin' "$REPO_ROOT" ;;
  marketing) printf '%s/apps/insolvia_marketing' "$REPO_ROOT" ;;
  esac
}

case "${1:-}" in
"") ;;
-h | --help)
  cat <<'USAGE'
Usage: ./scripts/dev-down.sh

Stops everything ./scripts/dev-up.sh starts: api, admin api, mailer, app,
admin portal, marketing — containers included. Safe to run when nothing is up.

To stop one part, run that part's own script instead:
  ./services/api/scripts/dev-down.sh
  ./services/admin/scripts/dev-down.sh
  ./services/mailer/scripts/dev-down.sh
  ./apps/insolvia_app/scripts/dev-down.sh
  ./apps/insolvia_admin/scripts/dev-down.sh
  ./apps/insolvia_marketing/scripts/dev-down.sh
USAGE
  exit 0
  ;;
*) die "unexpected argument '$1' — this script takes none (try --help)" ;;
esac

failures=0
for name in $SERVICES; do
  script="$(service_dir "$name")/scripts/dev-down.sh"
  if [[ ! -x "$script" ]]; then
    warn "$script is missing or not executable."
    failures=$((failures + 1))
    continue
  fi
  # Silenced on success so four areas make four lines; a failure names the
  # per-area script to re-run, which is where the detail lives.
  if "$script" >/dev/null 2>&1; then
    ok "$name is down"
  else
    warn "$name did not shut down cleanly — run $script to see why."
    failures=$((failures + 1))
  fi
done

[[ "$failures" -eq 0 ]] && ok "Everything is down."
exit "$failures"
