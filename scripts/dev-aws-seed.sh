#!/usr/bin/env bash
#
# Make this machine signable-in AND seeded: ensure the dev account exists in
# the SHARED development Cognito pool (insolvia-dev-users — one pool for every
# machine, insolvia-services', ADR 0026), then load seeds/dev.json into this
# machine's local stack — the Postgres and the S3 server services/api's
# compose stack runs (#624, ADR 0030 row 5).
#
#   ./scripts/dev-aws-seed.sh
#   ./scripts/dev-aws-seed.sh --check     # report what is missing, write nothing
#   ./scripts/dev-aws-seed.sh --refresh   # redo the Cognito half even if cached
#
# ## Two halves, and only the first needs AWS
#
# THE COGNITO HALF NEEDS AN AWS LOGIN, ONCE PER USER. It reads the pool's
# public ids from SSM (/insolvia/dev/auth/*), writes them into
# services/api/.env and apps/insolvia_app/.env, creates the fixture's people
# in the pool if they are missing, and records each person's Cognito `sub` in
# ~/.config/insolvia/dev-subjects.json. Log in first (the insolvia-aws-auth
# skill has the mechanics); after that run, nothing here touches AWS again
# unless the fixture names someone new or you pass --refresh.
#
# THE DATA HALF NEEDS NO AWS SESSION. It runs the seeder with --local: the
# firm and cases into the local Postgres, the sample documents from the
# committed fixture (seeds/fixtures/<version>/) into the local S3 server, the
# people resolved from the subjects file. It needs ./scripts/dev-up.sh (or
# ./services/api/scripts/dev-up.sh) running.
#
# ## Why this exists
#
# There is no sign-up screen, and there is not supposed to be one. Every
# Insolvia pool sets `allow_admin_create_user_only = true`
# (insolvia-services' infra/modules/auth/main.tf), so `admin-create-user` is
# the only way an account comes into existence. An account gets you PAST
# sign-in and no further: firm membership lives in Postgres, and without a
# firm every route behind current_accessor() answers 403
# (`no_active_firm_user`) — the tenancy model working as designed (ADR
# 0009). The first firm cannot be made through the API (POST /v1/firm/users is
# itself behind FIRM_ADMINISTRATION), so this one script does both, in the
# only order that works.
#
# ## The password, and ~/.config/insolvia/dev.env
#
# The account step needs a password exactly once per pool lifetime. The pool is
# SHARED: an account another machine already created exists here too, with the
# password that machine set — use the same DEV_USER_PASSWORD on every machine.
# It comes from, in order: DEV_USER_PASSWORD in the environment, then
# ~/.config/insolvia/dev.env, then a no-echo prompt that offers to write that
# file (chmod 600, outside the repo — this repo is public). When the account
# already exists and is CONFIRMED, no password is read.
#
# ## What is actually in the seed
#
# seeds/dev.json — this script only says WHERE it goes. The same loader puts
# seeds/staging.json into staging from .github/actions/seed-staging (against
# real AWS, with no --local), so the environments differ in fixture and
# target, never in the code path that builds the rows. The loader lives in
# services/admin/src/insolvia_admin/entrypoints/seed.py.
#
# shortcut: debtors' tax ids are not seeded locally until #623 moves them
# to Postgres — the local API keeps them in memory.
#
# ## Scope
#
# DEV ONLY, and not parameterised on purpose: the pool must be named
# insolvia-dev-users, and the seeded names carry this machine's short id,
# which the loader re-checks. Staging is seeded by CI, and prod is refused.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=/dev/null
source "$SCRIPT_DIR/dev-aws-common.sh"

DEV_ENV_FILE="$HOME/.config/insolvia/dev.env"
SUBJECTS_FILE="$CONFIG_DIR/dev-subjects.json"

