#!/usr/bin/env bash
# The contract suite, on either of its two targets (ADR 0029 rule 2).
#
#   contract/scripts/dev-test.sh                    # offline: the .NET stack on a throwaway Postgres
#   contract/scripts/dev-test.sh api                # extra args go to jest (a filtered run)
#   contract/scripts/dev-test.sh --target deployed  # this machine's dev stack, signed in for real
#
# ── offline (the default) ──────────────────────────────────────────
#
# No AWS, no sign-in anywhere. It:
# 1. mints a throwaway RS256 key for this run;
# 2. starts the stack in the background and waits for every service to answer:
#    a throwaway Postgres 17 container, migrated and seeded from
#    seeds/dev.json by the real migrator; contract/scripts/aux-server.ts for
#    the JWKS and the blob URLs; then the API, admin and MCP hosts, each as
#    its own database role;
# 3. runs the Jest suite against it (the coverage gate is enforced on an
#    unfiltered run), and stops the stack however that ends.
#
# Needs Docker and the .NET SDK (backend/global.json). `--target dotnet` is
# still accepted as a name for it: the Python stack this suite was first
# proven against went with the Python backend (ADR 0028, plan 18.23).
#
# ── deployed, against THIS MACHINE's dev stack ─────────────────────
#
# The API, admin and MCP hosts that scripts/dev-up.sh (backend/docker-compose.yml)
# serves on :8080, :8090 and :8788, signed in over real Cognito SRP to the
# shared dev pool as the people in seeds/dev.json. What it points at — none of
# it a default in the suite:
#   CONTRACT_API_URL                http://localhost:8080 unless exported
#   CONTRACT_ADMIN_URL              http://127.0.0.1:8090 unless exported
#   CONTRACT_ADMIN_ORIGIN           http://localhost:3100, the portal dev server
#   CONTRACT_MCP_URL                http://127.0.0.1:8788/mcp unless exported
#   CONTRACT_AUTH_POOL_ID           AUTH_USER_POOL_ID from backend/.env
#   CONTRACT_AUTH_CLIENT_ID         AUTH_CLIENT_ID from backend/.env
#   CONTRACT_AUTH_PORTAL_CLIENT_ID  AUTH_PORTAL_CLIENT_ID from backend/.env
#   CONTRACT_AUTH_MCP_CLIENT_ID     exported, else AUTH_MCP_INSPECTOR_CLIENT_ID
#                                   from backend/.env (the MCP Inspector's
#                                   client, on the MCP server's allowlist)
#   E2E_TEST_USER_PASSWORD          exported, else DEV_USER_PASSWORD from
#                                   ~/.config/insolvia/dev.env
# backend/.env is written by scripts/dev-aws-setup.sh; the local database must
# hold seeds/dev.json with the pool's real subjects (scripts/dev-aws-seed.sh),
# or the first signed-in spec 403s and names the missing firm. No AWS
# credentials are needed: sign-in is unsigned Cognito calls, the rest HTTP.
#
# Locally the compose API keeps its blob store and sealing keys as twins, so
# the document and vault specs cross the routes, not a bucket or a KMS key;
# staging, after every API deploy (api-staging.yml), is where they cross the
# deployed role.
#
# NEVER STAGING FROM A LAPTOP: that needs staging's test password in a
# developer's shell for a job CI already does on every deploy, so this
# script aims at dev only. CI sets the CONTRACT_* variables itself and runs
# `npm run contract --workspace @insolvia-ai/contract` with
# CONTRACT_TARGET=deployed. There is no production target anywhere.
#
# To aim the offline suite at some other stack, skip this script: set the
# CONTRACT_* variables contract/src/support/env.ts names and run
# `npm run contract --workspace @insolvia-ai/contract`.
set -euo pipefail

CONTRACT="$(cd "$(dirname "$0")/.." && pwd)"
REPO="$(cd "$CONTRACT/.." && pwd)"
BACKEND="$REPO/backend"

TARGET=offline
JEST_ARGS=()
while (($#)); do
  case "$1" in
    --target) TARGET="${2:-}"; shift 2 ;;
    --target=*) TARGET="${1#--target=}"; shift ;;
    *) JEST_ARGS+=("$1"); shift ;;
  esac
done
case "$TARGET" in
  offline | dotnet) TARGET=offline ;;
  deployed) ;;
  *) echo "--target must be offline or deployed, got '$TARGET'" >&2; exit 2 ;;
esac

