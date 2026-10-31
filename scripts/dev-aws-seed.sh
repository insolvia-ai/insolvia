#!/usr/bin/env bash
#
# Make this machine's dev environment signable-in AND seeded: ensure the dev
# account exists in the SHARED development Cognito pool (insolvia-dev-users —
# one pool for every machine, insolvia-services', ADR 0026), then load
# seeds/dev.json into this machine's local database.
#
#   ./scripts/dev-aws-seed.sh
#   ./scripts/dev-aws-seed.sh --check     # report what is missing, write nothing
#
# ## Which database (ADR 0028)
#
# The Postgres 17 of backend/docker-compose.yml (127.0.0.1:55432) — what
# scripts/dev-up.sh's API, admin and MCP hosts read — loaded by the SAME code
# that seeds staging: the migrator's `seed` command
# (backend/src/Insolvia.Migrator), run here with `dotnet run` after `migrate`
# and `purge-pre-client`. The people get their REAL subjects from the shared
# dev pool (AUTH_USER_POOL_ID), so a local host signed in against that pool
# resolves them; the fixture documents come from the committed seeds/fixtures
# tree (no bucket locally — SeedComposition.cs). `--dotnet` is still accepted
# and changes nothing: the DynamoDB target it used to opt out of went with the
# Python backend (plan 18.23).
#
# ## Why this exists
#
# There is no sign-up screen, and there is not supposed to be one. Every
# Insolvia pool sets `allow_admin_create_user_only = true`
# (insolvia-services' infra/modules/auth/main.tf) — a public sign-up form on a bankruptcy-filing
# platform is an invitation to junk accounts, not a growth channel. So the
# hosted UI offers sign-in and nothing else, and `admin-create-user` is the
# only way an account comes into existence.
#
# An account gets you PAST sign-in and no further: the account lives in
# Cognito, firm membership lives in the database, and without a firm every
# signed-in route answers 403 (no active firm user). That is the tenancy model working as
# designed (ADR 0009), and the first firm cannot be made through the API —
# POST /v1/firm/users is itself behind FIRM_ADMINISTRATION, so it needs an
# admin to make an admin. Those used to be two scripts run in an order you had
# to know; the refusal when you didn't cost real time, so now this one script
# does both, in the only order that works.
#
# ## The password, and ~/.config/insolvia/dev.env
#
# The account step needs a password exactly once per pool lifetime. The pool is
# SHARED: an account another machine already created exists here too, with the
# password that machine set — use the same DEV_USER_PASSWORD on every machine
# you work from. It comes from, in order:
#
#   1. DEV_USER_PASSWORD already in the environment,
#   2. DEV_USER_PASSWORD in ~/.config/insolvia/dev.env (created on request by
#      this script, chmod 600 — next to machine-id, which already lives there),
#   3. a no-echo prompt, which then offers to write that file for next time.
#
# The file sits OUTSIDE the repo tree on purpose: this repo is public, an
# in-tree gitignored .env has been committed to a public branch here once
# already, and a file git never walks cannot repeat that. It is still a
# plaintext credential on disk — accepted deliberately, because it guards one
# thing: sign-in to the shared development pool, which holds throwaway test
# accounts and nothing else. Nothing here may reach staging or prod (see Scope), whose
# secrets live in GitHub environments, never in files.
#
# When the account already exists and is CONFIRMED, no password is needed and
# none is read — re-seeding after a fixture edit stays credential-free.
#
# ## What is actually in the seed
#
# seeds/dev.json, which is the answer to "what is on a developer's machine" and
# is reviewable as a diff. This script does not describe the data; it only says
# WHERE to put it. The same loader puts seeds/staging.json into staging from
# the seed step CI runs (.github/actions/seed-staging), so the two environments
# differ in their fixture and their database, never in the code path that
# built the rows. The account step lives HERE in the wrapper, not in the loader, for
# the same reason: staging's accounts come from its fixture (password via
# ${E2E_TEST_USER_PASSWORD}), so a prompt in the loader would be a dev-only
# fork of a CI-shared path.
#
# The fixture also names CASES (seeds/fixtures/<version>/). Locally their
# sample documents are read from the committed tree; staging copies them from
# the shared fixture bucket (scripts/dev-fixture.sh publish).
#
# ## Why this is shell and the loading is .NET
#
# The split follows one question: does the step need to know the shape of our
# data? Not here. This file resolves an ENVIRONMENT — the machine id, the pool
# id in this machine's Terraform state, and AWS credentials an SDK can read
# (dev-aws-common.sh). Building rows does need the shape, so that is the
# migrator, next to the code owning it.
#
# ## Scope
#
# DEV ONLY, and not parameterised on purpose. A script that creates accounts
# and could be pointed at the prod pool by changing one argument is a script
# that eventually is. The pool comes from THIS machine's Terraform state and
# must be the shared dev pool by name; the database is the compose one on
# 127.0.0.1. Staging is seeded by the
# .github/actions/seed-staging step its deploy workflows share, and prod is
# refused outright.
#
# ## AWS credentials
#
# Needs a working session — the developer's own IAM user, the same one
# scripts/dev-aws-setup.sh used. If it is not working, read the
# `insolvia-aws-auth` skill; the mechanics live there and are not restated.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=/dev/null
source "$SCRIPT_DIR/dev-aws-common.sh"