CHECK_ONLY=0
REFRESH=0
while [[ $# -gt 0 ]]; do
  case "$1" in
  --check) CHECK_ONLY=1 ;;
  --refresh) REFRESH=1 ;;
  -h | --help)
    printf 'Usage: %s [--check] [--refresh]\n\n' "$0"
    printf '  DEV_USER_PASSWORD  optional — read from %s,\n' "$DEV_ENV_FILE"
    printf '                     or prompted for without echo, when an account must be created\n\n'
    printf 'Ensures the dev sign-in account(s) named by seeds/dev.json exist in the\n'
    printf 'shared dev pool (needs an AWS login, once), then loads that fixture into\n'
    printf 'the local Postgres and S3 server (no AWS).\n'
    exit 0
    ;;
  *) die "Unknown option: $1" ;;
  esac
  shift
done

require_command jq

FIXTURE="$REPO_ROOT/seeds/dev.json"
[[ -f "$FIXTURE" ]] || die "No fixture at $FIXTURE."

# The loader runs through the API's own code, so it needs the API's venv.
VENV_PYTHON="$API_DIR/.venv/bin/python"
[[ -x "$VENV_PYTHON" ]] ||
  die "No API venv at $VENV_PYTHON. Run ./services/api/scripts/dev-setup.sh first."

# The seeded names carry this machine's short id: the loader guards its
# target with them and derives the seeded ids from them. Not AWS resources.
load_machine_id true
FIRM_TABLE="insolvia-dev-$MACHINE_SHORT_ID-firms"
CASE_TABLE="insolvia-dev-$MACHINE_SHORT_ID-cases"
DOCUMENT_BUCKET="insolvia-local-case-documents"
API_ENV="$API_DIR/.env"
APP_ENV="$APP_DIR/.env"

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

# ── The Cognito half (AWS login, once) ──────────────────────────

has_subject() {
  [[ -f "$SUBJECTS_FILE" ]] && jq -e --arg e "$1" '.[$e] | strings' "$SUBJECTS_FILE" >/dev/null
}

cognito_half_done() {
  local email
  grep -q '^AUTH_ISSUER_URL=.' "$API_ENV" 2>/dev/null || return 1
  grep -q '^EXPO_PUBLIC_COGNITO_CLIENT_ID=.' "$APP_ENV" 2>/dev/null || return 1
  for email in "${ACCOUNT_EMAILS[@]}"; do has_subject "$email" || return 1; done
}

contract() {
  jq -r --arg name "$SHARED_CONTRACT_PATH/$1" '.[] | select(.Name == $name) | .Value' <<<"$PARAMETERS"
}

