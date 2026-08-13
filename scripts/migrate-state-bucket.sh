#!/usr/bin/env bash
#
# ONE-TIME: move Terraform state from `insolvia-terraform-state` to
# `insolvia-shared-terraform-state-us-east-1`.
#
# The old bucket was the account's last non-conforming name — no environment
# segment and no region suffix, on the one bucket whose name every root's
# backend hard-codes. The new name is what the insolvia-aws-naming skill asks
# for: shared is the environment, us-east-1 is S3's global-uniqueness suffix.
#
# ## Why this is a script and not a Terraform change
#
# The state bucket is created by hand in docs/runbooks/aws-bootstrap.md, before
# any Terraform exists to create it — a root that managed its own backend would
# have to already have a backend. So it is bootstrap, and moving it is bootstrap
# too.
#
# ## Why it COPIES rather than moves
#
# Every other resource in this rename is destroy-and-recreate and the data loss
# is accepted. State is the exception: losing it does not lose data, it loses
# the ability to destroy the resources the state described, which leaves them
# orphaned in the account with nothing to manage them. So this syncs, verifies
# key-for-key, and leaves the old bucket completely untouched. Deleting it is a
# separate, later, deliberate act — see the end of this file.
#
# ## Order (the whole rename, not just this step)
#
#   1. THIS SCRIPT                      — copy state, human credentials
#   2. terraform init -reconfigure      — every root, so it reads the new bucket
#   3. scripts/apply-ci-trust.sh        — human; renames the deploy + seed roles
#                                         and repoints every ARN pattern
#   4. update the AWS_ROLE_ARN and AWS_SEED_ROLE_ARN GitHub secrets — the role
#      names changed, so the OLD ARNs no longer resolve and every deploy fails
#      at configure-aws-credentials with a role-not-found
#   5. scripts/rename-teardown.sh <env> — clears deletion protection and empties
#                                         the buckets the rename apply destroys
#   6. apply shared, then staging / prod
#   7. scripts/bootstrap-ecr-images.sh <env> — the new repositories are empty,
#      and an Image-package Lambda cannot be created from an empty repository
#
# Steps 3 and 4 are the pair that bricks CI if either is skipped alone.
#
# Usage:
#   ./scripts/migrate-state-bucket.sh [--check] [--yes]
#
#   --check   compare the two buckets and report; change nothing
#   --yes     skip the confirmation prompt

set -euo pipefail

OLD_BUCKET="insolvia-terraform-state"
NEW_BUCKET="insolvia-shared-terraform-state-us-east-1"
REGION="us-east-1"

CHECK_ONLY=false
ASSUME_YES=false

log()  { printf '\033[1;34m[state-migrate]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[fail]\033[0m %s\n' "$*" >&2; exit 1; }

for arg in "$@"; do
  case "$arg" in
    --check)   CHECK_ONLY=true ;;
    --yes|-y)  ASSUME_YES=true ;;
    -h|--help) sed -n '2,48p' "$0"; exit 0 ;;
    *)         die "unrecognized argument: $arg" ;;
  esac
done

command -v aws >/dev/null || die "aws CLI not found."

# ── Credentials ─────────────────────────────────────────────────
# The deploy role cannot do this: until step 3 its policy still names the OLD
# bucket, so it cannot write the new one — and it is denied editing its own
# policy anyway (DenySelfPrivilegeEscalation). Human credentials, deliberately.
identity="$(aws sts get-caller-identity --output json 2>/dev/null)" \
  || die "No usable AWS credentials. See the insolvia-aws-auth skill."
arn="$(printf '%s' "$identity" | jq -r '.Arn')"
account="$(printf '%s' "$identity" | jq -r '.Account')"

