#!/usr/bin/env bash
#
# Run the UNIT tier — every suite, or only the areas a set of files touches.
#
#   ./scripts/dev-test-unit.sh                       # every unit suite in the repo
#   ./scripts/dev-test-unit.sh services/api/src/x.py # only the areas those files affect
#   ./scripts/dev-test-unit.sh --list                # which areas, which command, and why
#
# THIS IS WHAT THE PRE-PUSH HOOK RUNS (.pre-commit-config.yaml). Under the
# hook it reads the range being pushed from PRE_COMMIT_FROM_REF and
# PRE_COMMIT_TO_REF — NOT from filenames on the command line, because
# pre-commit splits a long file list into several invocations and each one
# would run the same suites again — and maps the changed files to the suites
# that could have broken. So a push touching one service pays for one
# service's tests, and a docs-only push pays for none. With no arguments and
# no push range it runs everything, which is the "did I break anything
# anywhere" question before opening a PR.
#
# UNIT MEANS UNIT. Nothing here needs AWS, a running server, a password, or
# the network: pytest over memory adapters, Jest and Vitest over mocks.
# The integration tier is a different script with a different name
# (services/api/scripts/dev-test-integration.sh) precisely so that a hook
# can never accidentally reach for it.
#
# THE COMMANDS ARE THE CI COMMANDS. Each area runs the same invocation its
# `*-pr.yml` job runs — a bare `pytest` inside the service, `npm run test
# --workspace <pkg>` at the root — so green here is a fair predictor of a
# green PR and a red here is not a false alarm. Lint, format and typecheck
# are deliberately NOT run here: they are already the pre-COMMIT hooks (and
# `npm run ci` / each service's dev-test.sh for the full gate).
#
# WHY A MISSING VENV FAILS RATHER THAN SKIPS. A hook that silently skipped
# the suite for an area whose toolchain was not installed would be a hook
# that passes on exactly the machine that has not been set up — and the
# push would go out untested while looking tested. So an affected area
# whose venv or node_modules is absent names the setup script and fails.
#
# CONSUMERS RUN WHEN A DEPENDENCY CHANGES. packages/insolvia_core is
# installed by four Python services, so a core change runs all of them; the
# api-client is consumed by the app, so a client change runs the app too.
# The map below is the one place that knowledge lives — keep it in step with
# the `paths:` lists in .github/workflows/*-pr.yml.
#
# Bash 3.2 (macOS) — no associative arrays, no mapfile.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

c_reset=$'\033[0m'; c_blue=$'\033[1;34m'; c_green=$'\033[1;32m'; c_red=$'\033[1;31m'; c_dim=$'\033[2m'
log()  { printf '%s[unit]%s %s\n' "$c_blue" "$c_reset" "$*"; }
ok()   { printf '%s[ ok ]%s %s\n' "$c_green" "$c_reset" "$*"; }
die()  { printf '%s[fail]%s %s\n' "$c_red" "$c_reset" "$*" >&2; exit 1; }

# Every area, in the order they run. Python first because a core change is
# the most common reason for a broad run and its suites are the fastest.
ALL_AREAS="core api admin mailer mcp api-client app portal marketing forms"