cognito_half() {
  require_command aws
  log "The Cognito half needs an AWS login (once per user) — profile '$AWS_PROFILE_VALUE'."
  aws_dev sts get-caller-identity >/dev/null 2>&1 ||
    die "No AWS session for profile '$AWS_PROFILE_VALUE'. Log in (see the insolvia-aws-auth skill), then re-run.
       Only this half needs it; once done, seeding and running the stack need no AWS."
  PARAMETERS="$(aws_dev ssm get-parameters-by-path --path "$SHARED_CONTRACT_PATH" \
    --query Parameters --output json)" || die "Could not read $SHARED_CONTRACT_PATH/* from SSM."
  POOL_ID="$(contract user-pool-id)"
  [[ "$POOL_ID" =~ ^[a-z0-9-]+_[A-Za-z0-9]+$ ]] ||
    die "No usable $SHARED_CONTRACT_PATH/user-pool-id. The shared dev pool is insolvia-services' infra/envs/dev."
  pool_name="$(aws_dev cognito-idp describe-user-pool --user-pool-id "$POOL_ID" \
    --query 'UserPool.Name' --output text)"
  [[ "$pool_name" == "$SHARED_POOL_NAME" ]] ||
    die "Refusing: pool '$POOL_ID' is named '$pool_name', not '$SHARED_POOL_NAME'."

  # Public identifiers (they ride in every sign-in redirect), into the two
  # gitignored env files that need them.
  upsert_env "$API_ENV" AUTH_ISSUER_URL "$(contract issuer-url)"
  upsert_env "$API_ENV" AUTH_CLIENT_ID "$(contract client-id)"
  upsert_env "$API_ENV" AUTH_PORTAL_CLIENT_ID "$(contract portal-client-id)"
  upsert_env "$APP_ENV" EXPO_PUBLIC_INSOLVIA_ENV "local"
  upsert_env "$APP_ENV" EXPO_PUBLIC_COGNITO_DOMAIN "$(contract domain)"
  upsert_env "$APP_ENV" EXPO_PUBLIC_COGNITO_CLIENT_ID "$(contract client-id)"
  upsert_env "$APP_ENV" EXPO_PUBLIC_COGNITO_PORTAL_CLIENT_ID "$(contract portal-client-id)"
  ok "Wrote the pool's ids into services/api/.env and apps/insolvia_app/.env"

  local email subjects='{}' sub
  for email in "${ACCOUNT_EMAILS[@]}"; do
    ensure_account "$email"
    sub="$(user_json "$email" | jq -r '.UserAttributes[] | select(.Name == "sub") | .Value')"
    [[ -n "$sub" ]] || die "Pool user '$email' has no sub."
    subjects="$(jq --arg e "$email" --arg s "$sub" '. + {($e): $s}' <<<"$subjects")"
  done
  offer_to_save_password
  (
    umask 077
    mkdir -p "$CONFIG_DIR"
    printf '%s\n' "$subjects" >"$SUBJECTS_FILE"
  )
  ok "Recorded ${#ACCOUNT_EMAILS[@]} subject(s) in $SUBJECTS_FILE"
}

# ── The data half (no AWS) ──────────────────────────────────────
# PYTHONPATH rather than an install: the service itself is not packaged into
# the venv. The seeder lives in the ADMIN service (#212); the venv is the
# API's, whose pinned boto3 + insolvia_core cover everything it imports.
# The local S3 server's throwaway keys, and no profile: nothing in this half
# may reach AWS.
seed() {
  env -u AWS_PROFILE \
    DATABASE_URL="${DATABASE_URL:-postgresql://insolvia:insolvia@127.0.0.1:5432/insolvia}" \
    CASE_DOCUMENT_ENDPOINT_URL="http://127.0.0.1:9000" \
    AWS_ACCESS_KEY_ID=insolvia AWS_SECRET_ACCESS_KEY=insolvia-local AWS_DEFAULT_REGION=us-east-1 \
    PYTHONPATH="$REPO_ROOT/services/admin/src" \
    "$VENV_PYTHON" -m insolvia_admin.entrypoints.seed load \
    --local "$SUBJECTS_FILE" \
    --fixture "$FIXTURE" \
    --firm-table "$FIRM_TABLE" \
    --case-table "$CASE_TABLE" \
    --document-bucket "$DOCUMENT_BUCKET" \
    "$@"
}

port_open() { (exec 3<>"/dev/tcp/127.0.0.1/$1") >/dev/null 2>&1; }

if [[ "$CHECK_ONLY" -eq 1 ]]; then
  cognito_half_done ||
    die "The Cognito half has not run on this machine (or the fixture names someone new). Re-run without --check."
  seed --check || die "seeds/dev.json is not fully loaded. Re-run without --check."
  ok "Sign-in ready and this machine matches seeds/dev.json."
  exit 0
fi

if [[ "$REFRESH" -eq 1 ]] || ! cognito_half_done; then
  cognito_half
else
  ok "Cognito half already done (subjects in $SUBJECTS_FILE) — no AWS needed."
fi

{ port_open 5432 && port_open 9000; } ||
  die "The local stack is not up (Postgres :5432, S3 :9000). Start ./scripts/dev-up.sh, then re-run."
log "Loading $(basename "$FIXTURE") into the local Postgres and S3 server"
seed
ok "Done. Sign in at http://localhost:3000 as ${ACCOUNT_EMAILS[0]}."
