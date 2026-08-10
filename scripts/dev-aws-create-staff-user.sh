#!/usr/bin/env bash
#
# Create a STAFF sign-in account in THIS MACHINE's dev staff Cognito pool.
#
#   ./scripts/dev-aws-create-staff-user.sh
#   ./scripts/dev-aws-create-staff-user.sh --check     # report, change nothing
#
# The staff pool (insolvia-staff-dev-<machine-id>, #209) is the admin portal's
# issuer — a second pool, separate from the firm pool dev-aws-create-user.sh
# fills, so the cross-issuer boundary is real on a laptop too. Like every
# Insolvia pool it is admin-create-only; this script is the way in, exactly as
# its sibling is for the firm pool. The header of dev-aws-create-user.sh owns
# the full reasoning (why the script exists, why there is a second AWS call,
# how the password is handled); this one restates only what differs:
#
#   - The pool REQUIRES TOTP (`mfa_configuration = "ON"`). The permanent
#     password set here still stands, and the FIRST hosted-UI sign-in walks
#     authenticator enrollment before it completes. That is the intended flow,
#     not a broken state — keep the authenticator entry; you will need it on
#     every sign-in.
#   - The default address is dev-staff@insolvia.test, so the two dev accounts
#     are distinguishable at a glance.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=/dev/null
source "$SCRIPT_DIR/dev-aws-common.sh"

# DEV ONLY, and not parameterised on purpose — same fence as the sibling
# script: a "create me a staff account" that could be pointed at the prod
# staff pool by changing one argument is a script that eventually is.
CHECK_ONLY=0
while [[ $# -gt 0 ]]; do
  case "$1" in
  --check) CHECK_ONLY=1 ;;
  -h | --help)
    printf 'Usage: %s [--check]\n\n' "$0"
    printf '  DEV_STAFF_EMAIL     optional — defaults to dev-staff@insolvia.test\n'
    printf '  DEV_STAFF_PASSWORD  optional — prompted for, without echo, when unset\n\n'
    printf 'Creates a staff account in this machine'"'"'s own dev STAFF Cognito pool\n'
    printf '(the admin portal'"'"'s issuer). TOTP enrollment runs at first sign-in.\n'
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

POOL_ID="$(jq -r '.staff_auth_user_pool_id.value // empty' <<<"$outputs")"
[[ -n "$POOL_ID" ]] ||
  die "No staff_auth_user_pool_id in this machine's Terraform state. Run ./scripts/dev-aws-setup.sh first (it applies the staff pool)."
[[ "$POOL_ID" =~ ^[a-z0-9-]+_[A-Za-z0-9]+$ ]] ||
  die "'$POOL_ID' does not look like a Cognito user pool id — refusing to continue."

# Belt and braces on "dev only": the pool this resolves to must carry THIS
# machine's short id in its name, or something has pointed us at a shared pool.
pool_name="$(aws_dev cognito-idp describe-user-pool --user-pool-id "$POOL_ID" \
  --query 'UserPool.Name' --output text)"
[[ "$pool_name" == "$STAFF_POOL_NAME_EXPECTED" ]] ||
  die "Refusing: pool '$POOL_ID' is named '$pool_name', not '$STAFF_POOL_NAME_EXPECTED'."

# `.test` is reserved (RFC 2606) and can never be a real mailbox — this repo is
# public, and nothing is emailed anyway (SUPPRESS below).
EMAIL="${DEV_STAFF_EMAIL:-dev-staff@insolvia.test}"
[[ "$EMAIL" == *@*.* ]] || die "DEV_STAFF_EMAIL ('$EMAIL') is not an email address."

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

PASSWORD="${DEV_STAFF_PASSWORD:-}"
if [[ -z "$PASSWORD" ]]; then
  read -rsp "Password for $USERNAME (not echoed, not stored): " PASSWORD
  printf '\n'
fi
[[ -n "$PASSWORD" ]] || die "No password given."

# Checked against the pool's own policy here (12+, upper, lower, digit;
# symbols allowed, not required — infra/modules/staff_auth/main.tf) so a weak
# one fails in one line rather than as an InvalidPasswordException two calls
# later.
{
  [[ ${#PASSWORD} -ge 12 ]] &&
    [[ "$PASSWORD" == *[[:lower:]]* ]] &&
    [[ "$PASSWORD" == *[[:upper:]]* ]] &&
    [[ "$PASSWORD" == *[[:digit:]]* ]]
} || die "Password does not meet the pool policy: 12+ characters with at least one lower-case
       letter, one upper-case letter and one digit."

# ── 1. Create, idempotently ─────────────────────────────────────
# --message-action SUPPRESS is not optional: without it Cognito emails a
# temporary password to the address, which is deliberately unroutable.
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
payload="$(mktemp "${TMPDIR:-/tmp}/insolvia-dev-staff-user.XXXXXX")"
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

ok "Staff sign-in ready: $USERNAME in $pool_name"
log "First sign-in runs TOTP enrollment (the pool requires MFA) — have an authenticator app ready."
log "This account signs into the ADMIN PORTAL (localhost:3100), not the app; it has no firm and never appears in insolvia-firms-*."
