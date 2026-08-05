#!/usr/bin/env bash
#
# Bring the whole system up locally, in one terminal.
#
#   ./scripts/dev-up.sh
#
# That is the entire interface. There is no flag to run a subset, because
# there is already a better way to run one part — that part's own dev-up.sh:
#
#   ./services/api/scripts/dev-up.sh
#   ./apps/insolvia_app/scripts/dev-up.sh
#
# This script starts and stops NOTHING itself. Every area owns a dev-up.sh
# that knows how to run its own thing (the API's exports short-lived AWS
# credentials before `compose up`; the app's pins port 3000 because Cognito
# registers that exact origin) and a dev-down.sh that knows how to stop it
# (compose containers outlive the process that started them; a stray npx
# grandchild keeps holding 3000). Both halves of that knowledge belong next to
# the thing they describe. This file is an orchestrator: preflight, launch,
# prefix each stream, and on Ctrl-C run every dev-down.sh.
#
# WHAT LOCAL ACTUALLY MEANS HERE. There is no DynamoDB emulator and no fake
# Cognito: the API talks to this machine's REAL per-developer tables and the
# app signs in against this machine's REAL Cognito pool, both provisioned by
# scripts/dev-aws-setup.sh into infra/envs/dev. That is the point — a KMS or
# IAM mistake surfaces on a laptop rather than after a deploy — but it does
# mean this script cannot do anything useful until that one has run.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Bash 3.2 is what macOS ships, so no associative arrays anywhere below.
SERVICES="api mailer app marketing"
PIDS=""

c_reset=$'\033[0m'
c_dim=$'\033[2m'
c_bold=$'\033[1m'
c_red=$'\033[1;31m'
c_yellow=$'\033[1;33m'
c_green=$'\033[1;32m'
c_blue=$'\033[1;34m'

log() { printf '%s[dev-up]%s %s\n' "$c_blue" "$c_reset" "$1"; }
warn() { printf '%s[warn]%s %s\n' "$c_yellow" "$c_reset" "$1" >&2; }
ok() { printf '%s[ ok ]%s %s\n' "$c_green" "$c_reset" "$1"; }
die() {
  printf '%s[fail]%s %s\n' "$c_red" "$c_reset" "$1" >&2
  exit 1
}

service_dir() {
  case "$1" in
  api) printf '%s/services/api' "$REPO_ROOT" ;;
  mailer) printf '%s/services/mailer' "$REPO_ROOT" ;;
  app) printf '%s/apps/insolvia_app' "$REPO_ROOT" ;;
  marketing) printf '%s/apps/insolvia_marketing' "$REPO_ROOT" ;;
  esac
}

service_port() {
  case "$1" in
  api) printf '8080' ;;
  mailer) printf '8026' ;;
  app) printf '3000' ;;
  marketing) printf '5173' ;;
  esac
}

service_url() {
  case "$1" in
  api) printf 'http://127.0.0.1:8080/health' ;;
  mailer) printf 'http://127.0.0.1:8026  (Mailpit: http://127.0.0.1:8025)' ;;
  app) printf 'http://localhost:3000' ;;
  marketing) printf 'http://localhost:5173' ;;
  esac
}

# One colour per service so four interleaved log streams stay attributable.
service_colour() {
  case "$1" in
  api) printf '\033[36m' ;;
  mailer) printf '\033[35m' ;;
  app) printf '\033[32m' ;;
  marketing) printf '\033[33m' ;;
  *) printf '' ;;
  esac
}

port_open() {
  # No nc/lsof dependency: bash's own /dev/tcp answers "is anything
  # listening?", and it is present wherever this script can run at all. The
  # subshell's exit status is the whole answer.
  (exec 3<>"/dev/tcp/127.0.0.1/$1") >/dev/null 2>&1
}

case "${1:-}" in
"") ;;
-h | --help)
  cat <<'USAGE'
Usage: ./scripts/dev-up.sh

Brings up the whole system: api, mailer, app, marketing.
Ctrl-C stops all of it, containers included.

To run one part, run that part's own script instead:
  ./services/api/scripts/dev-up.sh
  ./services/mailer/scripts/dev-up.sh
  ./apps/insolvia_app/scripts/dev-up.sh
  ./apps/insolvia_marketing/scripts/dev-up.sh

