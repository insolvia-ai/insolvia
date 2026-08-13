#!/usr/bin/env bash
#
# ONE-TIME: clear the guards that would make the naming-rename apply fail
# half-way through an environment.
#
# ## What this is for
#
# The naming refactor moves the environment from the LAST name segment to the
# SECOND (insolvia-api-prod -> insolvia-prod-api). For most resource types
# Terraform does that as a destroy-and-recreate, and prod deliberately protects
# exactly the resources that would be destroyed:
#
#   * Cognito user pool          deletion_protection = ACTIVE
#   * case / firm / admin-audit  deletion_protection_enabled = true
#     DynamoDB tables
#   * case-documents bucket      force_destroy = false
#   * mailer content bucket      force_destroy = false
#   * audit bucket               force_destroy = false, versioned
#
# The container repositories need the same treatment and are deliberately NOT
# here: they are shared across environments, so emptying them is account-wide
# and running this for `staging` would have pulled the images prod depends on.
# They belong to scripts/rename-teardown-ecr.sh, which runs BEFORE the shared
# apply — see that script's order.
#
# Each of those fails the apply at the moment it is reached, leaving the
# environment half-renamed — some resources on the new names, some on the old,
# and a state file that has to be untangled by hand. Clearing them first turns
# one messy multi-attempt apply into one clean one.
#
# ## THIS DESTROYS DATA, AND THAT IS THE POINT
#
# Emptying a bucket here is not a precaution before a destroy — it IS the
# destroy, done early. Every object in the case-documents, mailer-content and
# audit buckets for the named environment is deleted, including every version.
# Every row in the renamed tables and every account in the renamed Cognito pool
# goes when the apply runs. On prod that means real case documents, real firm
# records and every user's password.
#
# There is no migration path bolted on here on purpose: a half-migration that
# looks like it worked is worse than an obvious empty environment.
#
# Usage:
#   ./scripts/rename-teardown.sh <staging|prod> [--check] [--yes]
#
#   --check   report what WOULD be cleared; change nothing
#   --yes     skip the confirmation prompt (still requires the typed env name
#             for prod)

set -euo pipefail

ENV=""
CHECK_ONLY=false
ASSUME_YES=false
REGION="us-east-1"

log()  { printf '\033[1;34m[teardown]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[fail]\033[0m %s\n' "$*" >&2; exit 1; }

for arg in "$@"; do
  case "$arg" in
    staging|prod)
      [[ -z "$ENV" ]] || die "environment given twice ('$ENV' and '$arg')"
      ENV="$arg" ;;
    --check)   CHECK_ONLY=true ;;
    --yes|-y)  ASSUME_YES=true ;;
    -h|--help) sed -n '2,48p' "$0"; exit 0 ;;
    *)         die "unrecognized argument: $arg (see --help)" ;;
  esac
done

[[ -n "$ENV" ]] || die "no environment given. Usage: $0 <staging|prod> [--check] [--yes]"
command -v aws >/dev/null || die "aws CLI not found."
command -v jq  >/dev/null || die "jq not found."

identity="$(aws sts get-caller-identity --output json 2>/dev/null)" \
  || die "No usable AWS credentials. See the insolvia-aws-auth skill."
ACCOUNT="$(printf '%s' "$identity" | jq -r '.Account')"
ARN="$(printf '%s' "$identity" | jq -r '.Arn')"

log "Account    : $ACCOUNT"
log "Identity   : $ARN"
log "Environment: $ENV"

# ── What the OLD names are ──────────────────────────────────────
# Everything below addresses resources by their PRE-rename name, because that
# is what exists in the account when this runs. After the apply these strings
# match nothing, which is the intended way for this script to become inert.
OLD_TABLES=(
  "insolvia-cases-$ENV"
  "insolvia-case-access-log-$ENV"
  "insolvia-firms-$ENV"
  "insolvia-waitlist-$ENV"
  "insolvia-admin-audit-$ENV"
  "insolvia-mailer-messages-$ENV"
  "insolvia-mailer-suppressions-$ENV"
)
OLD_POOL_NAME="insolvia-users-$ENV"
OLD_BUCKETS=(
  "insolvia-web-$ENV"
  "insolvia-web-admin-$ENV"
  "insolvia-marketing-assets-$ENV"
  "insolvia-mailer-content-$ENV"
  "insolvia-case-documents-$ENV"
  "insolvia-audit-$ENV"
)

# ── Confirmation ────────────────────────────────────────────────
if ! $CHECK_ONLY; then
  printf '\n'
  warn "This deletes EVERY object, row and account in the $ENV environment."
  if [[ "$ENV" == "prod" ]]; then
    warn "This is PRODUCTION. Case documents, firm records and user passwords."
    printf 'Type the word prod to continue: '
    read -r reply
    [[ "$reply" == "prod" ]] || die "Aborted."
  elif ! $ASSUME_YES; then
    printf 'Continue? [y/N] '
    read -r reply
    [[ "$reply" == "y" || "$reply" == "Y" ]] || die "Aborted."
  fi
