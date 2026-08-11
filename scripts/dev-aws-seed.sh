#!/usr/bin/env bash
#
# Make this machine's dev environment signable-in AND seeded: ensure the dev
# account exists in THIS MACHINE's Cognito pool, then load seeds/dev.json into
# this machine's dev data stores.
#
#   ./scripts/dev-aws-seed.sh
#   ./scripts/dev-aws-seed.sh --check     # report what is missing, write nothing
#
# ## Why this exists
#
# There is no sign-up screen, and there is not supposed to be one. Every
# Insolvia pool sets `allow_admin_create_user_only = true`
# (infra/modules/auth/main.tf) — a public sign-up form on a bankruptcy-filing
# platform is an invitation to junk accounts, not a growth channel. So the
# hosted UI offers sign-in and nothing else, and `admin-create-user` is the
# only way an account comes into existence.
#
# An account gets you PAST sign-in and no further: the account lives in
# Cognito, firm membership lives in DynamoDB, and without a firm every route
# behind current_accessor() answers 403 ("accessor unresolved /
# no_active_firm_user" in the API log). That is the tenancy model working as
# designed (ADR 0009), and the first firm cannot be made through the API —
# POST /v1/firm/users is itself behind FIRM_ADMINISTRATION, so it needs an
# admin to make an admin. Those used to be two scripts run in an order you had
# to know; the refusal when you didn't cost real time, so now this one script
# does both, in the only order that works.
#
# ## The password, and ~/.config/insolvia/dev.env
#
# The account step needs a password exactly once per pool lifetime (and again
# after dev-aws-reset.sh wipes the pool). It comes from, in order:
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
# thing: sign-in to this machine's own dev pool, whose entire contents are
# this fixture. Nothing here may reach staging or prod (see Scope), whose
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
# app-staging.yml, so the two environments differ in their fixture and their
# tables, never in the code path that built the rows. The account step lives
# HERE in the wrapper, not in the loader, for the same reason: staging's
# accounts come from its fixture (password via ${E2E_TEST_USER_PASSWORD}), so
# a prompt in the loader would be a dev-only fork of a CI-shared path.
#
# ## Why this is shell and the loading is Python
#
# The split follows one question: does the step need to know the shape of our
# data? Not here. This file resolves an ENVIRONMENT — the machine id, the table
# and pool names in this machine's Terraform state, and, the load-bearing part,
# AWS credentials an SDK can actually read. The `aws login` session format is
# unreadable by boto3 and by Terraform's provider, which is what
# `export_temporary_aws_credentials` in dev-aws-common.sh exists to fix, and a
# child process cannot fix its own parent's environment. Run the loader without
# this wrapper and it fails with NoCredentialsError.
#
# Building rows does need the shape, so that is Python next to the code owning
# it: services/admin/src/insolvia_admin/entrypoints/seed.py.
#
# ## Scope
#
# DEV ONLY, and not parameterised on purpose. A script that creates accounts
# and could be pointed at the prod pool by changing one argument is a script
# that eventually is. The names come from THIS machine's Terraform state and
# must carry this machine's short id; the loader re-checks each name's shape
# itself, so neither guard is load-bearing alone. Staging is seeded by
# app-staging.yml calling the loader directly, and prod is refused outright.
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
  -h | --help)
    printf 'Usage: %s [--check]\n\n' "$0"
    printf '  DEV_USER_PASSWORD  optional — read from %s,\n' "$DEV_ENV_FILE"
    printf '                     or prompted for without echo, when an account must be created\n\n'
    printf 'Ensures the dev sign-in account(s) named by seeds/dev.json exist in this\n'
    printf 'machine'"'"'s pool, then loads that fixture into this machine'"'"'s dev tables.\n'
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

# The loader runs through the API's own code, so it needs the API's venv — the
# one services/api/scripts/dev-setup.sh creates. Checked here rather than left
# to a ModuleNotFoundError, which names `boto3` and not the missing step.
VENV_PYTHON="$API_DIR/.venv/bin/python"
[[ -x "$VENV_PYTHON" ]] ||
  die "No API venv at $VENV_PYTHON. Run ./services/api/scripts/dev-setup.sh first."

load_machine_id false
load_aws_identity
terraform_init
outputs="$(terraform_output_json)"

# Belt and braces on "dev only", exactly as dev-aws-reset.sh does before it
# deletes anything: every name must carry THIS machine's short id.
FIRM_TABLE="$(jq -r '.firm_table_name.value // empty' <<<"$outputs")"
[[ -n "$FIRM_TABLE" ]] ||
  die "No firm_table_name in this machine's Terraform state. Run ./scripts/dev-aws-setup.sh first."
[[ "$FIRM_TABLE" == "$FIRM_TABLE_NAME_EXPECTED" ]] ||
  die "Refusing: firm table '$FIRM_TABLE' is not '$FIRM_TABLE_NAME_EXPECTED'."

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
# The addresses are all `.test` ones — a reserved TLD (RFC 2606) that can
# never be a real mailbox, which matters twice over: this repo is public, and
# Cognito would otherwise be able to mail a stranger. Nothing is emailed
# anyway — see SUPPRESS below.
# Not mapfile — macOS ships bash 3.2 and these scripts run on it.
ACCOUNT_EMAILS=()
while IFS= read -r email; do
  ACCOUNT_EMAILS+=("$email")
done < <(jq -r '[.firms[].users[] | select(has("password") | not) | .email] | unique | .[]' "$FIXTURE")
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
# PYTHONPATH rather than an install: the venv has the runtime dependencies but
# the service itself is not packaged into it (pyproject declares no dependencies
# and nothing runs `pip install -e .`), so `src` has to be on the path the same
# way pytest's `pythonpath` setting puts it there.
#
# The seeder lives in the ADMIN service (#212 — provisioning tooling has one
# home), while the venv is still the API's: its pinned boto3 + insolvia_core
# cover everything the seeder imports, and a second venv for one entrypoint
# would be setup for setup's sake.
seed() {
  PYTHONPATH="$REPO_ROOT/services/admin/src" "$VENV_PYTHON" -m insolvia_admin.entrypoints.seed \
    --fixture "$FIXTURE" \
    --firm-table "$FIRM_TABLE" \
    --user-pool-id "$POOL_ID" \
    "$@"
}

if [[ "$CHECK_ONLY" -eq 1 ]]; then
  for email in "${ACCOUNT_EMAILS[@]}"; do
    user_is_confirmed "$email" ||
      die "No CONFIRMED user '$email' in $pool_name. Re-run without --check to create it."
  done
  seed --check || die "seeds/dev.json is not fully loaded. Re-run without --check."
  ok "Sign-in ready and this machine matches seeds/dev.json."
  exit 0
fi

for email in "${ACCOUNT_EMAILS[@]}"; do
  ensure_account "$email"
done
offer_to_save_password

log "Loading $(basename "$FIXTURE") into $FIRM_TABLE"
seed
ok "Done. Start ./scripts/dev-up.sh if it is not running, then sign in at http://localhost:3000 as ${ACCOUNT_EMAILS[0]}."