DEV_ENV_FILE="$HOME/.config/insolvia/dev.env"

CHECK_ONLY=0
while [[ $# -gt 0 ]]; do
  case "$1" in
  --check) CHECK_ONLY=1 ;;
  --dotnet) : ;; # the only target now; accepted for old habits
  -h | --help)
    printf 'Usage: %s [--check]\n\n' "$0"
    printf '  DEV_USER_PASSWORD  optional — read from %s,\n' "$DEV_ENV_FILE"
    printf '                     or prompted for without echo, when an account must be created\n\n'
    printf 'Ensures the dev sign-in account(s) named by seeds/dev.json exist in the\n'
    printf 'shared dev pool, then loads that fixture into the local database\n'
    printf '(backend/docker-compose.yml'"'"'s Postgres, through the migrator).\n'
    printf 'Edit the fixture to change WHAT is seeded; this script only says where.\n'
    exit 0
    ;;
  *) die "Unknown option: $1" ;;
  esac
  shift
done

for command in aws jq terraform; do require_command "$command"; done

FIXTURE="$REPO_ROOT/seeds/dev.json"
[[ -f "$FIXTURE" ]] || die "No fixture at $FIXTURE."

require_command dotnet
require_command docker

load_machine_id false
load_aws_identity
terraform_init
outputs="$(terraform_output_json)"

POOL_ID="$(jq -r '.auth_user_pool_id.value // empty' <<<"$outputs")"
[[ -n "$POOL_ID" ]] ||
  die "No auth_user_pool_id in this machine's Terraform state. Run ./scripts/dev-aws-setup.sh first."
[[ "$POOL_ID" =~ ^[a-z0-9-]+_[A-Za-z0-9]+$ ]] ||
  die "'$POOL_ID' does not look like a Cognito user pool id — refusing to continue."
pool_name="$(aws_dev cognito-idp describe-user-pool --user-pool-id "$POOL_ID" \
  --query 'UserPool.Name' --output text)"
[[ "$pool_name" == "$USER_POOL_NAME_EXPECTED" ]] ||
  die "Refusing: pool '$POOL_ID' is named '$pool_name', not '$USER_POOL_NAME_EXPECTED'."

# ── The accounts ────────────────────────────────────────────────
# FROM THE FIXTURE, not from an env var or a default that has to be kept in
# step with it: a fixture person WITHOUT a password is one whose account this
# wrapper must provide (with one, the loader creates it — staging's model).
# That includes a fixture case's portal CLIENTS (ADR 0023), which get the same
# dev password — one secret for every throwaway seat on this machine.
# The addresses are all `.test` ones — a reserved TLD (RFC 2606) that can
# never be a real mailbox, which matters twice over: this repo is public, and
# Cognito would otherwise be able to mail a stranger. Nothing is emailed
# anyway — see SUPPRESS below.
# Not mapfile — macOS ships bash 3.2 and these scripts run on it.
ACCOUNT_EMAILS=()
while IFS= read -r email; do
  ACCOUNT_EMAILS+=("$email")