if [[ "$TARGET" == deployed ]]; then
  BACKEND_ENV="$BACKEND/.env"
  DEV_ENV_FILE="$HOME/.config/insolvia/dev.env"
  die() { echo "$*" >&2; exit 1; }
  [[ -f "$BACKEND_ENV" ]] || die "No $BACKEND_ENV — run ./scripts/dev-aws-setup.sh first."
  read_env() { sed -n "s/^$1=//p" "$BACKEND_ENV" | tail -1; }
  export CONTRACT_TARGET=deployed CONTRACT_ENVIRONMENT=dev
  export CONTRACT_API_URL="${CONTRACT_API_URL:-http://localhost:8080}"
  export CONTRACT_ADMIN_URL="${CONTRACT_ADMIN_URL:-http://127.0.0.1:8090}"
  export CONTRACT_ADMIN_ORIGIN="${CONTRACT_ADMIN_ORIGIN:-http://localhost:3100}"
  export CONTRACT_MCP_URL="${CONTRACT_MCP_URL:-http://127.0.0.1:8788/mcp}"
  CONTRACT_AUTH_MCP_CLIENT_ID="${CONTRACT_AUTH_MCP_CLIENT_ID:-$(read_env AUTH_MCP_INSPECTOR_CLIENT_ID)}"
  export CONTRACT_AUTH_MCP_CLIENT_ID
  [[ -n "$CONTRACT_AUTH_MCP_CLIENT_ID" ]] ||
    die "AUTH_MCP_INSPECTOR_CLIENT_ID is not in $BACKEND_ENV — re-run ./scripts/dev-aws-setup.sh (or export CONTRACT_AUTH_MCP_CLIENT_ID)."
  CONTRACT_AUTH_POOL_ID="$(read_env AUTH_USER_POOL_ID)"
  CONTRACT_AUTH_CLIENT_ID="$(read_env AUTH_CLIENT_ID)"
  CONTRACT_AUTH_PORTAL_CLIENT_ID="$(read_env AUTH_PORTAL_CLIENT_ID)"
  export CONTRACT_AUTH_POOL_ID CONTRACT_AUTH_CLIENT_ID CONTRACT_AUTH_PORTAL_CLIENT_ID
  [[ -n "$CONTRACT_AUTH_POOL_ID" && -n "$CONTRACT_AUTH_CLIENT_ID" && -n "$CONTRACT_AUTH_PORTAL_CLIENT_ID" ]] ||
    die "AUTH_USER_POOL_ID / AUTH_CLIENT_ID / AUTH_PORTAL_CLIENT_ID are not in $BACKEND_ENV — re-run ./scripts/dev-aws-setup.sh."
  # Credentials: named, never echoed.
  if [[ -z "${E2E_TEST_USER_PASSWORD:-}" && -f "$DEV_ENV_FILE" ]]; then
    # shellcheck source=/dev/null
    source "$DEV_ENV_FILE"
    E2E_TEST_USER_PASSWORD="${DEV_USER_PASSWORD:-}"
  fi
  [[ -n "${E2E_TEST_USER_PASSWORD:-}" ]] ||
    die "E2E_TEST_USER_PASSWORD is not set and $DEV_ENV_FILE provides no DEV_USER_PASSWORD.
There is no default on purpose — this repo is public. Use the password you gave
./scripts/dev-aws-seed.sh for the accounts in seeds/dev.json."
  export E2E_TEST_USER_PASSWORD
  curl -sf -o /dev/null --max-time 5 "$CONTRACT_API_URL/health" ||
    die "Nothing is serving at $CONTRACT_API_URL — start it with ./scripts/dev-up.sh (or ./backend/scripts/dev-up.sh)."
  echo "contract target: deployed — $CONTRACT_API_URL (seeds/dev.json), pool $CONTRACT_AUTH_POOL_ID"
  cd "$REPO"
  exec npm run contract --workspace @insolvia-ai/contract -- "${JEST_ARGS[@]+"${JEST_ARGS[@]}"}"
fi

RUN="$(mktemp -d "${TMPDIR:-/tmp}/insolvia-contract.XXXXXX")"
PIDS=()
CONTAINER=""
cleanup() {
  for pid in "${PIDS[@]+"${PIDS[@]}"}"; do kill "$pid" 2>/dev/null || true; done
  for pid in "${PIDS[@]+"${PIDS[@]}"}"; do wait "$pid" 2>/dev/null || true; done
  if [[ -n "$CONTAINER" ]]; then docker rm -f "$CONTAINER" >/dev/null 2>&1 || true; fi
  rm -rf "$RUN"
}
trap cleanup EXIT

