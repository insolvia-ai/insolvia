#!/usr/bin/env bash
#
# Grant this environment read access to the @insolvia-ai/design-system package
# on GitHub Packages (npm.pkg.github.com), which apps/insolvia_marketing
# depends on. GitHub Packages requires a token for EVERY npm read — even for
# public packages — carrying the `read:packages` scope (classic PAT) /
# "Packages: read" permission (fine-grained PAT / GitHub App). The default
# GH_TOKEN in CI/sandboxes does NOT have it — `npm ci` fails with a 401/403.
#
# The consumers' committed .npmrc already reads the token from an env var:
#     @insolvia-ai:registry=https://npm.pkg.github.com
#     //npm.pkg.github.com/:_authToken=${NODE_AUTH_TOKEN}
# so this script only has to make a `read:packages`-scoped token available as
# NODE_AUTH_TOKEN. It NEVER writes a token into a committed file — this repo is
# public (see CLAUDE.md).
#
# It obtains such a token in this order:
#   1. A PAT you provide via env (preferred for CI/sandbox): GITHUB_PACKAGES_TOKEN
#      (also honors NODE_AUTH_TOKEN / NPM_TOKEN if already set).
#   2. The GitHub CLI on a developer machine: `gh auth refresh --scopes
#      read:packages` ADDS the scope to your existing `gh` login, then reads the
#      refreshed token via `gh auth token`.
#
# IDEMPOTENT: if a reachable token already reads the package, it changes nothing.
#
# Usage:
#   ./scripts/github-packages-auth.sh            # ensure access, print usage hint
#   eval "$(./scripts/github-packages-auth.sh --export)"   # wire NODE_AUTH_TOKEN
#                                                          #   into the current shell
#   ./scripts/github-packages-auth.sh --check    # verify only, install/refresh nothing
#
set -euo pipefail

MODE="ensure"
case "${1:-}" in
  --export) MODE="export" ;;
  --check)  MODE="check" ;;
  "")       MODE="ensure" ;;
  *) echo "usage: $0 [--export|--check]" >&2; exit 2 ;;
esac

SCOPE="read:packages"
PKG_PATH="@insolvia-ai%2Fdesign-system"
REGISTRY_HOST="npm.pkg.github.com"

# --- logging (all to stderr so --export stdout stays eval-clean) ------------
log()  { printf '\033[1;34m[gh-packages]\033[0m %s\n' "$*" >&2; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*" >&2; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*" >&2; }
err()  { printf '\033[1;31m[fail]\033[0m %s\n' "$*" >&2; }
have() { command -v "$1" >/dev/null 2>&1; }

# Return 0 if $1 can read the package from GitHub Packages (HTTP 200).
token_can_read() {
  local token="$1"
  [[ -n "$token" ]] || return 1
  local code
  code="$(curl -sS -o /dev/null -w '%{http_code}' \
    -H "Authorization: Bearer ${token}" \
    "https://${REGISTRY_HOST}/${PKG_PATH}" 2>/dev/null || echo 000)"
  [[ "$code" == "200" ]]
}

emit_result() {
  # $1 = a verified token
  local token="$1"
  case "$MODE" in
    export) printf 'export NODE_AUTH_TOKEN=%s\n' "$token" ;;   # stdout, eval-able
    *)
      ok "GitHub Packages read access confirmed for @insolvia-ai/design-system."
      log "Wire it into your shell / CI as NODE_AUTH_TOKEN, e.g.:"
      log "    eval \"\$($0 --export)\"     # current shell"
      log "    export NODE_AUTH_TOKEN=<token>   # or set it as a CI/env secret"
      log "Then 'npm ci' in apps/insolvia_marketing will resolve the package."
      ;;
  esac
}

# 1) A token supplied through the environment (CI / sandbox / preset).
for var in GITHUB_PACKAGES_TOKEN NODE_AUTH_TOKEN NPM_TOKEN GH_TOKEN GITHUB_TOKEN; do
  val="${!var:-}"
  [[ -n "$val" ]] || continue
  if token_can_read "$val"; then
    [[ "$MODE" == export ]] || ok "\$$var already has ${SCOPE}."
    emit_result "$val"
    exit 0
  else
    [[ "$MODE" == export ]] || warn "\$$var is set but cannot read the package (missing ${SCOPE}?)."
  fi
done

if [[ "$MODE" == "check" ]]; then
  err "No available token can read @insolvia-ai/design-system (need ${SCOPE})."
  err "Provide a PAT via GITHUB_PACKAGES_TOKEN, or run this script without --check to refresh via gh."
  exit 1
fi

# 2) GitHub CLI: add the scope to an existing developer login, then use its token.
if have gh; then
  log "Attempting to add '${SCOPE}' to your GitHub CLI login via 'gh auth refresh'..."
  if gh auth refresh --hostname github.com --scopes "${SCOPE}" >&2; then
    gh_token="$(gh auth token 2>/dev/null || true)"
    if token_can_read "$gh_token"; then
      ok "gh login now carries ${SCOPE}."
      emit_result "$gh_token"
      exit 0
    fi
    warn "gh refreshed but the token still can't read the package."
  else
    warn "'gh auth refresh' did not complete (needs an interactive gh login on this machine)."
  fi
fi

# 3) Nothing worked — tell the human exactly what to do (the one non-scriptable step).
err "Could not obtain a token with the '${SCOPE}' scope."
cat >&2 <<EOF

To fix this, create a token that can READ GitHub Packages and expose it as
NODE_AUTH_TOKEN (the repo .npmrc already references it):

  • Classic PAT:      https://github.com/settings/tokens  -> scope: read:packages
  • Fine-grained PAT: https://github.com/settings/tokens?type=beta
                        -> Permissions -> Packages: Read-only
  • Developer w/ gh:  gh auth refresh --hostname github.com --scopes read:packages

Then, for CI/sandbox, set it as an environment secret:
      GITHUB_PACKAGES_TOKEN=<token>     (this script picks it up automatically)
  or directly:
      export NODE_AUTH_TOKEN=<token>

Re-run:  $0 --check   to verify.
EOF
exit 1
