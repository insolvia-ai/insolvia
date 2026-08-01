#!/usr/bin/env bash
#
# Create — or converge — the dedicated E2E test user in the STAGING Cognito
# pool, so `.github/workflows/app-staging.yml`'s post-deploy auth round trip has
# an identity to sign in as. One-time setup; see
# docs/runbooks/staging-e2e-setup.md for where it sits in the order.
#
#   E2E_TEST_USER_EMAIL='e2e@…' ./scripts/e2e-create-test-user.sh
#   ./scripts/e2e-create-test-user.sh --check      # verify, change nothing
#
# ## Why an admin creates it
#
# Self-signup is disabled on every Insolvia pool (`allow_admin_create_user_only
# = true` in infra/modules/auth/main.tf — a public sign-up form on a
# bankruptcy-filing platform is an invitation to junk accounts, not a growth
# channel). `admin-create-user` is therefore the only path to a user at all.
#
# ## Why there is a SECOND call
#
# `admin-create-user` leaves the account in `FORCE_CHANGE_PASSWORD`, and the
# hosted UI answers a sign-in for such a user with a "set a new password"
# challenge. A browser test has nothing to say to that screen, so the E2E job
# would hang until its timeout and take the staging run — and with it every
# production promotion of that commit — down with it. `admin-set-user-password
# --permanent` is what makes the very first sign-in a plain sign-in.
#
# ## AWS credentials
#
# This needs a staging-capable AWS session. If one is not working, read the
# `insolvia-aws-auth` skill — the mechanics live there and are not restated
# here. This script only READS Terraform state (`init` + `output`); it applies
# nothing. Infra applies happen in CI, never from a CLI (infra/CLAUDE.md).
#
# ## The password
#
# It is never defaulted, never generated, never printed, never written to a file
# in this repo, and never passed on a command line where `ps` could see it. It
# comes from `E2E_TEST_USER_PASSWORD` or a no-echo prompt, is handed to exactly
# one AWS call through a mode-0600 temporary file that is deleted on exit, and
# leaves no other trace.
#
set -euo pipefail

# STAGING ONLY, and not parameterised on purpose. The prod pool holds real
# attorney accounts; a "create a test user" script that could be pointed at it
# by changing one argument is a script that eventually is.
readonly ENV_NAME="staging"
readonly TF_DIR="infra/envs/${ENV_NAME}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

log()  { printf '\033[1;34m[e2e-user]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[error]\033[0m %s\n' "$*" >&2; exit 1; }

CHECK_ONLY=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --check) CHECK_ONLY=1 ;;
    --help|-h)
      printf 'Usage: %s [--check]\n\n' "$0"
      printf '  E2E_TEST_USER_EMAIL     required — the test user (a dedicated synthetic address)\n'
      printf '  E2E_TEST_USER_PASSWORD  optional — prompted for, without echo, when unset\n'
      exit 0 ;;
    *) die "Unknown option: $1" ;;
  esac
  shift
done

for command in aws jq terraform; do
  command -v "$command" >/dev/null || die "$command is not installed. Run ./scripts/dev-setup.sh."
done

# ── Credentials ──────────────────────────────────────────────────────
# Terraform's SDK cannot read the `aws login` session format, so resolve it into
# environment variables first — the same dance scripts/apply-ci-trust.sh and
# scripts/dev-aws-common.sh do, for the same reason.
log "Checking AWS session"
aws sts get-caller-identity >/dev/null 2>&1 ||
  die "AWS session is expired or absent. Run 'aws login' (see the insolvia-aws-auth skill)."
eval "$(aws configure export-credentials --format env)"
ok "Authenticated as $(aws sts get-caller-identity --query Arn --output text)"

# ── The pool id comes from Terraform, never from a literal ───────────
# A hard-coded pool id in a public repo is both a leak and a lie the first time
# the pool is recreated.
log "Reading the ${ENV_NAME} user pool id from Terraform state (read-only)"
terraform -chdir="$REPO_ROOT/$TF_DIR" init -input=false >/dev/null ||
  die "terraform init failed in $TF_DIR — check the AWS session and the S3 backend."
POOL_ID="$(terraform -chdir="$REPO_ROOT/$TF_DIR" output -raw auth_user_pool_id)" ||
  die "No 'auth_user_pool_id' output in $TF_DIR. Has ${ENV_NAME} been applied?"
[[ "$POOL_ID" =~ ^[a-z0-9-]+_[A-Za-z0-9]+$ ]] ||
  die "'$POOL_ID' does not look like a Cognito user pool id — refusing to continue."
log "User pool: $POOL_ID"

# ── The test user's address ──────────────────────────────────────────
EMAIL="${E2E_TEST_USER_EMAIL:-}"
[[ -n "$EMAIL" ]] || die "E2E_TEST_USER_EMAIL is not set. Export the address of the dedicated E2E user."
[[ "$EMAIL" == *@*.* ]] || die "E2E_TEST_USER_EMAIL ('$EMAIL') is not an email address."

# Email IS the username on these pools (`username_attributes = ["email"]`).
readonly USERNAME="$EMAIL"

