#!/usr/bin/env bash
#
# Seed THIS MACHINE's dev data stores with rows the API cannot create itself.
#
#   ./scripts/dev-aws-seed.sh firm
#   ./scripts/dev-aws-seed.sh firm --check     # report, change nothing
#
# ## Why this exists at all
#
# scripts/dev-aws-create-user.sh gets you an account that can SIGN IN. It does
# not get you an account that can DO anything, and the gap between those two is
# not obvious from the outside: you reach the app, the header renders, and every
# request answers 403 with "accessor unresolved / no_active_firm_user" in the
# API log.
#
# That is the tenancy model working as designed (ADR 0009), and the first firm
# cannot be made through the API — POST /v1/firm/users is itself behind
# FIRM_ADMINISTRATION, so it needs an admin to make an admin. Something outside
# the API has to open the loop once. See the seed module's docstring for the
# rest, including why it composes core/ instead of writing DynamoDB items.
#
# ## Why this is shell and the seeding is Python
#
# Not style — the split follows one question: does the step need to know the
# shape of our data?
#
# It does not, here. This file resolves an ENVIRONMENT: the machine id, the
# table names in this machine's Terraform state, the Cognito sub behind an email
# address, and — the load-bearing one — AWS credentials an SDK can actually
# read. The `aws login` session format is unreadable by boto3 and by Terraform's
# provider, which is what `export_temporary_aws_credentials` in
# dev-aws-common.sh exists to fix, and a child process cannot fix its own
# parent's environment. Running the seed module directly without this wrapper
# fails with NoCredentialsError.
#
# The seeding itself does need the data's shape, so it is Python next to the
# code that owns that shape (services/api/src/insolvia_api/entrypoints/seed.py).
# ONE wrapper and ONE CLI with subcommands, rather than a shell script per
# entity: everything below is per-environment and identical for every entity,
# so a second copy of it would be a second place for the guards to rot.
#
# ## Scope
#
# DEV ONLY, and not parameterised — the same reasoning dev-aws-create-user.sh
# records. Every table name is read from THIS machine's Terraform state and must
# carry this machine's short id; the seed module re-checks each name's shape
# itself, so neither guard is load-bearing alone.
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

# Keep in step with the seed module's subparsers.
SEEDABLE="firm"

usage() {
  printf 'Usage: %s <what> [--check]\n\n' "$0"
  printf '  what:\n'
  printf '    firm    a firm, with this machine'"'"'s dev account as its admin\n\n'
  printf '  DEV_FIRM_NAME          optional — defaults to Dev Firm\n'
  printf '  DEV_USER_EMAIL         optional — defaults to dev@insolvia.test\n'
  printf '  DEV_USER_DISPLAY_NAME  optional — defaults to Dev User\n'
  printf '  DEV_USER_ROLE          optional — attorney (default), paralegal, staff\n\n'
  printf 'Without a firm, every API route answers 403 after sign-in.\n'
}

WHAT=""
CHECK_ONLY=0
while [[ $# -gt 0 ]]; do
  case "$1" in
  --check) CHECK_ONLY=1 ;;
  -h | --help)
    usage
    exit 0
    ;;
  -*) die "Unknown option: $1" ;;
  *)
    [[ -z "$WHAT" ]] || die "Only one <what> at a time (got '$WHAT' and '$1')."
    WHAT="$1"
    ;;
  esac
  shift
done

[[ -n "$WHAT" ]] || {
  usage >&2
  die "Nothing named to seed."
}
# shellcheck disable=SC2076
[[ " $SEEDABLE " == *" $WHAT "* ]] ||
  die "Cannot seed '$WHAT'. Known: $SEEDABLE."

for command in aws jq terraform; do require_command "$command"; done

# The seeding runs through the API's own code, so it needs the API's venv — the
# one services/api/scripts/dev-setup.sh creates. Checked here rather than left
# to a ModuleNotFoundError, which names `boto3` and not the missing step.
VENV_PYTHON="$API_DIR/.venv/bin/python"
[[ -x "$VENV_PYTHON" ]] ||
  die "No API venv at $VENV_PYTHON. Run ./services/api/scripts/dev-setup.sh first."

