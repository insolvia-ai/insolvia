#!/usr/bin/env bash
#
# ONE-TIME: empty the four PRE-rename container repositories, so the
# infra/envs/shared apply can destroy them.
#
# ## Why this is its own script
#
# The container repositories are shared across environments (infra/envs/shared),
# so emptying them is an ACCOUNT-WIDE act, not a per-environment one. This lived
# inside rename-teardown.sh at first, which was wrong in a way worth recording:
# running that script for `staging` would silently pull the images prod's
# Lambdas depend on.
#
# ## Order, and why this comes FIRST
#
# `aws_ecr_repository` has no `force_delete` in this config, so the shared apply
# fails on RepositoryNotEmptyException while these hold images:
#
#   Error: ECR Repository (insolvia-api) not empty, consider using force_delete
#
# That failure is mid-apply, so it leaves shared half-applied — the new
# repositories created, the old ones still there. Recovering is just running
# this and re-applying, but the way to not need to is to run this first:
#
#   1. THIS SCRIPT                            <- empties the old repositories
#   2. terraform -chdir=infra/envs/shared apply
#   3. ./scripts/bootstrap-ecr-images.sh staging
#      ./scripts/bootstrap-ecr-images.sh prod  <- the new repositories are empty
#   4. ./scripts/rename-teardown.sh <env>      <- tables, pools, buckets
#   5. merge; CI applies the environments
#
# ## THIS OPENS THE OUTAGE WINDOW
#
# Deleting these images is the point of no return for the running services. A
# Lambda already running keeps serving from its cached image, but any cold start
# or scale-out after this cannot pull one — and rollback stops being possible,
# because the images the old-named Lambdas point at are gone. From here the way
# out is forward: finish the rename.
#
# Usage:
#   ./scripts/rename-teardown-ecr.sh [--check] [--yes]

set -euo pipefail

REGION="us-east-1"
CHECK_ONLY=false
ASSUME_YES=false

# The PRE-rename names. After the shared apply these match nothing, which is the
# intended way for this script to become inert.
OLD_ECR=(insolvia-api insolvia-admin insolvia-mailer insolvia-marketing)

log()  { printf '\033[1;34m[teardown-ecr]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[fail]\033[0m %s\n' "$*" >&2; exit 1; }

for arg in "$@"; do
  case "$arg" in
    --check)   CHECK_ONLY=true ;;
    --yes|-y)  ASSUME_YES=true ;;
    -h|--help) sed -n '2,41p' "$0"; exit 0 ;;
    *)         die "unrecognized argument: $arg (see --help)" ;;
  esac
done

command -v aws >/dev/null || die "aws CLI not found."
command -v jq  >/dev/null || die "jq not found."

identity="$(aws sts get-caller-identity --output json 2>/dev/null)" \
  || die "No usable AWS credentials. See the insolvia-aws-auth skill."
log "Account : $(printf '%s' "$identity" | jq -r '.Account')"
log "Identity: $(printf '%s' "$identity" | jq -r '.Arn')"

if ! $CHECK_ONLY && ! $ASSUME_YES; then
  printf '\n'
  warn "This deletes every image in the four pre-rename repositories."
  warn "Running Lambdas keep their cached image; a cold start after this fails"
  warn "until the environment has been re-applied under the new names."
  printf 'Continue? [y/N] '
  read -r reply
  [[ "$reply" == "y" || "$reply" == "Y" ]] || die "Aborted."
fi

for repo in "${OLD_ECR[@]}"; do
  if ! aws ecr describe-repositories --repository-names "$repo" --region "$REGION" >/dev/null 2>&1; then
    log "  $repo — gone already, skipping"
    continue
  fi

  # batch-delete-image takes at most 100 ids per call, and list-images pages, so
  # this loops rather than assuming one round clears it.
  total=0
  while true; do
    ids="$(aws ecr list-images --repository-name "$repo" --region "$REGION" \
      --max-items 100 --query 'imageIds[*]' --output json)"
    n="$(printf '%s' "$ids" | jq 'length')"
    [[ "$n" -gt 0 ]] || break

    if $CHECK_ONLY; then
      log "  $repo — WOULD delete $n image(s) (first page)"
      break
    fi

    aws ecr batch-delete-image --repository-name "$repo" --region "$REGION" \
      --image-ids "$ids" >/dev/null
    total=$((total + n))
    log "  $repo — deleted $n"
  done

  if $CHECK_ONLY; then
    [[ "$n" -eq 0 ]] && log "  $repo — already empty"
  else
    if [[ "$total" -eq 0 ]]; then
      log "  $repo — already empty"
    else
      ok "  $repo — empty ($total image(s) deleted)"
    fi
  fi
done

if $CHECK_ONLY; then
  printf '\n'; log "--check: nothing was changed."
  exit 0
fi

cat <<'EOF'

Next:

  terraform -chdir=infra/envs/shared apply

Then seed the NEW repositories before any environment applies — an
Image-package Lambda cannot be created from an empty repository:

  ./scripts/bootstrap-ecr-images.sh staging
  ./scripts/bootstrap-ecr-images.sh prod

EOF
ok "pre-rename repositories emptied"