case "$arn" in
  *:assumed-role/insolvia-shared-deploy-role/*|*:assumed-role/insolvia-github-actions/*)
    die "Running as the CI deploy role. This is a human-applied bootstrap step — use your own IAM user."
    ;;
esac

log "Account : $account"
log "Identity: $arn"
log "From    : s3://$OLD_BUCKET"
log "To      : s3://$NEW_BUCKET"

aws s3api head-bucket --bucket "$OLD_BUCKET" >/dev/null 2>&1 \
  || die "Source bucket s3://$OLD_BUCKET does not exist or is unreadable."

# ── Report ──────────────────────────────────────────────────────
list_keys() {
  aws s3api list-objects-v2 --bucket "$1" --query 'Contents[].Key' --output text 2>/dev/null \
    | tr '\t' '\n' | grep -v '^None$' | sort || true
}

old_keys="$(list_keys "$OLD_BUCKET")"
old_count="$(printf '%s' "$old_keys" | grep -c . || true)"
log "Source holds $old_count object(s):"
printf '%s\n' "$old_keys" | sed 's/^/         /'

if $CHECK_ONLY; then
  if aws s3api head-bucket --bucket "$NEW_BUCKET" >/dev/null 2>&1; then
    new_keys="$(list_keys "$NEW_BUCKET")"
    log "Destination exists. Keys present in source but NOT in destination:"
    comm -23 <(printf '%s\n' "$old_keys") <(printf '%s\n' "$new_keys") | sed 's/^/         /'
  else
    log "Destination s3://$NEW_BUCKET does not exist yet."
  fi
  exit 0
fi

if ! $ASSUME_YES; then
  printf '\nCopy %s object(s) to s3://%s? The source is left untouched. [y/N] ' "$old_count" "$NEW_BUCKET"
  read -r reply
  [[ "$reply" == "y" || "$reply" == "Y" ]] || die "Aborted."
fi

# ── Create the destination ──────────────────────────────────────
# Same four properties the bootstrap runbook gives the original: versioning
# (state history and the recovery path from a bad apply), SSE, and a full public
# access block. Versioning FIRST, so no object can land unversioned.
if aws s3api head-bucket --bucket "$NEW_BUCKET" >/dev/null 2>&1; then
  log "Destination already exists — reusing it."
else
  log "Creating s3://$NEW_BUCKET ..."
  # us-east-1 takes no LocationConstraint; passing one is an error.
  aws s3api create-bucket --bucket "$NEW_BUCKET" --region "$REGION" >/dev/null
  ok "created"
fi

aws s3api put-bucket-versioning --bucket "$NEW_BUCKET" \
  --versioning-configuration Status=Enabled
aws s3api put-bucket-encryption --bucket "$NEW_BUCKET" \
  --server-side-encryption-configuration \
  '{"Rules":[{"ApplyServerSideEncryptionByDefault":{"SSEAlgorithm":"AES256"}}]}'
aws s3api put-public-access-block --bucket "$NEW_BUCKET" \
  --public-access-block-configuration \
  'BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true'
ok "versioning + encryption + public access block set"

# ── Copy ────────────────────────────────────────────────────────
# Only the CURRENT version of each object moves. Old state versions stay in the
# source bucket, which is the reason it is kept rather than deleted: a rollback
# to a pre-migration state reads from there.
#
# .tflock objects are excluded: they are ephemeral lock markers, and copying a
# stale one would make the new bucket look locked to the next init.
log "Copying (current versions only, excluding locks) ..."
aws s3 sync "s3://$OLD_BUCKET" "s3://$NEW_BUCKET" --exclude '*.tflock'
ok "sync complete"

# ── Verify ──────────────────────────────────────────────────────
# Key-for-key, not "the sync exited 0". A partial copy that nobody checked is
# how a root ends up initialising against a state file that is missing.
new_keys="$(list_keys "$NEW_BUCKET")"
missing="$(comm -23 \
  <(printf '%s\n' "$old_keys" | grep -v '\.tflock$' || true) \
  <(printf '%s\n' "$new_keys") || true)"

if [[ -n "$missing" ]]; then
  warn "These keys did not make it across:"
  printf '%s\n' "$missing" | sed 's/^/         /'
  die "Verification failed. Nothing has been changed in the source bucket; re-run."
fi
ok "every source key is present in the destination"

cat <<EOF

Next:

  1. Re-point each root at the new bucket (backend.tf already names it):

       for e in shared staging prod ci-trust account-access; do
         terraform -chdir=infra/envs/\$e init -reconfigure
       done

     Answer "no" if Terraform offers to copy state — it is already copied.
     infra/envs/dev is per-machine: scripts/dev-aws-setup.sh re-inits it.

  2. ./scripts/apply-ci-trust.sh
     Renames the deploy role and repoints every ARN pattern, including the two
     explicit denies. CI cannot do this.

  3. Update the GitHub secrets to the NEW role ARNs:

       arn:aws:iam::$account:role/insolvia-shared-deploy-role   -> AWS_ROLE_ARN (repo)
       arn:aws:iam::$account:role/insolvia-staging-seed-role    -> AWS_SEED_ROLE_ARN
                                                                   (insolvia-staging env)

     Until both are updated every deploy fails at configure-aws-credentials.

The old bucket is untouched and still holds every version. Delete it only after
a full staging AND prod apply has succeeded from the new one:

  aws s3 rb s3://$OLD_BUCKET --force

EOF
ok "state bucket migrated"
