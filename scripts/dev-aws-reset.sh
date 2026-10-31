#!/usr/bin/env bash
#
# Reset this machine's Insolvia development DATA: wipe the local database
# (backend/docker-compose.yml's Postgres volume, `insolvia-backend-pgdata`)
# and, with --documents, empty this machine's case-document bucket. The
# resources themselves survive; only data goes. The next `docker compose up`
# (scripts/dev-up.sh) migrates an empty database, and scripts/dev-aws-seed.sh
# seeds it again.
#
# The name predates ADR 0028: the data used to be per-machine DynamoDB tables,
# which this script deleted and recreated. The database is local now, so
# most of a reset needs no AWS at all; only --documents does.
#
# COGNITO USERS ARE NOT DELETED. Sign-in uses the ONE development pool every
# machine shares (insolvia-services', ADR 0026); its accounts are not this
# machine's to delete. --skip-cognito is accepted and changes nothing.
#
# AFTERWARDS YOUR ACCOUNT STILL EXISTS BUT HAS NO FIRM. Re-run
# scripts/dev-aws-seed.sh — it reseeds the firm (and converges the account).
#
#   ./scripts/dev-aws-reset.sh                 # the local database
#   ./scripts/dev-aws-reset.sh --documents     # and this machine's bucket
#   ./scripts/dev-aws-reset.sh --dry-run       # say what would go
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=dev-aws-common.sh
source "$SCRIPT_DIR/dev-aws-common.sh"

AUTO_APPROVE=0
DRY_RUN=0
DOCUMENTS=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --profile) [[ $# -ge 2 ]] || die "--profile requires a value."; AWS_PROFILE_VALUE="$2"; shift ;;
    --region) [[ $# -ge 2 ]] || die "--region requires a value."; AWS_REGION_VALUE="$2"; shift ;;
    --yes|-y) AUTO_APPROVE=1 ;;
    --dry-run) DRY_RUN=1 ;;
    --documents) DOCUMENTS=1 ;;
    --skip-cognito) : ;; # accepted for old habits; users are never deleted now
    --help|-h)
      printf 'Usage: %s [--documents] [--profile NAME] [--region REGION] [--yes] [--dry-run]\n' "$0"
      exit 0
      ;;
    *) die "Unknown option: $1" ;;
  esac
  shift
done

COMPOSE_FILE="$REPO_ROOT/backend/docker-compose.yml"
VOLUME="insolvia-backend-pgdata"
require_command docker

bucket=""
if [[ "$DOCUMENTS" -eq 1 ]]; then
  for command in aws jq terraform; do require_command "$command"; done
  load_machine_id false
  load_aws_identity
  terraform_init
  outputs="$(terraform_output_json)"
  output_machine_id="$(jq -r '.machine_id.value' <<<"$outputs")"
  [[ "$output_machine_id" == "$MACHINE_ID" ]] ||
    die "Terraform state belongs to machine '$output_machine_id', not '$MACHINE_ID'."
  # Paranoid ownership check: the bucket must carry THIS machine's prefix.
  bucket="$(jq -r '.case_document_bucket.value // empty' <<<"$outputs")"
  [[ "$bucket" == "$RESOURCE_PREFIX-case-documents-"* ]] ||
    die "Refusing reset: document bucket '$bucket' does not carry this machine's prefix ($RESOURCE_PREFIX)."
fi

printf '\nThis will clear development data on this machine:\n'
printf '  Database:   docker volume %s (backend/docker-compose.yml)\n' "$VOLUME"
[[ -n "$bucket" ]] && printf '  Documents:  every object in s3://%s\n' "$bucket"
printf '  Cognito:    the shared pool — its users are NOT deleted\n\n'

if [[ "$DRY_RUN" -eq 1 ]]; then
  ok "Dry run complete; nothing was changed."
  exit 0
fi
if [[ "$AUTO_APPROVE" -eq 0 ]]; then
  [[ -t 0 ]] || die "Non-interactive reset requires --yes."
  read -r -p "Type RESET to continue: " confirmation
  [[ "$confirmation" == "RESET" ]] || die "Reset canceled."
fi

# `down -v` stops the stack and removes its named volume — the database.
log "Stopping the local backend and removing its database volume..."
docker compose -f "$COMPOSE_FILE" down -v --remove-orphans

if [[ -n "$bucket" ]]; then
  log "Emptying s3://$bucket..."
  aws_dev s3 rm "s3://$bucket" --recursive >/dev/null
fi

ok "Reset. Next: ./scripts/dev-up.sh (migrates the empty database), then ./scripts/dev-aws-seed.sh."
