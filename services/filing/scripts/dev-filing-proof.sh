#!/usr/bin/env bash
#
# Prove the filing worker (ADR 0024 PR 7) on THIS MACHINE's real dev AWS —
# the real case table, bucket and case key, the real vault and its key
# policy, the real filing queue — against the fake CM/ECF, with the worker
# running AS ITS OWN ROLE (insolvia-dev-<id>-filing-role, which infra/envs/dev
# alone trusts the developer to assume). Fake values only:
#
#   ./services/filing/scripts/dev-filing-proof.sh
#
# The API's half (writing a ready case, assembling its packet, signing the
# authorization, enrolling the fake court's login in the vault, approving)
# runs as the developer, standing in for the API's role exactly as
# services/api/scripts/dev-filing-approval-proof.sh does. The worker's half
# runs under the role's credentials only. Each scenario is a fresh copy of the
# API unit tier's reference case under the fake firm `proof-firm-00000000`,
# because a consumed approval is never re-approvable (PR 6).
#
#   1. FILES END TO END: approve -> one ids-only message on the real queue ->
#      the worker (as its role) receives it, consumes the approval, opens the
#      fake login through the vault key, signs in with a real TOTP, uploads
#      (Debtor.txt first, built with each debtor's sealed tax id opened
#      through the role's TaxIdOpen grant — ADR 0024 PR 9),
#      re-checks, marks, submits ONCE, and stores the receipt with the case.
#   2. A REDELIVERED JOB (the same body sent again) is a no-op: still one
#      submission.
#   3. A CRASH AFTER THE FINAL SUBMIT, then SQS redelivery: outcome_unknown,
#      never retried — still one submission.
#   4. EVERY FAULT MODE ends handed_back or outcome_unknown, with a reason.
#   5. THE ROLE IS NARROW: it cannot read an uploaded source document, delete
#      a row, seal a tax id, or decrypt the case key under any context but
#      the tax-id envelope's.
#
# WHAT IS LEFT BEHIND: one case partition per scenario under the fake firm,
# its packet and receipt in the dev bucket — the PR 6 proof's cost, cleared by
# scripts/dev-aws-reset.sh. Every fake login is destroyed at the end by
# withdrawing the authorization. Nothing real is used; nothing secret is
# printed.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FILING_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
API_DIR="$REPO_ROOT/services/api"
PROOF="$FILING_DIR/scripts/dev_filing_proof.py"

# shellcheck source=/dev/null
source "$REPO_ROOT/scripts/dev-aws-common.sh"

env_file="$FILING_DIR/.env"
[[ -f "$env_file" ]] || die "services/filing/.env is missing — run ./scripts/dev-aws-setup.sh."
read_env() { sed -n "s/^$1=//p" "$env_file" | tail -n 1; }
profile_from_env="$(read_env AWS_PROFILE)"
[[ -n "$profile_from_env" ]] && AWS_PROFILE_VALUE="$profile_from_env"
CASE_TABLE_NAME="$(read_env CASE_TABLE_NAME)"
CASE_ACCESS_LOG_TABLE_NAME="$(read_env CASE_ACCESS_LOG_TABLE_NAME)"
CASE_DOCUMENT_BUCKET="$(read_env CASE_DOCUMENT_BUCKET)"
FILING_QUEUE_URL="$(read_env FILING_QUEUE_URL)"
FILING_WORKER_ROLE_ARN="$(read_env FILING_WORKER_ROLE_ARN)"
[[ "$CASE_TABLE_NAME" == insolvia-dev-*-cases ]] ||
  die "CASE_TABLE_NAME is not a dev case table — this proof runs against dev only."
[[ "$FILING_QUEUE_URL" == */insolvia-dev-*-filing ]] ||
  die "FILING_QUEUE_URL is not a dev filing queue."
[[ "$FILING_WORKER_ROLE_ARN" == *:role/insolvia-dev-*-filing-role ]] ||
  die "FILING_WORKER_ROLE_ARN is not a dev filing worker role."
[[ -x "$FILING_DIR/.venv/bin/python" ]] || die "services/filing/.venv is missing — run ./services/filing/scripts/dev-setup.sh."

export_temporary_aws_credentials
export AWS_DEFAULT_REGION="$AWS_REGION_VALUE"
unset AWS_PROFILE
export CASE_TABLE_NAME CASE_ACCESS_LOG_TABLE_NAME CASE_DOCUMENT_BUCKET FILING_QUEUE_URL FILING_WORKER_ROLE_ARN
export INSOLVIA_ENV=local FILING_SUBMISSIONS_ENABLED=true
export PYTHONPATH="$FILING_DIR/src:$API_DIR/src:$FILING_DIR/fake:$API_DIR"

cd "$API_DIR"
exec "$FILING_DIR/.venv/bin/python" "$PROOF"
