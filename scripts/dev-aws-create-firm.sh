#!/usr/bin/env bash
#
# Put THIS MACHINE's dev sign-in account into a firm, creating that firm.
#
#   ./scripts/dev-aws-create-firm.sh
#   ./scripts/dev-aws-create-firm.sh --check     # report, change nothing
#
# ## Why this script has to exist
#
# scripts/dev-aws-create-user.sh gets you an account that can SIGN IN. It does
# not get you an account that can DO anything, and the gap between those two is
# not obvious from the outside: you reach the app, the header renders, and
# every request answers 403 with "accessor unresolved / no_active_firm_user" in
# the API log.
#
# That is the tenancy model working as designed (ADR 0009). A case belongs to a
# firm, so every route behind `current_accessor()` needs the caller to resolve
# to an active user of an active firm, and a signed-in stranger resolves to
# neither. `/v1/me` is the one route that reports the absence instead of
# refusing it — which is why the app renders at all.
#
# The first firm cannot be made through the API. `POST /v1/firm/users` sits
# behind FIRM_ADMINISTRATION, so it needs an admin to make an admin; the pools
# set `allow_admin_create_user_only`, so nobody can sign themselves up into
# one; and ADR 0009 refuses with a 409 any edit that would leave a firm without
# an active administrator. Each of those is right on its own and together they
# close the loop. Something outside the API has to open it once. This is it.
#
# ## What it writes
#
# One firm, and one person in it as an ADMIN with `access_all_cases`. Both rows
# are constructed by services/api's own core/firms.py — see
# entrypoints/seed_dev_firm.py for why it composes those functions instead of
# writing DynamoDB items directly.
#
# ## Scope
#
# DEV ONLY, and not parameterised — the same reasoning dev-aws-create-user.sh
# records. The table name is read from THIS machine's Terraform state and must
# carry this machine's short id; seed_dev_firm.py re-checks the name shape
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

CHECK_ONLY=0
while [[ $# -gt 0 ]]; do
  case "$1" in
  --check) CHECK_ONLY=1 ;;
  -h | --help)
    printf 'Usage: %s [--check]\n\n' "$0"
    printf '  DEV_FIRM_NAME          optional — defaults to Dev Firm\n'
    printf '  DEV_USER_EMAIL         optional — defaults to dev@insolvia.test\n'
    printf '  DEV_USER_DISPLAY_NAME  optional — defaults to Dev User\n'
    printf '  DEV_USER_ROLE          optional — attorney (default), paralegal, staff\n\n'
    printf 'Creates a firm in this machine'"'"'s dev tables and makes that account\n'
    printf 'its admin. Without it every API route answers 403 after sign-in.\n'
    exit 0
    ;;
  *) die "Unknown option: $1" ;;
  esac
  shift
done

for command in aws jq terraform; do require_command "$command"; done

# The seeding runs through the API's own code, so it needs the API's venv —
# the one services/api/scripts/dev-setup.sh creates. Checked here rather than
# left to a ModuleNotFoundError, which names `boto3` and not the missing step.
VENV_PYTHON="$API_DIR/.venv/bin/python"
[[ -x "$VENV_PYTHON" ]] ||
  die "No API venv at $VENV_PYTHON. Run ./services/api/scripts/dev-setup.sh first."

load_machine_id false
load_aws_identity
terraform_init
outputs="$(terraform_output_json)"

FIRM_TABLE="$(jq -r '.firm_table_name.value // empty' <<<"$outputs")"
[[ -n "$FIRM_TABLE" ]] ||
  die "No firm_table_name in this machine's Terraform state. Run ./scripts/dev-aws-setup.sh first."
# Belt and braces on "dev only", exactly as dev-aws-create-user.sh does for the
# pool: the table this resolves to must carry THIS machine's short id.
[[ "$FIRM_TABLE" == "$FIRM_TABLE_NAME_EXPECTED" ]] ||
  die "Refusing: firm table '$FIRM_TABLE' is not '$FIRM_TABLE_NAME_EXPECTED'."

POOL_ID="$(jq -r '.auth_user_pool_id.value // empty' <<<"$outputs")"
[[ -n "$POOL_ID" ]] ||
  die "No auth_user_pool_id in this machine's Terraform state. Run ./scripts/dev-aws-setup.sh first."
pool_name="$(aws_dev cognito-idp describe-user-pool --user-pool-id "$POOL_ID" \
  --query 'UserPool.Name' --output text)"
[[ "$pool_name" == "$USER_POOL_NAME_EXPECTED" ]] ||
  die "Refusing: pool '$POOL_ID' is named '$pool_name', not '$USER_POOL_NAME_EXPECTED'."

# Defaults match dev-aws-create-user.sh's, so the common case is two scripts
# with no arguments and no environment between them.
EMAIL="${DEV_USER_EMAIL:-dev@insolvia.test}"
FIRM_NAME="${DEV_FIRM_NAME:-Dev Firm}"
DISPLAY_NAME="${DEV_USER_DISPLAY_NAME:-Dev User}"
ROLE="${DEV_USER_ROLE:-attorney}"

# The Cognito `sub`, which is half the key of the row being written. Read from
# the pool rather than accepted as input: a subject somebody can type is a
# subject that will eventually be typed wrong, and the row it produces resolves
# for nobody while looking perfectly valid.
user_json="$(aws_dev cognito-idp admin-get-user \
  --user-pool-id "$POOL_ID" --username "$EMAIL" --output json 2>/dev/null)" ||
  die "No user '$EMAIL' in $pool_name. Run ./scripts/dev-aws-create-user.sh first."

SUBJECT="$(jq -r '.UserAttributes[] | select(.Name == "sub") | .Value' <<<"$user_json")"
[[ "$SUBJECT" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] ||
  die "Cognito returned '$SUBJECT' as the sub for '$EMAIL', which is not a uuid."

# PYTHONPATH rather than an install: the venv has the runtime dependencies but
# the service itself is not packaged into it (pyproject declares no
# dependencies and nothing runs `pip install -e .`), so `src` has to be on the
# path the same way pytest's `pythonpath` setting puts it there.
run_seed() {
  PYTHONPATH="$API_DIR/src" "$VENV_PYTHON" -m insolvia_api.entrypoints.seed_dev_firm \
    --table "$FIRM_TABLE" \
    --subject "$SUBJECT" \
    "$@"
}

if [[ "$CHECK_ONLY" -eq 1 ]]; then
  run_seed --check ||
    die "'$EMAIL' is in no firm. Re-run without --check to create one."
  exit 0
fi

log "Seeding a firm in $FIRM_TABLE for $EMAIL"
run_seed \
  --firm-name "$FIRM_NAME" \
  --email "$EMAIL" \
  --display-name "$DISPLAY_NAME" \
  --role "$ROLE"

ok "Done. Sign in at http://localhost:3000 — the 403s should be gone."
