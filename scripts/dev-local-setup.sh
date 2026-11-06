#!/usr/bin/env bash
#
# Wire this machine's backend and app at backend/local-auth, the local stand-in
# for Cognito (ADR 0030). Needs Docker, and no AWS.
#
#   ./scripts/dev-local-setup.sh
#
# Writes backend/.env and apps/insolvia_app/.env, then ./scripts/dev-up.sh runs
# everything. Safe to run again.
#
# AN EXISTING DATABASE KEEPS ITS PEOPLE. A database seeded through a real pool
# holds that pool's subjects, and every case points at them. Rather than wipe
# it, this reads each address and subject out of the local database into
# LOCAL_AUTH_SUBJECTS, so local-auth signs those people in as who they already
# are. Seeding is then left off, because a seed would add the same people again
# under derived subjects. A fresh machine has none of this: compose seeds it.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=dev-aws-common.sh
source "$SCRIPT_DIR/dev-aws-common.sh"

COMPOSE_FILE="$REPO_ROOT/backend/docker-compose.yml"
AUTH_ORIGIN="http://localhost:8071"
VOLUME="insolvia-backend-pgdata"

require_command docker
require_command jq
require_command node

subjects='{}'
if docker volume inspect "$VOLUME" >/dev/null 2>&1; then
  log "Reading who exists from the local database"
  docker compose -f "$COMPOSE_FILE" up -d --wait postgres >/dev/null ||
    die "Could not start the local Postgres. Is Docker running?"
  query="SELECT coalesce(json_object_agg(lower(email), subject), '{}') FROM (
           SELECT email, subject FROM insolvia.firm_users
           UNION SELECT email, subject FROM insolvia.client_bindings) people"
  # A database older than these tables has nobody to keep.
  subjects="$(docker compose -f "$COMPOSE_FILE" exec -T postgres \
    psql -U insolvia -d insolvia -Atc "$query" 2>/dev/null || printf '{}')"
fi

# Only people whose subject local-auth would not derive need keeping.
kept="$(SUBJECTS="$subjects" node --input-type=module -e "
  const { subjectFor } = await import('$REPO_ROOT/backend/local-auth/server.ts');
  const people = Object.entries(JSON.parse(process.env.SUBJECTS));
  console.log(JSON.stringify(Object.fromEntries(people.filter(([email, subject]) => subjectFor(email) !== subject))));
")"
kept_count="$(jq 'length' <<<"$kept")"

backend_env="$BACKEND_ENV"
upsert_env "$backend_env" INSOLVIA_ENV "local"
upsert_env "$backend_env" AUTH_ISSUER_URL "$AUTH_ORIGIN/local-pool"
upsert_env "$backend_env" AUTH_JWKS_URL "http://local-auth:8071/local-pool/.well-known/jwks.json"
upsert_env "$backend_env" AUTH_CLIENT_ID "local-web"
upsert_env "$backend_env" AUTH_PORTAL_CLIENT_ID "local-portal"
upsert_env "$backend_env" AUTH_CLIENT_IDS "local-mcp-inspector"
upsert_env "$backend_env" AUTH_MCP_INSPECTOR_CLIENT_ID "local-mcp-inspector"
upsert_env "$backend_env" INSOLVIA_LOCAL_SUBJECTS "derived"
remove_env "$backend_env" AUTH_USER_POOL_ID

if [[ "$kept_count" -gt 0 ]]; then
  upsert_env "$backend_env" LOCAL_AUTH_SUBJECTS "'$kept'"
  upsert_env "$backend_env" INSOLVIA_SEED "false"
  ok "Kept $kept_count existing $([[ "$kept_count" -eq 1 ]] && echo person || echo people) from the local database."
else
  remove_env "$backend_env" LOCAL_AUTH_SUBJECTS
  upsert_env "$backend_env" INSOLVIA_SEED "true"
fi

app_env="$APP_DIR/.env"
upsert_env "$app_env" EXPO_PUBLIC_INSOLVIA_ENV "local"
upsert_env "$app_env" EXPO_PUBLIC_COGNITO_DOMAIN "$AUTH_ORIGIN"
upsert_env "$app_env" EXPO_PUBLIC_COGNITO_CLIENT_ID "local-web"
upsert_env "$app_env" EXPO_PUBLIC_COGNITO_PORTAL_CLIENT_ID "local-portal"

ok "Local sign-in is wired. Run ./scripts/dev-up.sh, then sign in at http://localhost:3000."
