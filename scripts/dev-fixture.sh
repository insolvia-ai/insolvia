#!/usr/bin/env bash
#
# Move a seed fixture between git and the shared fixture bucket, or capture a
# new one out of THIS MACHINE's dev stack.
#
#   ./scripts/dev-fixture.sh publish v1                 # git -> the bucket (idempotent)
#   ./scripts/dev-fixture.sh publish v1 --check         # what would upload; write nothing
#   ./scripts/dev-fixture.sh capture v2 <case-id> <handle> [--replace]
#                                                       # this dev stack -> seeds/fixtures/v2/
#
# ## The three directions, and who runs which
#
#   load      git + bucket -> an environment    scripts/dev-aws-seed.sh (dev),
#                                               .github/actions/seed-staging (staging)
#   publish   git -> bucket                     a developer, after a fixture PR merges
#   capture   a DEV stack -> git                a developer curating a new version
#
# `publish` and `capture` are here and NOT in CI, deliberately. Curating a
# fixture is a human act — choosing which case, checking every value is
# synthetic — and the bucket is written by a developer's own credentials so
# that CI's seed role stays read-only on it (infra/envs/ci-trust, the
# DevFixtureObjectsRead statement). Publishing is idempotent by digest, so
# running it twice costs nothing and running it after a version's PR merged
# is the whole procedure.
#
# ## Guards the loader enforces (this wrapper adds none)
#
# `capture` refuses any source table that is not a dev table — what a
# developer typed into their own stack is synthetic by construction, and
# nothing else is. `publish` refuses a bucket outside the
# insolvia-shared-dev-fixtures-* family and refuses to upload bytes that
# disagree with manifest.json. Every fixture value must describe nobody;
# review a seeds/fixtures/ diff with that question in mind.
#
# ## AWS credentials
#
# The developer's own session, exported the way every dev-aws-* script exports
# it (dev-aws-common.sh). The fixture bucket is account-level; the case table
# and document bucket for `capture` are this machine's, read from its
# Terraform state.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=/dev/null
source "$SCRIPT_DIR/dev-aws-common.sh"

usage() {
  sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

[[ $# -ge 2 ]] || usage 1
COMMAND="$1"; VERSION="$2"; shift 2
[[ "$VERSION" =~ ^v[0-9]+$ ]] || die "A fixture version looks like v1, not '$VERSION'."
FOLDER="$REPO_ROOT/seeds/fixtures/$VERSION"

for command in aws jq terraform; do require_command "$command"; done
VENV_PYTHON="$API_DIR/.venv/bin/python"
[[ -x "$VENV_PYTHON" ]] || die "No API venv at $VENV_PYTHON. Run ./services/api/scripts/dev-setup.sh first."

seed() {
  PYTHONPATH="$REPO_ROOT/services/admin/src" "$VENV_PYTHON" -m insolvia_admin.entrypoints.seed "$@"
}

load_machine_id false
load_aws_identity
export_temporary_aws_credentials
FIXTURE_BUCKET="${INSOLVIA_DEV_FIXTURES_BUCKET:-insolvia-shared-dev-fixtures-$AWS_REGION_VALUE}"

case "$COMMAND" in
  publish)
    [[ -d "$FOLDER" ]] || die "No fixture at $FOLDER."
    log "Publishing $VERSION to s3://$FIXTURE_BUCKET/$VERSION/"
    seed publish --version "$FOLDER" --fixture-bucket "$FIXTURE_BUCKET" "$@"
    ;;
  capture)
    [[ $# -ge 2 ]] || usage 1
    CASE_ID="$1"; HANDLE="$2"; shift 2
    terraform_init
    outputs="$(terraform_output_json)"
    CASE_TABLE="$(jq -r '.case_table_name.value // empty' <<<"$outputs")"
    DOCUMENT_BUCKET="$(jq -r '.case_document_bucket.value // empty' <<<"$outputs")"
    [[ "$CASE_TABLE" == "$CASE_TABLE_NAME_EXPECTED" ]] ||
      die "Refusing: case table '$CASE_TABLE' is not '$CASE_TABLE_NAME_EXPECTED'."
    log "Capturing case $CASE_ID from $CASE_TABLE as '$HANDLE' into $FOLDER"
    seed capture --version "$FOLDER" --case "$CASE_ID" --handle "$HANDLE" \
      --case-table "$CASE_TABLE" --document-bucket "$DOCUMENT_BUCKET" "$@"
    ok "Review the diff under seeds/fixtures/$VERSION, open a PR, then publish it once merged."
    ;;
  *) usage 1 ;;
esac