# ── Which areas a path can break ────────────────────────────────────────────
# One line per path family; an area listed twice is run once. Kept broad on
# purpose: a false positive costs a fast suite, a false negative costs a red
# PR after the push that was supposed to catch it.
areas_for_path() {
  case "$1" in
    packages/insolvia_core/*)                       echo "core api admin mcp" ;;
    services/api/*)                                 echo "api" ;;
    services/admin/*)                               echo "admin" ;;
    services/mailer/*)                              echo "mailer" ;;
    services/mcp/*)                                 echo "mcp" ;;
    forms/*)                                        echo "forms api" ;;
    packages/insolvia_api_client/*)                 echo "api-client app" ;;
    apps/insolvia_app/*)                            echo "app" ;;
    apps/insolvia_admin/*)                          echo "portal" ;;
    apps/insolvia_marketing/*)                      echo "marketing" ;;
    package.json|package-lock.json|tsconfig.base.json|eslint.base.js)
                                                    echo "api-client app" ;;
    ruff.toml)                                      echo "core api admin mailer mcp" ;;
    # A change to the runner itself is proven by running every suite; a
    # change to the hook config is proven by the hook firing at all.
    scripts/dev-test-unit.sh)                       echo "$ALL_AREAS" ;;
    *)                                              echo "" ;;
  esac
}

# ── How each area runs ──────────────────────────────────────────────────────
# Python: the service's own venv (dev-setup.sh), a bare `pytest` — which
# pyproject's `testpaths` pins to tests/unit. core has no venv of its own and
# runs under the API's, exactly as core-pr.yml installs the API's pins.
python_suite() { # $1 area  $2 directory  $3 venv directory  $4 setup script
  [[ -x "$REPO_ROOT/$3/bin/pytest" ]] ||
    die "$1: no venv at $3 — run ./$4 first (a push touching $2 must be tested, not skipped)."
  ( cd "$REPO_ROOT/$2" && "$REPO_ROOT/$3/bin/pytest" )
}

node_workspace_suite() { # $1 area  $2 workspace
  [[ -d "$REPO_ROOT/node_modules" ]] ||
    die "$1: no root node_modules — run ./apps/insolvia_app/scripts/dev-setup.sh first."
  ( cd "$REPO_ROOT" && npm run test --workspace "$2" )
}

node_standalone_suite() { # $1 area  $2 directory  $3 setup script
  [[ -d "$REPO_ROOT/$2/node_modules" ]] ||
    die "$1: no node_modules in $2 — run ./$3 first."
  ( cd "$REPO_ROOT/$2" && npm test )
}

run_area() {
  case "$1" in
    core)       python_suite core packages/insolvia_core services/api/.venv services/api/scripts/dev-setup.sh ;;
    api)        python_suite api services/api services/api/.venv services/api/scripts/dev-setup.sh ;;
    admin)      python_suite admin services/admin services/admin/.venv services/admin/scripts/dev-setup.sh ;;
    mailer)     python_suite mailer services/mailer services/mailer/.venv services/mailer/scripts/dev-setup.sh ;;
    mcp)        python_suite mcp services/mcp services/mcp/.venv services/mcp/scripts/dev-setup.sh ;;
    api-client) node_workspace_suite api-client @insolvia-ai/api-client ;;
    app)        node_workspace_suite app apps/insolvia_app ;;
    portal)     node_standalone_suite portal apps/insolvia_admin apps/insolvia_admin/scripts/dev-setup.sh ;;
    marketing)  node_standalone_suite marketing apps/insolvia_marketing apps/insolvia_marketing/scripts/dev-setup.sh ;;
    forms)      ( cd "$REPO_ROOT" && ./forms/scripts/dev-test.sh ) ;;
    *)          die "unknown area '$1'" ;;
  esac
}

describe_area() {
  case "$1" in
    core)       echo "packages/insolvia_core   pytest (API venv)" ;;
    api)        echo "services/api             pytest" ;;
    admin)      echo "services/admin           pytest" ;;
    mailer)     echo "services/mailer          pytest" ;;
    mcp)        echo "services/mcp             pytest" ;;
    api-client) echo "packages/insolvia_api_client  npm run test --workspace" ;;
    app)        echo "apps/insolvia_app        npm run test --workspace (jest)" ;;
    portal)     echo "apps/insolvia_admin      npm test (own lockfile)" ;;
    marketing)  echo "apps/insolvia_marketing  npm test (own lockfile)" ;;
    forms)      echo "forms/                   forms/scripts/dev-test.sh" ;;
  esac
}

# ── Pick the areas ──────────────────────────────────────────────────────────
LIST_ONLY=0
FILES=()
for arg in "$@"; do
  case "$arg" in
    --list) LIST_ONLY=1 ;;
    -h|--help)
      sed -n '2,40p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *) FILES+=("$arg") ;;
  esac
done

# Under the pre-push hook the changed files come from the push range.
if [[ ${#FILES[@]} -eq 0 && -n "${PRE_COMMIT_FROM_REF:-}" && -n "${PRE_COMMIT_TO_REF:-}" ]]; then
  while IFS= read -r changed; do
    [[ -n "$changed" ]] && FILES+=("$changed")
  done < <(git -C "$REPO_ROOT" diff --name-only "$PRE_COMMIT_FROM_REF" "$PRE_COMMIT_TO_REF")
  log "push range ${PRE_COMMIT_FROM_REF:0:7}..${PRE_COMMIT_TO_REF:0:7}: ${#FILES[@]} file(s)"
  if [[ ${#FILES[@]} -eq 0 ]]; then
    ok "The push changes no files — nothing to run."
    exit 0
  fi
fi

if [[ ${#FILES[@]} -eq 0 ]]; then
  SELECTED="$ALL_AREAS"
else
  SELECTED=""
  for file in "${FILES[@]}"; do
    # Paths arrive repo-relative from pre-commit; absolute from a human.
    rel="${file#"$REPO_ROOT"/}"
    for area in $(areas_for_path "$rel"); do
      case " $SELECTED " in *" $area "*) ;; *) SELECTED="$SELECTED $area" ;; esac
    done
  done
  # Restore the canonical order so a run is the same shape whatever the input.
  ordered=""
  for area in $ALL_AREAS; do
    case " $SELECTED " in *" $area "*) ordered="$ordered $area" ;; esac
  done
  SELECTED="$ordered"
fi

if [[ -z "${SELECTED// /}" ]]; then
  ok "No unit suite covers the given files — nothing to run."
  exit 0
fi

if [[ "$LIST_ONLY" -eq 1 ]]; then
  for area in $SELECTED; do printf '  %-11s %s\n' "$area" "$(describe_area "$area")"; done
  exit 0
fi

for area in $SELECTED; do
  log "$area ${c_dim}($(describe_area "$area"))${c_reset}"
  run_area "$area"
done
ok "Unit tier green: $SELECTED"