done < <(jq -r '[(.firms[].users[], (.cases // [])[].clients[]?) | select(has("password") | not) | .email] | reduce .[] as $e ([]; if index([$e]) then . else . + [$e] end) | .[]' "$FIXTURE")
[[ ${#ACCOUNT_EMAILS[@]} -gt 0 ]] ||
  die "No passwordless person in $(basename "$FIXTURE") — nothing for this wrapper to ensure, which is not the dev fixture's shape."
for email in "${ACCOUNT_EMAILS[@]}"; do
  [[ "$email" == *@*.* ]] || die "Fixture email '$email' is not an email address."
done

# Email IS the username on these pools (`username_attributes = ["email"]`).
user_json() {
  aws_dev cognito-idp admin-get-user \
    --user-pool-id "$POOL_ID" --username "$1" --output json 2>/dev/null
}

user_is_confirmed() {
  local existing
  existing="$(user_json "$1")" || return 1
  [[ "$(jq -r '.UserStatus' <<<"$existing")" == "CONFIRMED" ]]
}

# Fills DEV_USER_PASSWORD from the env, the dev.env file, or a prompt — in
# that order, so a value exported for this session always wins over the file.
# One password for every account this wrapper creates: they are all this
# machine's throwaway dev seats. Sets PROMPTED=1 when a human typed it, which
# is what gates the offer to save.
PROMPTED=0
resolve_password() {
  local for_email="$1"
  if [[ -z "${DEV_USER_PASSWORD:-}" && -f "$DEV_ENV_FILE" ]]; then
    # The file is the developer's own shell fragment; sourcing it is the point.
    # shellcheck source=/dev/null
    source "$DEV_ENV_FILE"
    [[ -n "${DEV_USER_PASSWORD:-}" ]] ||
      log "$DEV_ENV_FILE exists but sets no DEV_USER_PASSWORD."
  fi
  if [[ -z "${DEV_USER_PASSWORD:-}" ]]; then
    [[ -t 0 ]] || die "No pool user '$for_email', and no password to create one with.
       Set DEV_USER_PASSWORD, or put DEV_USER_PASSWORD=... in $DEV_ENV_FILE (chmod 600),
       or run this script from a terminal so it can prompt."
    read -rsp "Password for this machine's dev sign-in ($for_email — not echoed; you can save it after): " DEV_USER_PASSWORD
    printf '\n'
    PROMPTED=1
  fi
  [[ -n "${DEV_USER_PASSWORD:-}" ]] || die "No password given."

  # Checked against the pool's own policy here (12+, upper, lower, digit;
  # symbols allowed, not required — infra/modules/auth/main.tf) so a weak one
  # fails in one line rather than as an InvalidPasswordException two calls later.
  {
    [[ ${#DEV_USER_PASSWORD} -ge 12 ]] &&
      [[ "$DEV_USER_PASSWORD" == *[[:lower:]]* ]] &&
      [[ "$DEV_USER_PASSWORD" == *[[:upper:]]* ]] &&
      [[ "$DEV_USER_PASSWORD" == *[[:digit:]]* ]]
  } || die "Password does not meet the pool policy: 12+ characters with at least one lower-case
       letter, one upper-case letter and one digit."
}

offer_to_save_password() {
  [[ "$PROMPTED" -eq 1 && -t 0 ]] || return 0
  local reply
  read -rp "Save it to $DEV_ENV_FILE (chmod 600) so future runs and resets need no prompt? [y/N] " reply
  [[ "$reply" == [Yy]* ]] || return 0
  (
    umask 077
    mkdir -p "$(dirname "$DEV_ENV_FILE")"
    # %q so any password round-trips through `source` intact.
    printf '# Written by scripts/dev-aws-seed.sh — this machine'"'"'s DEV pool password only.\n' >"$DEV_ENV_FILE"
    printf 'DEV_USER_PASSWORD=%q\n' "$DEV_USER_PASSWORD" >>"$DEV_ENV_FILE"
  )
  chmod 600 "$DEV_ENV_FILE"
  ok "Saved. e2e/scripts/dev-test.sh reads the same file, so local E2E needs no export either."
}

ensure_account() {
  local email="$1"
  if user_is_confirmed "$email"; then
    return 0
  fi
  resolve_password "$email"

  # --message-action SUPPRESS is not optional: without it Cognito emails a
  # temporary password to the address. The address is deliberately unroutable,
  # so that mail would only ever bounce — and SES is still in the sandbox.
  if user_json "$email" >/dev/null; then
    log "User '$email' exists but is not CONFIRMED — setting its password."
  else
    log "Creating '$email' in $pool_name"
    aws_dev cognito-idp admin-create-user \
      --user-pool-id "$POOL_ID" \
      --username "$email" \
      --user-attributes "Name=email,Value=$email" "Name=email_verified,Value=true" \
      --message-action SUPPRESS \
      --output json >/dev/null ||
      die "admin-create-user failed. If this is an AccessDenied, see the insolvia-aws-auth skill."
  fi

  # A PERMANENT password, because admin-create-user leaves the account in
  # FORCE_CHANGE_PASSWORD and the hosted UI answers that with a "set a new
  # password" challenge. Through --cli-input-json from a mode-0600 temp file
  # rather than --password, so the value never appears in this process's argv
  # and therefore never in `ps` output nor in a shell history that captured
  # the command line.
  log "Setting a permanent password (clears FORCE_CHANGE_PASSWORD)"
  local payload
  umask 077
  payload="$(mktemp "${TMPDIR:-/tmp}/insolvia-dev-user.XXXXXX")"
  trap 'rm -f "$payload"' EXIT
  jq -n --arg pool "$POOL_ID" --arg user "$email" --arg password "$DEV_USER_PASSWORD" \
    '{UserPoolId: $pool, Username: $user, Password: $password, Permanent: true}' >"$payload"
  aws_dev cognito-idp admin-set-user-password --cli-input-json "file://$payload" ||
    die "admin-set-user-password failed. If it rejected the password it did not meet the policy;
       if it was AccessDenied, see the insolvia-aws-auth skill."
  rm -f "$payload"
  trap - EXIT

  user_is_confirmed "$email" ||
    die "User '$email' is still not CONFIRMED after setting a permanent password — re-run this script."
  ok "Sign-in ready: $email in $pool_name"
}

# ── The data ────────────────────────────────────────────────────
# The compose database's superuser — fixed, local-only values committed in
# backend/docker-compose.yml, reachable from this machine alone. The migrator
# connects as it (it creates roles); the seed then logs in as the `migrator`
# role itself, with INSOLVIA_DB_ROLE_PASSWORD (deployed, with an IAM token).
LOCAL_DB='Host=127.0.0.1;Port=55432;Database=insolvia;Username=insolvia;Password=insolvia-local-only'

migrator() {
  INSOLVIA_ENV=local \
    INSOLVIA_DB_CONNECTION_STRING="$LOCAL_DB" \
    INSOLVIA_DB_ROLE_PASSWORD=insolvia-local-only \
    INSOLVIA_SEED=false \
    AUTH_USER_POOL_ID="$POOL_ID" \
    AWS_REGION="$AWS_REGION_VALUE" \
    dotnet run --project "$REPO_ROOT/backend/src/Insolvia.Migrator" -- "$@"
}

dotnet_seed() {
  log "Starting the local Postgres (backend/docker-compose.yml) if it is not up"
  docker compose -f "$REPO_ROOT/backend/docker-compose.yml" up -d --wait postgres >/dev/null ||
    die "Could not start backend/docker-compose.yml's postgres. Is Docker running?"
  if [[ "$CHECK_ONLY" -eq 1 ]]; then
    migrator purge-pre-client --check ||
      die "The local database still holds pre-client cases (ADR 0022). Re-run without --check."
    migrator seed --check || die "seeds/dev.json is not fully loaded locally. Re-run without --check."
    return 0
  fi
  log "Migrating the local database (idempotent)"
  migrator migrate || die "The migrator could not migrate the local database."
  log "Deleting pre-client cases (ADR 0022) — finding none is the usual answer"
  migrator purge-pre-client --apply
  log "Loading $(basename "$FIXTURE") into the local database (subjects from $pool_name)"
  migrator seed || die "The seed refused or failed — its output above says which person or case."
}

if [[ "$CHECK_ONLY" -eq 1 ]]; then
  for email in "${ACCOUNT_EMAILS[@]}"; do
    user_is_confirmed "$email" ||
      die "No CONFIRMED user '$email' in $pool_name. Re-run without --check to create it."
  done
  dotnet_seed
  ok "Sign-in ready and the local database matches seeds/dev.json."
  exit 0
fi
for email in "${ACCOUNT_EMAILS[@]}"; do
  ensure_account "$email"
done
offer_to_save_password
dotnet_seed
ok "Done. Start ./scripts/dev-up.sh if it is not running, then sign in at http://localhost:3000 as ${ACCOUNT_EMAILS[0]}."