user_json() {
  aws cognito-idp admin-get-user \
    --user-pool-id "$POOL_ID" --username "$USERNAME" --output json 2>/dev/null
}

# MFA is OPTIONAL on the pool (infra/modules/auth/main.tf), which means a user
# CAN enrol a TOTP authenticator — and if this one ever does, the hosted UI will
# present a code challenge the browser test cannot answer, so the E2E job hangs
# until timeout and reddens staging. Refusing here turns a mystifying 90-second
# hang into one sentence.
assert_no_mfa() {
  local devices
  devices="$(jq -r '(.UserMFASettingList // []) | join(", ")' <<<"$1")"
  [[ -z "$devices" ]] ||
    die "The E2E user has MFA enrolled ($devices). The auth round trip has no way through a
       TOTP challenge and would hang. Remove the factor (admin-set-user-mfa-preference) or use
       a different address — never enrol MFA on this account."
}

# ── --check: report, change nothing ──────────────────────────────────
if [[ "$CHECK_ONLY" -eq 1 ]]; then
  existing="$(user_json)" ||
    die "No user '$USERNAME' in $POOL_ID. Run this script without --check to create it."
  status="$(jq -r '.UserStatus' <<<"$existing")"
  assert_no_mfa "$existing"
  [[ "$status" == "CONFIRMED" ]] ||
    die "User '$USERNAME' is $status, not CONFIRMED — the hosted UI will challenge it and the
       E2E job will hang. Re-run this script without --check to set a permanent password."
  ok "E2E user exists in $POOL_ID, is CONFIRMED, and has no MFA factor enrolled."
  exit 0
fi

# ── The password ─────────────────────────────────────────────────────
PASSWORD="${E2E_TEST_USER_PASSWORD:-}"
if [[ -z "$PASSWORD" ]]; then
  read -rsp "Password for $USERNAME (not echoed, not stored): " PASSWORD
  printf '\n'
fi
[[ -n "$PASSWORD" ]] || die "No password given."

# Check it against the pool's own policy locally (12+, upper, lower, digit;
# symbols not required — infra/modules/auth/main.tf), so a weak password fails
# in one line here rather than as an InvalidPasswordException three calls later.
{
  [[ ${#PASSWORD} -ge 12 ]] &&
  [[ "$PASSWORD" == *[[:lower:]]* ]] &&
  [[ "$PASSWORD" == *[[:upper:]]* ]] &&
  [[ "$PASSWORD" == *[[:digit:]]* ]]
} || die "Password does not meet the pool policy: 12+ characters with at least one lower-case
       letter, one upper-case letter and one digit. (Symbols are allowed, not required.)"

# ── 1. Create, idempotently ──────────────────────────────────────────
# `--message-action SUPPRESS` is not optional: without it Cognito emails a
# temporary password to the address, which is noise at best and, while SES is
# still in the sandbox, a bounce at worst.
if user_json >/dev/null; then
  log "User '$USERNAME' already exists — leaving it in place."
else
  log "Creating '$USERNAME' in $POOL_ID"
  aws cognito-idp admin-create-user \
    --user-pool-id "$POOL_ID" \
    --username "$USERNAME" \
    --user-attributes "Name=email,Value=$EMAIL" "Name=email_verified,Value=true" \
    --message-action SUPPRESS \
    --output json >/dev/null ||
    die "admin-create-user failed. If this is an AccessDenied, your session cannot administer
       the ${ENV_NAME} pool — see the insolvia-aws-auth skill."
  ok "Created."
fi

# ── 2. Make the password permanent ───────────────────────────────────
# Via --cli-input-json from a mode-0600 temp file rather than --password, so the
# value never appears in this process's argv (and therefore never in `ps` output
# on a shared machine, nor in a shell history that captured the command line).
# The file is created with a restrictive umask and removed on every exit path.
log "Setting a permanent password (clears FORCE_CHANGE_PASSWORD)"
umask 077
payload="$(mktemp "${TMPDIR:-/tmp}/insolvia-e2e.XXXXXX")"
trap 'rm -f "$payload"' EXIT
jq -n --arg pool "$POOL_ID" --arg user "$USERNAME" --arg password "$PASSWORD" \
  '{UserPoolId: $pool, Username: $user, Password: $password, Permanent: true}' >"$payload"
aws cognito-idp admin-set-user-password --cli-input-json "file://$payload" ||
  die "admin-set-user-password failed. If it rejected the password, it did not meet the pool
       policy; if it was AccessDenied, see the insolvia-aws-auth skill."
rm -f "$payload"

# ── 3. Verify what we just did ───────────────────────────────────────
final="$(user_json)" || die "The user disappeared between calls — re-run this script."
status="$(jq -r '.UserStatus' <<<"$final")"
assert_no_mfa "$final"
[[ "$status" == "CONFIRMED" ]] ||
  die "User is $status after setting a permanent password, expected CONFIRMED."

ok "E2E user is ready in $POOL_ID (CONFIRMED, no MFA)."
warn "Never enrol MFA on this account — the E2E flow cannot answer a TOTP challenge."
log "Next: ./scripts/e2e-set-secrets.sh  (puts the same two values in the insolvia-staging environment)"