Ports: api 8080 · mailer 8026 (Mailpit 8025) · app 3000 · marketing 5173
USAGE
  exit 0
  ;;
*) die "unexpected argument '$1' — this script takes none (try --help)" ;;
esac

# ── Preflight ───────────────────────────────────────────────────
# Every check here exists because its failure mode is otherwise a wall of
# container or bundler output that never names the missing step.
preflight() {
  local failures=0
  local api_env="$REPO_ROOT/services/api/.env"

  if command -v docker >/dev/null 2>&1; then
    if ! docker info >/dev/null 2>&1; then
      warn "docker is installed but not running — start Docker Desktop."
      failures=$((failures + 1))
    fi
  else
    warn "docker not found — install Docker Desktop (macOS) or docker engine (Linux)."
    failures=$((failures + 1))
  fi

  # The API is the one service with nothing local to fall back to:
  # infra/envs/dev IS the dev database.
  if [[ ! -f "$api_env" ]]; then
    warn "services/api/.env is missing — run ./scripts/dev-aws-setup.sh to provision this machine's dev tables and write it."
    failures=$((failures + 1))
  else
    local missing="" key
    for key in WAITLIST_TABLE_NAME CASE_TABLE_NAME CASE_ACCESS_LOG_TABLE_NAME; do
      grep -q "^$key=" "$api_env" || missing="$missing $key"
    done
    if [[ -n "$missing" ]]; then
      warn "services/api/.env is missing:$missing — re-run ./scripts/dev-aws-setup.sh (it adds keys as new stores land)."
      failures=$((failures + 1))
    fi
    # Not fatal: the API fails closed, so the public routes still work and
    # only sign-in breaks. Worth saying out loud, because "every protected
    # route 401s" reads as a bug rather than a setup gap.
    grep -q '^AUTH_ISSUER_URL=' "$api_env" ||
      warn "services/api/.env has no AUTH_ISSUER_URL — protected routes will answer 401 and the app cannot sign in."
  fi

  [[ -d "$REPO_ROOT/node_modules" ]] || {
    warn "root node_modules missing — run ./apps/insolvia_app/scripts/dev-setup.sh (the app resolves through the root workspace)."
    failures=$((failures + 1))
  }
  # Marketing is deliberately outside the npm workspace and installs from its
  # own lockfile — see the root package.json comments.
  [[ -d "$REPO_ROOT/apps/insolvia_marketing/node_modules" ]] || {
    warn "apps/insolvia_marketing/node_modules missing — run ./apps/insolvia_marketing/scripts/dev-setup.sh."
    failures=$((failures + 1))
  }

  local name dir port half
  for name in $SERVICES; do
    dir="$(service_dir "$name")"
    for half in up down; do
      [[ -x "$dir/scripts/dev-$half.sh" ]] || {
        warn "$dir/scripts/dev-$half.sh is missing or not executable."
        failures=$((failures + 1))
      }
    done
    port="$(service_port "$name")"
    if port_open "$port"; then
      warn "port $port is already in use — $name will fail to bind. Run $dir/scripts/dev-down.sh first."
      failures=$((failures + 1))
    fi
  done

  return "$failures"
}

# ── Teardown ────────────────────────────────────────────────────
# Signal a process and everything below it, deepest first. Needed because the
# recorded pid is not always the thing holding the port: the app's script ends
# in `exec npx expo start`, and npx spawns a separate node process that
# survives its parent.
kill_tree() {
  local pid="$1" child
  for child in $(pgrep -P "$pid" 2>/dev/null); do
    kill_tree "$child"
  done
  kill "$pid" >/dev/null 2>&1 || true
}

cleanup() {
  local status=$?
  trap - INT TERM EXIT

  printf '\n'
  log "Shutting down…"

  local pid
  for pid in $PIDS; do
    kill_tree "$pid"
  done
  for pid in $PIDS; do
    wait "$pid" >/dev/null 2>&1 || true
  done

  # Then each area's own dev-down.sh, because stopping the process that
  # started a thing is not the same as stopping the thing. Every one of them
  # is idempotent, so running all four unconditionally is correct even when
  # some never started.
  local name dir
  for name in $SERVICES; do
    dir="$(service_dir "$name")"
    [[ -x "$dir/scripts/dev-down.sh" ]] || continue
    "$dir/scripts/dev-down.sh" >/dev/null 2>&1 || warn "$name did not shut down cleanly — check $dir."
  done

  ok "Everything is down."
  exit "$status"
}

