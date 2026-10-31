#!/usr/bin/env bash
#
# Publish a seed fixture version from the repository to the shared fixture
# bucket.
#
#   ./scripts/dev-fixture.sh publish v1                 # repo -> the bucket (idempotent)
#   ./scripts/dev-fixture.sh publish v1 --check         # what would upload; write nothing
#
# ## The directions, and who runs which
#
#   load      repo + bucket -> an environment   scripts/dev-aws-seed.sh (dev),
#                                               .github/actions/seed-staging (staging)
#   publish   repo -> bucket                    a developer, after a fixture PR merges
#
# `publish` is the .NET migrator's `seed-publish` command
# (backend/src/Insolvia.Migrator/Seeding/SeedPublisher.cs, the same keys and
# digests the Python publisher wrote, so published versions stay valid) run
# with `dotnet run` on this machine.
#
# THERE IS NO `capture` ANY MORE. It was the Python seeder reading a case out
# of a developer's DynamoDB dev tables into seeds/fixtures/<version>/; it went
# with the Python (ADR 0028, plan 18.23) and was deliberately not ported. A
# new version is curated by hand: copy the latest seeds/fixtures/<version>/ to
# the next one, edit cases.json (seeds/README.md says what a case may hold),
# and list its documents in manifest.json. The migrator's seed tests load
# every committed version, so a malformed one fails `Backend (.NET)`.
#
# `publish` is here and NOT in CI, deliberately. Curating a
# fixture is a human act — choosing which case, checking every value is
# synthetic — and the bucket is written by a developer's own credentials so
# that CI's seed role stays read-only on it (insolvia-services'
# infra/envs/ci-trust, the DevFixtureObjectsRead statement). Publishing is
# idempotent by digest, so running it twice costs nothing and running it after
# a version's PR merged is the whole procedure.
#
# ## Guards the loader enforces (this wrapper adds none)
#
# `publish` refuses a bucket outside the
# insolvia-shared-dev-fixtures-* family and refuses to upload bytes that
# disagree with manifest.json. Every fixture value must describe nobody;
# review a seeds/fixtures/ diff with that question in mind.
#
# ## AWS credentials
#
# The developer's own session, exported the way every dev-aws-* script exports
# it (dev-aws-common.sh). The fixture bucket is account-level.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=/dev/null
source "$SCRIPT_DIR/dev-aws-common.sh"

usage() {
  sed -n '2,24p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

[[ $# -ge 2 ]] || usage 1
COMMAND="$1"; VERSION="$2"; shift 2
[[ "$VERSION" =~ ^v[0-9]+$ ]] || die "A fixture version looks like v1, not '$VERSION'."
FOLDER="$REPO_ROOT/seeds/fixtures/$VERSION"

for command in aws jq; do require_command "$command"; done

# The .NET migrator, from source. --version takes the folder path.
migrator() {
  require_command dotnet
  AWS_REGION="$AWS_REGION_VALUE" dotnet run --project "$REPO_ROOT/backend/src/Insolvia.Migrator" -- "$@"
}

load_machine_id false
load_aws_identity
export_temporary_aws_credentials
FIXTURE_BUCKET="${INSOLVIA_DEV_FIXTURES_BUCKET:-insolvia-shared-dev-fixtures-$AWS_REGION_VALUE}"

case "$COMMAND" in
  publish)
    [[ -d "$FOLDER" ]] || die "No fixture at $FOLDER."
    # The bucket is created by insolvia-services' CI applying its
    # infra/envs/shared (modules/dev_fixtures). Before that, boto3 would report a
    # NoSuchBucket three calls in; this says what is actually missing.
    aws_dev s3api head-bucket --bucket "$FIXTURE_BUCKET" >/dev/null 2>&1 ||
      die "s3://$FIXTURE_BUCKET does not exist (or is not readable). It is created by
       insolvia-services' shared infra apply (its infra/envs/shared, modules/dev_fixtures)
       — and that apply needs the deploy role's DevFixtureBucket grant, a human ci-trust
       apply (insolvia-services' scripts/apply-ci-trust.sh). Publish once the bucket exists."
    log "Publishing $VERSION to s3://$FIXTURE_BUCKET/$VERSION/"
    migrator seed-publish --version "$FOLDER" --fixture-bucket "$FIXTURE_BUCKET" "$@"
    ;;
  *) usage 1 ;;
esac
