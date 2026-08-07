#!/usr/bin/env bash
#
# Create a sign-in account in THIS MACHINE's dev Cognito pool.
#
#   ./scripts/dev-aws-create-user.sh
#   ./scripts/dev-aws-create-user.sh --check     # report, change nothing
#
# ## Why this script has to exist
#
# There is no sign-up screen, and there is not supposed to be one. Every
# Insolvia pool sets `allow_admin_create_user_only = true`
# (infra/modules/auth/main.tf) — a public sign-up form on a bankruptcy-filing
# platform is an invitation to junk accounts, not a growth channel. So the
# hosted UI offers sign-in and nothing else, and `admin-create-user` is the
# only way an account comes into existence.
#
# That is correct for staging and prod and merely puzzling on a laptop, where
# it presents as "I ran dev-up.sh, I have a sign-in page, and no way past it."
# This is the way past it. It is the dev counterpart of
# scripts/e2e-create-test-user.sh, which does the same job for the staging pool.
#
# ## Why there is a SECOND call
#
# `admin-create-user` leaves the account in FORCE_CHANGE_PASSWORD, and the
# hosted UI answers a sign-in for such a user with a "set a new password"
# challenge. `admin-set-user-password --permanent` is what makes the very first
# sign-in a plain sign-in.
#
# ## The password
#
# Never defaulted, never generated, never printed, never written to a file in
# this repo, and never passed on a command line where `ps` could see it. It
# comes from DEV_USER_PASSWORD or a no-echo prompt, is handed to exactly one
# AWS call through a mode-0600 temporary file, and that file is deleted on
# every exit path.
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

# DEV ONLY, and not parameterised on purpose — the same reasoning
# e2e-create-test-user.sh gives for being staging-only. A "create me an
# account" script that could be pointed at the prod pool by changing one
# argument is a script that eventually is.
CHECK_ONLY=0
while [[ $# -gt 0 ]]; do
  case "$1" in
  --check) CHECK_ONLY=1 ;;
  -h | --help)
    printf 'Usage: %s [--check]\n\n' "$0"
    printf '  DEV_USER_EMAIL     optional — defaults to dev@insolvia.test\n'
    printf '  DEV_USER_PASSWORD  optional — prompted for, without echo, when unset\n\n'
    printf 'Creates a sign-in account in this machine'"'"'s own dev Cognito pool.\n'
    printf 'There is no sign-up screen by design; this is the only way to get one.\n'
    exit 0
    ;;
  *) die "Unknown option: $1" ;;
  esac
  shift
done

for command in aws jq terraform; do require_command "$command"; done

load_machine_id false
load_aws_identity
terraform_init
outputs="$(terraform_output_json)"

POOL_ID="$(jq -r '.auth_user_pool_id.value // empty' <<<"$outputs")"
[[ -n "$POOL_ID" ]] ||
  die "No auth_user_pool_id in this machine's Terraform state. Run ./scripts/dev-aws-setup.sh first."
[[ "$POOL_ID" =~ ^[a-z0-9-]+_[A-Za-z0-9]+$ ]] ||
  die "'$POOL_ID' does not look like a Cognito user pool id — refusing to continue."

# Belt and braces on "dev only": the pool this resolves to must carry THIS
# machine's short id in its name, or something has pointed us at a shared pool.
pool_name="$(aws_dev cognito-idp describe-user-pool --user-pool-id "$POOL_ID" \
  --query 'UserPool.Name' --output text)"
[[ "$pool_name" == "$USER_POOL_NAME_EXPECTED" ]] ||
  die "Refusing: pool '$POOL_ID' is named '$pool_name', not '$USER_POOL_NAME_EXPECTED'."

# `.test` is a reserved TLD (RFC 2606) and can never be a real mailbox, which
# matters twice over: this repo is public, and Cognito would otherwise be able
# to mail a stranger. Nothing is emailed anyway — see SUPPRESS below.
EMAIL="${DEV_USER_EMAIL:-dev@insolvia.test}"
[[ "$EMAIL" == *@*.* ]] || die "DEV_USER_EMAIL ('$EMAIL') is not an email address."

# Email IS the username on these pools (`username_attributes = ["email"]`).
USERNAME="$EMAIL"