# ── Launch ──────────────────────────────────────────────────────
prefix_stream() {
  local tag="$1" line
  while IFS= read -r line; do
    printf '%s %s\n' "$tag" "$line"
  done
}

start_service() {
  local name="$1" tag colour dir
  dir="$(service_dir "$name")"
  colour="$(service_colour "$name")"
  tag="$(printf '%s%-9s |%s' "$colour" "$name" "$c_reset")"

  # Process substitution rather than a `( … | while read )` pipeline, and the
  # difference is not style. In a pipeline `$!` is the SUBSHELL, so killing it
  # can orphan the real dev server; here `$!` is the child script itself.
  #
  # stdout and stderr are merged deliberately: a bundler writing errors to
  # stderr should not have them appear unlabelled among four streams.
  "$dir/scripts/dev-up.sh" > >(prefix_stream "$tag") 2>&1 &
  PIDS="$PIDS $!"
}

wait_for_ports() {
  # Generous, because the first run of the day is not the steady state: the
  # two compose stacks build images and Vite does its cold dependency scan,
  # all concurrently. 180s was not enough for marketing on a cold start.
  local deadline=$((SECONDS + 420))
  local pending="$SERVICES"
  local name still

  while [[ -n "$pending" && $SECONDS -lt $deadline ]]; do
    still=""
    for name in $pending; do
      if port_open "$(service_port "$name")"; then
        ok "$name is listening on $(service_port "$name")"
      else
        still="$still $name"
      fi
    done
    pending="${still# }"
    [[ -n "$pending" ]] && sleep 2
  done

  [[ -n "$pending" ]] &&
    warn "still waiting on:$pending — everything else is up, and it may yet appear. Its own lines above will say why if not."
  return 0
}

banner() {
  local name
  printf '\n%s  Insolvia is up%s\n\n' "$c_bold" "$c_reset"
  for name in $SERVICES; do
    printf '  %-10s %s\n' "$name" "$(service_url "$name")"
  done
  # The sign-in pointer names the account script on purpose. Every pool sets
  # allow_admin_create_user_only, so the hosted UI has NO sign-up link — which
  # reads as a broken page rather than a deliberate policy, and the first
  # person to hit it lost real time to exactly that.
  printf '\n%s  Sign in at http://localhost:3000 — this machine'"'"'s own Cognito pool.%s\n' "$c_dim" "$c_reset"
  printf '%s  No account yet? There is no sign-up screen by design:%s\n' "$c_dim" "$c_reset"
  printf '%s      ./scripts/dev-aws-create-user.sh%s\n' "$c_dim" "$c_reset"
  printf '%s  Ctrl-C once stops everything, containers included.%s\n\n' "$c_dim" "$c_reset"
}

main() {
  if ! preflight; then
    die "preflight failed — fix the warnings above and re-run. Nothing was started."
  fi
  ok "Prerequisites look good."

  trap cleanup INT TERM EXIT

  local name
  for name in $SERVICES; do
    # ${name} braced, not bare. The next character is a multibyte ellipsis,
    # and bash can absorb those bytes into the IDENTIFIER — it then looks up a
    # variable that does not exist and `set -u` kills the script with
    # "name<mojibake>: unbound variable". Brace any expansion that touches a
    # non-ASCII character.
    log "Starting ${name}…"
    start_service "$name"
  done

  wait_for_ports
  banner

  # Block until ANY child dies, or the user interrupts.
  #
  # `wait -n` would be the obvious tool and is bash 4.3+; macOS ships 3.2, so
  # this polls. Plain `wait` is wrong for a subtler reason: it returns only
  # when EVERY child has exited, so one crashed service would sit unnoticed
  # behind three healthy ones, its port free and its logs stopped.
  local pid
  while :; do
    for pid in $PIDS; do
      if ! kill -0 "$pid" 2>/dev/null; then
        warn "a service exited — bringing the rest down."
        return 0
      fi
    done
    # `sleep 2 & wait $!` rather than `sleep 2`, and this is the difference
    # between Ctrl-C working and Ctrl-C doing nothing: bash defers a trap
    # until the current FOREGROUND command finishes, and `wait` is
    # interruptible where `sleep` is not.
    sleep 2 &
    wait $! || true
  done
}

main "$@"
