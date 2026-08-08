#!/usr/bin/env bash
#
# Load seeds/dev.json into THIS MACHINE's dev data stores.
#
#   ./scripts/dev-aws-seed.sh
#   ./scripts/dev-aws-seed.sh --check     # report what is missing, write nothing
#
# ## Why this exists
#
# scripts/dev-aws-create-user.sh gets you an account that can SIGN IN. It does
# not get you an account that can DO anything, and the gap is not obvious from
# outside: you reach the app, the header renders, and every request answers 403
# with "accessor unresolved / no_active_firm_user" in the API log.
#
# That is the tenancy model working as designed (ADR 0009), and the first firm
# cannot be made through the API — POST /v1/firm/users is itself behind
# FIRM_ADMINISTRATION, so it needs an admin to make an admin. See the seed
# module's docstring for the rest.
#
# ## What is actually in the seed
#
# seeds/dev.json, which is the answer to "what is on a developer's machine" and
# is reviewable as a diff. This script does not describe the data; it only says
# WHERE to put it. The same loader puts seeds/staging.json into staging from
# app-staging.yml, so the two environments differ in their fixture and their
# tables, never in the code path that built the rows.
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
# it: services/api/src/insolvia_api/entrypoints/seed.py.
#
# ## Scope
#
# DEV ONLY — the same reasoning dev-aws-create-user.sh records. The names come
# from THIS machine's Terraform state and must carry this machine's short id;
# the loader re-checks each name's shape itself, so neither guard is
# load-bearing alone. It accepts staging too, which is how app-staging.yml
# uses it, and refuses prod outright.
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

CHECK_ONLY=0
while [[ $# -gt 0 ]]; do
  case "$1" in
  --check) CHECK_ONLY=1 ;;
  -h | --help)
    printf 'Usage: %s [--check]\n\n' "$0"
    printf 'Loads seeds/dev.json into this machine'"'"'s dev tables.\n'
    printf 'Edit that file to change WHAT is seeded; this script only says where.\n\n'
    printf 'Every person named in the fixture must already exist in the pool —\n'
    printf 'run ./scripts/dev-aws-create-user.sh first.\n'
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
pool_name="$(aws_dev cognito-idp describe-user-pool --user-pool-id "$POOL_ID" \
  --query 'UserPool.Name' --output text)"
[[ "$pool_name" == "$USER_POOL_NAME_EXPECTED" ]] ||
  die "Refusing: pool '$POOL_ID' is named '$pool_name', not '$USER_POOL_NAME_EXPECTED'."

# PYTHONPATH rather than an install: the venv has the runtime dependencies but
# the service itself is not packaged into it (pyproject declares no dependencies
# and nothing runs `pip install -e .`), so `src` has to be on the path the same
# way pytest's `pythonpath` setting puts it there.
seed() {
  PYTHONPATH="$API_DIR/src" "$VENV_PYTHON" -m insolvia_api.entrypoints.seed \
    --fixture "$FIXTURE" \
    --firm-table "$FIRM_TABLE" \
    --user-pool-id "$POOL_ID" \
    "$@"
}

if [[ "$CHECK_ONLY" -eq 1 ]]; then
  seed --check || die "seeds/dev.json is not fully loaded. Re-run without --check."
  ok "This machine matches seeds/dev.json."
  exit 0
fi

log "Loading $(basename "$FIXTURE") into $FIRM_TABLE"
seed
ok "Done. Sign in at http://localhost:3000 — the 403s should be gone."
