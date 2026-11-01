#!/usr/bin/env bash
# The contract suite against a local stack — offline, no AWS, no sign-in
# anywhere.
#
#   contract/scripts/dev-test.sh                    # the .NET stack on Postgres
#   contract/scripts/dev-test.sh api                # extra args go to jest
#
# It:
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
# still accepted, and is the only target: the Python stack this suite was
# first proven against went with the Python backend (ADR 0028, plan 18.23).
#
# To aim the suite at some other stack, skip this script: set the CONTRACT_*
# variables contract/src/support/env.ts names and run
# `npm run contract --workspace @insolvia-ai/contract`.
set -euo pipefail

CONTRACT="$(cd "$(dirname "$0")/.." && pwd)"
REPO="$(cd "$CONTRACT/.." && pwd)"
BACKEND="$REPO/backend"

TARGET=dotnet
JEST_ARGS=()
while (($#)); do
  case "$1" in
    --target) TARGET="${2:-}"; shift 2 ;;
    --target=*) TARGET="${1#--target=}"; shift ;;
    *) JEST_ARGS+=("$1"); shift ;;
  esac
done
case "$TARGET" in
  dotnet) ;;
  *) echo "--target must be dotnet (the only stack), got '$TARGET'" >&2; exit 2 ;;
esac

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
export CONTRACT_ISSUER_URL="$ISSUER_URL"
export CONTRACT_SIGNING_KEY="$RUN/signing-key.pem"

# ── the .NET stack ─────────────────────────────────────────────────

# Fixed, run-local credentials for a container that lives as long as this
# script and listens on 127.0.0.1 only. Never a deployed value: deployed
# services authenticate to Aurora with IAM tokens.
DB_PASSWORD=insolvia-contract-only
ROLE_PASSWORD=insolvia-contract-only

start_dotnet() {
  command -v docker >/dev/null || { echo "--target dotnet needs Docker" >&2; exit 1; }
  command -v dotnet >/dev/null || { echo "--target dotnet needs the .NET SDK" >&2; exit 1; }
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