user_json() {
  aws_dev cognito-idp admin-get-user \
    --user-pool-id "$POOL_ID" --username "$USERNAME" --output json 2>/dev/null
}

if [[ "$CHECK_ONLY" -eq 1 ]]; then
  existing="$(user_json)" || die "No user '$USERNAME' in $pool_name. Run without --check to create it."
  status="$(jq -r '.UserStatus' <<<"$existing")"
  [[ "$status" == "CONFIRMED" ]] ||
    die "User '$USERNAME' is $status, not CONFIRMED — the hosted UI will challenge it. Re-run without --check."
  ok "'$USERNAME' exists in $pool_name and is CONFIRMED."
  exit 0
fi

PASSWORD="${DEV_USER_PASSWORD:-}"
if [[ -z "$PASSWORD" ]]; then
  read -rsp "Password for $USERNAME (not echoed, not stored): " PASSWORD
  printf '\n'
fi
[[ -n "$PASSWORD" ]] || die "No password given."

# Checked against the pool's own policy here (12+, upper, lower, digit;
# symbols allowed, not required — infra/modules/auth/main.tf) so a weak one
# fails in one line rather than as an InvalidPasswordException two calls later.
{
  [[ ${#PASSWORD} -ge 12 ]] &&
    [[ "$PASSWORD" == *[[:lower:]]* ]] &&
    [[ "$PASSWORD" == *[[:upper:]]* ]] &&
    [[ "$PASSWORD" == *[[:digit:]]* ]]
} || die "Password does not meet the pool policy: 12+ characters with at least one lower-case
       letter, one upper-case letter and one digit."

# ── 1. Create, idempotently ─────────────────────────────────────
# --message-action SUPPRESS is not optional: without it Cognito emails a
# temporary password to the address. The address is deliberately unroutable,
# so that mail would only ever bounce — and SES is still in the sandbox.
if user_json >/dev/null; then
  log "User '$USERNAME' already exists — leaving it in place."
else
  log "Creating '$USERNAME' in $pool_name"
  aws_dev cognito-idp admin-create-user \
    --user-pool-id "$POOL_ID" \
    --username "$USERNAME" \
    --user-attributes "Name=email,Value=$EMAIL" "Name=email_verified,Value=true" \
    --message-action SUPPRESS \
    --output json >/dev/null ||
    die "admin-create-user failed. If this is an AccessDenied, see the insolvia-aws-auth skill."
  ok "Created."
fi

# ── 2. Make the password permanent ──────────────────────────────
# Through --cli-input-json from a mode-0600 temp file rather than --password,
# so the value never appears in this process's argv and therefore never in
# `ps` output nor in a shell history that captured the command line.
log "Setting a permanent password (clears FORCE_CHANGE_PASSWORD)"
umask 077
payload="$(mktemp "${TMPDIR:-/tmp}/insolvia-dev-user.XXXXXX")"
trap 'rm -f "$payload"' EXIT
jq -n --arg pool "$POOL_ID" --arg user "$USERNAME" --arg password "$PASSWORD" \
  '{UserPoolId: $pool, Username: $user, Password: $password, Permanent: true}' >"$payload"
aws_dev cognito-idp admin-set-user-password --cli-input-json "file://$payload" ||
  die "admin-set-user-password failed. If it rejected the password it did not meet the policy;
       if it was AccessDenied, see the insolvia-aws-auth skill."
rm -f "$payload"

# ── 3. Verify ───────────────────────────────────────────────────
final="$(user_json)" || die "The user disappeared between calls — re-run this script."
status="$(jq -r '.UserStatus' <<<"$final")"
[[ "$status" == "CONFIRMED" ]] ||
  die "User is $status after setting a permanent password, expected CONFIRMED."

ok "Sign-in ready: $USERNAME in $pool_name"
# Sign-in is half of it. Without a firm this account resolves to no accessor
# and every route behind current_accessor() answers 403 — see ADR 0009, and
# dev-aws-create-firm.sh's header for why the API cannot make the first one.
log "Next: ./scripts/dev-aws-create-firm.sh — without a firm every API route answers 403."
log "Then start the system with ./scripts/dev-up.sh, open http://localhost:3000, and sign in with that address."