fi

# ── 1. DynamoDB deletion protection ─────────────────────────────
# Terraform's own `deletion_protection_enabled = false` would work too, but only
# as a SEPARATE apply before the rename one — the same apply cannot both clear
# the flag and delete the table, because the flag is read at delete time. Doing
# it out of band here keeps the rename to a single apply.
log "── DynamoDB deletion protection ──────────────"
for table in "${OLD_TABLES[@]}"; do
  if ! aws dynamodb describe-table --table-name "$table" --region "$REGION" >/dev/null 2>&1; then
    continue
  fi
  protected="$(aws dynamodb describe-table --table-name "$table" --region "$REGION" \
    --query 'Table.DeletionProtectionEnabled' --output text)"
  if [[ "$protected" != "True" ]]; then
    log "  $table — already unprotected"
    continue
  fi
  if $CHECK_ONLY; then
    log "  $table — WOULD clear deletion protection"
  else
    aws dynamodb update-table --table-name "$table" --region "$REGION" \
      --no-deletion-protection-enabled >/dev/null
    ok "  $table — deletion protection cleared"
  fi
done

# ── 2. Cognito deletion protection ──────────────────────────────
# The pool is addressed by ID, which has to be resolved from the name: there is
# no describe-by-name API. ListUserPools pages at 60, which is well above what
# this account has.
log "── Cognito deletion protection ───────────────"
pool_id="$(aws cognito-idp list-user-pools --max-results 60 --region "$REGION" \
  --query "UserPools[?Name=='$OLD_POOL_NAME'].Id | [0]" --output text 2>/dev/null || echo "None")"

if [[ "$pool_id" == "None" || -z "$pool_id" ]]; then
  log "  $OLD_POOL_NAME — not found, skipping"
elif $CHECK_ONLY; then
  log "  $OLD_POOL_NAME ($pool_id) — WOULD set DeletionProtection=INACTIVE"
else
  # update-user-pool REPLACES the pool's configuration with what is passed, so
  # the unspecified fields would revert to defaults. That is acceptable here and
  # only here: this pool is about to be destroyed. Do NOT copy this call into
  # anything that expects the pool to survive.
  aws cognito-idp update-user-pool --user-pool-id "$pool_id" --region "$REGION" \
    --deletion-protection INACTIVE >/dev/null
  ok "  $OLD_POOL_NAME ($pool_id) — deletion protection cleared"
fi

# ── 3. Empty the buckets ────────────────────────────────────────
# `aws s3 rm --recursive` removes current versions only, which leaves a
# versioned bucket (the audit one, deliberately) non-empty and still
# undeletable. Both loops are needed.
log "── S3 buckets ────────────────────────────────"
for bucket in "${OLD_BUCKETS[@]}"; do
  if ! aws s3api head-bucket --bucket "$bucket" >/dev/null 2>&1; then
    continue
  fi
  if $CHECK_ONLY; then
    n="$(aws s3api list-object-versions --bucket "$bucket" --output json 2>/dev/null \
      | jq '((.Versions // []) + (.DeleteMarkers // [])) | length' || echo 0)"
    log "  $bucket — WOULD delete $n object version(s)"
    continue
  fi

  log "  $bucket — emptying ..."
  aws s3 rm "s3://$bucket" --recursive --only-show-errors || true

  # Versions and delete markers, a page at a time. The payload is shaped in jq
  # rather than JMESPath: on an empty bucket the Versions/DeleteMarkers keys are
  # ABSENT rather than empty lists, and `// []` is the readable way to say that.
  # delete-objects takes at most 1000 keys per call, hence the loop.
  while true; do
    payload="$(aws s3api list-object-versions --bucket "$bucket" --max-keys 1000 \
      --output json 2>/dev/null \
      | jq -c '{Objects: [((.Versions // []) + (.DeleteMarkers // []))[]
                          | {Key, VersionId}]}' || echo '{"Objects":[]}')"
    count="$(printf '%s' "$payload" | jq '.Objects | length')"
    [[ "$count" -gt 0 ]] || break
    aws s3api delete-objects --bucket "$bucket" --delete "$payload" >/dev/null
    log "    removed $count version(s)/marker(s)"
  done
  ok "  $bucket — empty"
done

if $CHECK_ONLY; then
  printf '\n'
  log "--check: nothing was changed."
  exit 0
fi

cat <<EOF

Cleared.

By this point infra/envs/shared must already be applied and the new
repositories seeded (rename-teardown-ecr.sh -> shared apply ->
bootstrap-ecr-images.sh). If they are not, the $ENV apply fails with
"Source image ... does not exist".

  1. Apply $ENV — in CI, which is the rule for staging and prod
     (infra/CLAUDE.md). The rename itself. Every protection this script cleared
     is re-asserted by the config on the NEW resources, so prod comes back
     protected.

  2. Re-seed. Staging: the seed step in app-staging.yml, on the next deploy.
     Prod: there is no seeding path — the pool and the firm table are empty and
     every account has to be re-created.

EOF
ok "$ENV ready for the rename apply"
