#!/usr/bin/env bash
#
# Apply infra/envs/ci-trust — the GitHub OIDC provider + insolvia-shared-deploy-role
# deploy role + its permissions policy.
#
# This root is HUMAN-APPLIED ONLY, on purpose. The deploy role's policy denies
# the role permission to edit its own permissions (DenySelfPrivilegeEscalation),
# so CI — which runs as that role — cannot apply a change here; it fails with
# AccessDenied. A privilege change to the pipeline must be applied by a human
# admin with their own credentials, so merged code alone can never escalate CI.
# See docs/runbooks/aws-bootstrap.md § "The ci-trust anchor".
#
# When you need this: you edited the deploy role's permissions in
# infra/envs/ci-trust/main.tf (e.g. added an IAM action the pipeline needs),
# merged it, and now a staging/prod deploy is failing with AccessDenied on the
# new action. Run this, then re-run the deploy.
#
# What it does: credential hygiene → init → plan → show you the plan → apply
# after you confirm. It does NOT auto-approve; the trust anchor is the last
# thing that should apply unseen.
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TF_DIR="$REPO_ROOT/infra/envs/ci-trust"

log()  { printf '\033[1;34m[ci-trust]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[error]\033[0m %s\n' "$*" >&2; exit 1; }

command -v aws >/dev/null || die "aws CLI not installed."
command -v terraform >/dev/null || die "terraform not installed."

# ── Credentials ────────────────────────────────────────────────────
# Terraform's SDK can't read the `aws login` session, so resolve it into env
# vars. Same self-healing as scripts/bootstrap-ecr-images.sh: catch a dead
# session before the opaque "refreshed, but still expired", and clear stale
# AWS_* vars that would shadow a fresh login (env-var creds win the chain).
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

# A human admin should be applying this, not the deploy role. If someone runs
# it with the deploy role's own credentials, the apply would hit the self-deny
# anyway — but catch it early with a clear message.
case "$caller" in
  # Both spellings: `insolvia-github-actions` is the pre-rename name, and this
  # root is the apply that renames it — so during that one apply the caller
  # could legitimately be either.
  *:assumed-role/insolvia-shared-deploy-role/*|*:assumed-role/insolvia-github-actions/*)
    die "You are the deploy role itself — it cannot change its own permissions (that's the whole point). Run this as a human admin (aws login)." ;;
esac

# ── Plan ───────────────────────────────────────────────────────────
log "init + plan  ($TF_DIR)"
terraform -chdir="$TF_DIR" init -input=false >/dev/null
terraform -chdir="$TF_DIR" plan -input=false -out=/tmp/ci-trust.tfplan

echo
warn "Review the plan above. Expected: a change to aws_iam_role_policy.github_permissions"
warn "(the deploy role's inline policy), and NOTHING that destroys or replaces the role"
warn "or the OIDC provider. If you see a destroy/replace of either, STOP — that would"
warn "break CI's ability to authenticate. Ctrl-C now if anything looks off."
echo
read -r -p "Apply this plan? [y/N] " reply
[[ "$reply" == [yY] ]] || die "aborted — nothing applied"

# ── Apply ──────────────────────────────────────────────────────────
terraform -chdir="$TF_DIR" apply -input=false /tmp/ci-trust.tfplan
rm -f /tmp/ci-trust.tfplan
ok "ci-trust applied. If a deploy was blocked on a new permission, re-run it now."
