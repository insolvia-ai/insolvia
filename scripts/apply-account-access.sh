#!/usr/bin/env bash
#
# Apply infra/envs/account-access — the account's human IAM users, the groups
# they belong to, and the policies attached to them.
#
# This root is HUMAN-APPLIED ONLY, and unlike ci-trust that is not a choice
# anyone made here: the CI deploy role holds no iam:*User* or iam:*Group*
# action at all, so a CI apply fails with AccessDenied. That absence is the
# control — a pipeline that can create an IAM user with AdministratorAccess can
# mint itself an admin. See infra/envs/account-access/main.tf for the full
# reasoning, and docs/reference/terraform.md § "Human account access".
#
# When you need this: someone joins, leaves, or changes group; or you attached
# a policy to a user by hand and want the code to be true again.
#
# When you do NOT need this: rotating your own MFA device. That is a console
# procedure and no Terraform resource is involved on purpose (the TOTP seed
# would land in state) — see docs/runbooks/iam-mfa-rotation.md.
#
# What it does: credential hygiene -> init -> plan -> show you the plan -> apply
# after you confirm. It does NOT auto-approve; this root can remove your own
# console access.
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TF_DIR="$REPO_ROOT/infra/envs/account-access"
PLAN_FILE="$(mktemp -t account-access.tfplan.XXXXXX)"
trap 'rm -f "$PLAN_FILE"' EXIT

log()  { printf '\033[1;34m[account-access]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[error]\033[0m %s\n' "$*" >&2; exit 1; }

command -v aws >/dev/null || die "aws CLI not installed."
command -v terraform >/dev/null || die "terraform not installed."

# ── Credentials ────────────────────────────────────────────────────
# Terraform's SDK can't read the `aws login` session, so resolve it into env
# vars. Same self-healing as scripts/apply-ci-trust.sh: catch a dead session
# before the opaque "refreshed, but still expired", and clear stale AWS_* vars
# that would shadow a fresh login (env-var creds win the provider chain).
log "Checking AWS session"
if ! aws sts get-caller-identity >/dev/null 2>&1; then
  if [[ -n "${AWS_ACCESS_KEY_ID:-}" ]]; then
    warn "Ignoring stale AWS_* environment credentials; using the profile instead."
    warn "Clear them in your shell too: unset AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY AWS_SESSION_TOKEN AWS_CREDENTIAL_EXPIRATION"
    unset AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY AWS_SESSION_TOKEN AWS_CREDENTIAL_EXPIRATION
  fi
  aws sts get-caller-identity >/dev/null 2>&1 || die "AWS session is expired or absent. Run 'aws login' and try again."
fi
eval "$(aws configure export-credentials --format env)"
caller="$(aws sts get-caller-identity --query Arn --output text)"
ok "Authenticated as $caller"

# The deploy role cannot apply this root — it holds no IAM user/group actions.
# It would fail on the first API call anyway; say so up front instead.
case "$caller" in
  # Both spellings: `insolvia-github-actions` is the pre-rename name, and this
  # root is the apply that renames it — so during that one apply the caller
  # could legitimately be either.
  *:assumed-role/insolvia-shared-deploy-role/*|*:assumed-role/insolvia-github-actions/*)
    die "You are the deploy role — it holds no iam:*User*/*Group* permissions, by design. Run this as a human admin (aws login)." ;;
esac

# ── Plan ───────────────────────────────────────────────────────────
log "init + plan  ($TF_DIR)"
terraform -chdir="$TF_DIR" init -input=false >/dev/null
terraform -chdir="$TF_DIR" plan -input=false -out="$PLAN_FILE"

echo
warn "Review the plan above before answering."
warn "  * On the FIRST apply, expect exactly '5 to import, 0 to add, 1 to change,"
warn "    0 to destroy' — the group, the Admin policy attachment, the user, its"
warn "    membership and its attached policy all already exist. The one change is"
warn "    the user's tags: Project/Environment/ManagedBy added, and the hand-set"
warn "    access-key provenance tag removed (see variables.tf — that removal is"
warn "    intended). A plan proposing to CREATE any of those resources means an"
warn "    import block is missing or wrong; STOP, because the apply will then fail"
warn "    on EntityAlreadyExists."
warn "  * On LATER applies a tag REMOVAL is a warning sign, not routine: aws_iam_user"
warn "    manages tags exclusively, so it means someone tagged the user by hand and"
warn "    that tag needs adding to var.human_users[*].extra_tags to survive."
warn "  * A DESTROY of aws_iam_user.human is blocked by prevent_destroy and will"
warn "    fail the plan outright. If you meant to offboard someone, that is the"
warn "    deliberate two-step in main.tf — not something to force through here."
warn "  * A change to aws_iam_user_group_membership removes group memberships not"
warn "    listed in var.human_users. Check you are not dropping your own Admin."
echo
read -r -p "Apply this plan? [y/N] " reply
[[ "$reply" == [yY] ]] || die "aborted — nothing applied"

# ── Apply ──────────────────────────────────────────────────────────
terraform -chdir="$TF_DIR" apply -input=false "$PLAN_FILE"
ok "account-access applied."
log "Verify you did not lock yourself out before closing this shell:"
log "  aws iam list-groups-for-user --user-name \"\${USER_NAME:-<your-iam-user>}\""