load_machine_id false
load_aws_identity
terraform_init
outputs="$(terraform_output_json)"

# One table name, asserted against what infra/envs/dev provisions for THIS
# machine. Belt and braces on "dev only", exactly as dev-aws-reset.sh does
# before it deletes anything.
tf_table() {
  local output="$1" expected="$2" value
  value="$(jq -r ".${output}.value // empty" <<<"$outputs")"
  [[ -n "$value" ]] ||
    die "No $output in this machine's Terraform state. Run ./scripts/dev-aws-setup.sh first."
  [[ "$value" == "$expected" ]] ||
    die "Refusing: '$value' is not '$expected'."
  printf '%s' "$value"
}

# PYTHONPATH rather than an install: the venv has the runtime dependencies but
# the service itself is not packaged into it (pyproject declares no dependencies
# and nothing runs `pip install -e .`), so `src` has to be on the path the same
# way pytest's `pythonpath` setting puts it there.
seed() {
  PYTHONPATH="$API_DIR/src" "$VENV_PYTHON" -m insolvia_api.entrypoints.seed "$@"
}

# The Cognito `sub` for an email, which is half the key of any row keyed by a
# person. Read from the pool rather than accepted as input: a subject somebody
# can type is a subject that will eventually be typed wrong, and the row it
# produces resolves for nobody while looking perfectly valid.
pool_subject() {
  local email="$1" pool_id pool_name user_json subject
  pool_id="$(jq -r '.auth_user_pool_id.value // empty' <<<"$outputs")"
  [[ -n "$pool_id" ]] ||
    die "No auth_user_pool_id in this machine's Terraform state. Run ./scripts/dev-aws-setup.sh first."
  pool_name="$(aws_dev cognito-idp describe-user-pool --user-pool-id "$pool_id" \
    --query 'UserPool.Name' --output text)"
  [[ "$pool_name" == "$USER_POOL_NAME_EXPECTED" ]] ||
    die "Refusing: pool '$pool_id' is named '$pool_name', not '$USER_POOL_NAME_EXPECTED'."
  user_json="$(aws_dev cognito-idp admin-get-user \
    --user-pool-id "$pool_id" --username "$email" --output json 2>/dev/null)" ||
    die "No user '$email' in $pool_name. Run ./scripts/dev-aws-create-user.sh first."
  subject="$(jq -r '.UserAttributes[] | select(.Name == "sub") | .Value' <<<"$user_json")"
  [[ "$subject" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] ||
    die "Cognito returned '$subject' as the sub for '$email', which is not a uuid."
  printf '%s' "$subject"
}

case "$WHAT" in
firm)
  # Defaults match dev-aws-create-user.sh's, so the common case is two scripts
  # with no arguments and no environment between them.
  EMAIL="${DEV_USER_EMAIL:-dev@insolvia.test}"
  firm_table="$(tf_table firm_table_name "$FIRM_TABLE_NAME_EXPECTED")"
  subject="$(pool_subject "$EMAIL")"

  if [[ "$CHECK_ONLY" -eq 1 ]]; then
    seed firm --firm-table "$firm_table" --subject "$subject" --check ||
      die "'$EMAIL' is in no firm. Re-run without --check to create one."
    exit 0
  fi

  log "Seeding a firm in $firm_table for $EMAIL"
  seed firm \
    --firm-table "$firm_table" \
    --subject "$subject" \
    --firm-name "${DEV_FIRM_NAME:-Dev Firm}" \
    --email "$EMAIL" \
    --display-name "${DEV_USER_DISPLAY_NAME:-Dev User}" \
    --role "${DEV_USER_ROLE:-attorney}"
  ok "Done. Sign in at http://localhost:3000 — the 403s should be gone."
  ;;
esac