show_logs() {
  for log in "$RUN"/*.log; do
    [[ -f "$log" ]] || continue
    echo "── $(basename "$log") ──" >&2
    cat "$log" >&2
  done
}

node "$CONTRACT/scripts/make-signing-key.ts" "$RUN" >/dev/null

export CONTRACT_API_PORT="${CONTRACT_API_PORT:-18080}"
export CONTRACT_ADMIN_PORT="${CONTRACT_ADMIN_PORT:-18090}"
export CONTRACT_MCP_PORT="${CONTRACT_MCP_PORT:-18070}"
export CONTRACT_AUX_PORT="${CONTRACT_AUX_PORT:-18060}"

AUX="http://127.0.0.1:$CONTRACT_AUX_PORT"
stack_value() { node -p "require('$CONTRACT/stack/stack.json').$1"; }
ISSUER_URL="$AUX/$(stack_value issuerPath)"

export CONTRACT_API_URL="http://127.0.0.1:$CONTRACT_API_PORT"
export CONTRACT_ADMIN_URL="http://127.0.0.1:$CONTRACT_ADMIN_PORT"
export CONTRACT_MCP_URL="http://127.0.0.1:$CONTRACT_MCP_PORT/mcp"
# The origin admin's CORS must admit: the portal's dev server (local admits loopback).
export CONTRACT_ADMIN_ORIGIN="http://localhost:3100"
export CONTRACT_ISSUER_URL="$ISSUER_URL"
export CONTRACT_SIGNING_KEY="$RUN/signing-key.pem"

# ── the .NET stack ─────────────────────────────────────────────────

# Fixed, run-local credentials for a container that lives as long as this
# script and listens on 127.0.0.1 only. Never a deployed value: deployed
# services authenticate to Aurora with IAM tokens.
DB_PASSWORD=insolvia-contract-only
ROLE_PASSWORD=insolvia-contract-only

start_offline() {
  command -v docker >/dev/null || { echo "the offline target needs Docker" >&2; exit 1; }
  command -v dotnet >/dev/null || { echo "the offline target needs the .NET SDK" >&2; exit 1; }
  # Several builds may share a machine; a config watcher per host is noise.
  export DOTNET_hostBuilder__reloadConfigOnChange=false
  export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

  # A throwaway database, on a port Docker picks so nothing collides.
  CONTAINER="insolvia-contract-$$-$RANDOM"
  docker run -d --rm --name "$CONTAINER" \
    -e POSTGRES_DB=insolvia -e POSTGRES_USER=insolvia -e "POSTGRES_PASSWORD=$DB_PASSWORD" \
    -p 127.0.0.1::5432 "${CONTRACT_POSTGRES_IMAGE:-postgres:17}" >/dev/null

  echo "building the .NET hosts"
  for host in Migrator Api Admin Mcp; do
    dotnet build "$BACKEND/src/Insolvia.$host/Insolvia.$host.csproj" --nologo -v quiet \
      >"$RUN/build-$host.log" 2>&1 || { cat "$RUN/build-$host.log" >&2; exit 1; }
  done

  # Over TCP inside the container: during initdb the server listens on the
  # socket only, so a TCP answer means the real server is up.
  local db_port=""
  for _ in $(seq 1 150); do
    if docker exec "$CONTAINER" psql -h 127.0.0.1 -U insolvia -d insolvia -qtAc 'select 1' >/dev/null 2>&1; then
      db_port="$(docker port "$CONTAINER" 5432/tcp | head -n1 | sed 's/.*://')"
      break
    fi
    sleep 0.2
  done
  [[ -n "$db_port" ]] || { echo "Postgres did not start" >&2; docker logs "$CONTAINER" >&2; exit 1; }
  local db="Host=127.0.0.1;Port=$db_port;Database=insolvia"

  # Migrations, roles and grants, then seeds/dev.json and the fixture versions
  # it names — the migrator's own local seed, as `docker compose up` runs it.
  # Subjects are derived (LocalSeedAccounts), which is what stack.json lists.
  mkdir -p "$RUN/blobs"
  INSOLVIA_DB_CONNECTION_STRING="$db;Username=insolvia;Password=$DB_PASSWORD" \
    INSOLVIA_DB_ROLE_PASSWORD="$ROLE_PASSWORD" \
    INSOLVIA_ENV=local INSOLVIA_SEED=true \
    INSOLVIA_SEED_FIXTURE="$REPO/seeds/dev.json" \
    INSOLVIA_SEED_DOCUMENT_DIR="$RUN/blobs" \
    dotnet "$BACKEND/src/Insolvia.Migrator/bin/Debug/net10.0/Insolvia.Migrator.dll" \
    >"$RUN/migrator.log" 2>&1 || {
    echo "the migrator failed:" >&2
    cat "$RUN/migrator.log" >&2
    exit 1
  }

  CONTRACT_JWKS="$RUN/jwks.json" CONTRACT_BLOB_DIR="$RUN/blobs" \
    node "$CONTRACT/scripts/aux-server.ts" >"$RUN/aux.log" 2>&1 &
  PIDS+=($!)

  # Each host as its own role, trusting the run's key through configuration
  # alone: AUTH_ISSUER_URL (whose JWKS the aux server publishes) and, for
  # admin, the local-only GOOGLE_JWKS_URL.
  (
    export INSOLVIA_ENV=local ASPNETCORE_URLS="$CONTRACT_API_URL"
    export INSOLVIA_DB_CONNECTION_STRING="$db;Username=api;Password=$ROLE_PASSWORD"
    export AUTH_ISSUER_URL="$ISSUER_URL"
    AUTH_CLIENT_ID="$(stack_value clientIds.app)"
    AUTH_PORTAL_CLIENT_ID="$(stack_value clientIds.portal)"
    export AUTH_CLIENT_ID AUTH_PORTAL_CLIENT_ID
    export INSOLVIA_LOCAL_BLOB_DIR="$RUN/blobs" INSOLVIA_LOCAL_BLOB_URL="$AUX"
    exec dotnet "$BACKEND/src/Insolvia.Api/bin/Debug/net10.0/Insolvia.Api.dll"
  ) >"$RUN/api.log" 2>&1 &
  PIDS+=($!)
  (
    export INSOLVIA_ENV=local ASPNETCORE_URLS="$CONTRACT_ADMIN_URL"
    export INSOLVIA_DB_CONNECTION_STRING="$db;Username=admin;Password=$ROLE_PASSWORD"
    GOOGLE_CLIENT_ID="$(stack_value clientIds.google)"
    export GOOGLE_CLIENT_ID GOOGLE_JWKS_URL="$AUX/google/certs"
    exec dotnet "$BACKEND/src/Insolvia.Admin/bin/Debug/net10.0/Insolvia.Admin.dll"
  ) >"$RUN/admin.log" 2>&1 &
  PIDS+=($!)
  (
    export INSOLVIA_ENV=local ASPNETCORE_URLS="http://127.0.0.1:$CONTRACT_MCP_PORT"
    export INSOLVIA_DB_CONNECTION_STRING="$db;Username=mcp;Password=$ROLE_PASSWORD"
    export AUTH_ISSUER_URL="$ISSUER_URL" MCP_RESOURCE_URL="$CONTRACT_MCP_URL"
    AUTH_CLIENT_IDS="$(stack_value clientIds.mcp)"
    export AUTH_CLIENT_IDS
    exec dotnet "$BACKEND/src/Insolvia.Mcp/bin/Debug/net10.0/Insolvia.Mcp.dll"
  ) >"$RUN/mcp.log" 2>&1 &
  PIDS+=($!)
}

"start_$TARGET"

ready() {
  curl -fsS -o /dev/null "$AUX/$(stack_value issuerPath)/.well-known/jwks.json" &&
    curl -fsS -o /dev/null "$CONTRACT_API_URL/health" &&
    curl -fsS -o /dev/null "$CONTRACT_ADMIN_URL/health" &&
    curl -fsS -o /dev/null "http://127.0.0.1:$CONTRACT_MCP_PORT/.well-known/oauth-protected-resource/mcp"
}
alive() {
  for pid in "${PIDS[@]}"; do kill -0 "$pid" 2>/dev/null || return 1; done
}
for _ in $(seq 1 150); do
  if ready 2>/dev/null; then break; fi
  if ! alive; then
    echo "the $TARGET contract stack exited before it was ready:" >&2
    show_logs
    exit 1
  fi
  sleep 0.2
done
if ! ready 2>/dev/null; then
  echo "the $TARGET contract stack did not become ready:" >&2
  show_logs
  exit 1
fi
echo "contract stack ($TARGET) ready"

cd "$REPO"
status=0
npm run contract --workspace @insolvia-ai/contract -- "${JEST_ARGS[@]+"${JEST_ARGS[@]}"}" || status=$?
if [[ $status -ne 0 && -n "${CONTRACT_SHOW_STACK_LOG:-}" ]]; then
  show_logs
fi
exit $status
