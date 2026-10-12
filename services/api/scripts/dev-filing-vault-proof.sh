#!/usr/bin/env bash
#
# Prove the filing-credential vault's guardrail on THIS MACHINE's real dev
# AWS (ADR 0024 PR 4's done-when), with fake values only:
#
#   ./services/api/scripts/dev-filing-vault-proof.sh
#
#   1. ENROL SEALS — as the developer, who plays the API in dev (there is no
#      API Lambda here): GenerateDataKey under the vault key, item written to
#      insolvia-dev-<id>-filing-credentials.
#   2. THE API'S PRINCIPAL IS REFUSED DECRYPT BY THE KEY POLICY — the same
#      developer, an account admin, asks KMS to open it and gets
#      AccessDeniedException "with an explicit deny in a resource-based
#      policy": the key policy's DenyDecryptToAllButFilingWorker, not a
#      missing IAM grant.
#   3. THE WORKER'S ROLE OPENS — the script assumes insolvia-dev-<id>-
#      filing-role (its trust names the developer IN DEV ONLY; the module
#      refuses that outside dev-*) and runs insolvia_core's open path under
#      the role's own credentials: GetItem, Decrypt, the access-log append.
#   4. THE WRONG CONTEXT FAILS — the worker opening the same envelope under
#      another attorney's context is refused by KMS.
#   5. REVOKE DESTROYS, AND THE NEXT OPEN FAILS — the item is deleted; the
#      worker's next open raises CredentialUnavailableError.
#
# And guardrail 2, the written authorization (ADR 0024 PR 5's done-when):
#
#   0. NO SIGNATURE, NO ENROLMENT — before anything else, an enrolment for
#      an attorney with no signed authorization is refused, and nothing is
#      sealed or written.
#      (Then the attorney signs the current text, and steps 1-5 run as
#      above, signed.)
#   6. WITHDRAWAL REVOKES, AND THE NEXT OPEN FAILS — a second credential is
#      enrolled and opened by the worker; the attorney withdraws; the
#      credential item is gone, the current authorization item is gone (the
#      history record says `withdrawn`), and the worker's next open raises.
#   7. THE WORKER READS THE AUTHORIZATION UNDER ITS OWN GRANT — implicit in
#      3 and 6: `open_credential` GetItems the `AUTHORIZATION` item with the
#      role's credentials, which hold GetItem on the vault table and
#      nothing more.
#
# The signature's fresh `auth_time` is a stand-in here (the script signs
# in to nothing); the API's fresh-sign-in check against the real pool is
# the integration tier's (tests/integration/test_filing_authorization.py).
#
# NOTHING REAL, NOTHING PRINTED. The login is FAKE-ECF-USER, the password a
# fake literal, the TOTP seed minted from os.urandom for this run. The
# script prints outcomes ("opened; matches what was sealed"), never a value.
# The firm id is a fixed obviously-fake one, so these rows never touch a
# seeded firm.
#
# WHY THE DEVELOPER STANDS IN FOR THE API'S ROLE. Dev has no API Lambda and
# so no API role. The key policy's deny names every principal but the
# worker's role, so the developer is refused exactly as staging's
# insolvia-staging-api-role is; that the API's role specifically holds
# GenerateDataKey and not Decrypt is pinned by the module's
# `terraform test` (infra/modules/filing_credentials/tests).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
API_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

# shellcheck source=/dev/null
source "$REPO_ROOT/scripts/dev-aws-common.sh"

env_file="$API_DIR/.env"
[[ -f "$env_file" ]] || die "services/api/.env is missing — run ./services/api/scripts/dev-setup.sh first."
profile_from_env="$(sed -n 's/^AWS_PROFILE=//p' "$env_file" | tail -n 1)"
[[ -n "$profile_from_env" ]] && AWS_PROFILE_VALUE="$profile_from_env"
CASE_TABLE_NAME="$(sed -n 's/^CASE_TABLE_NAME=//p' "$env_file" | tail -n 1)"
CASE_ACCESS_LOG_TABLE_NAME="$(sed -n 's/^CASE_ACCESS_LOG_TABLE_NAME=//p' "$env_file" | tail -n 1)"
[[ "$CASE_TABLE_NAME" == insolvia-dev-*-cases ]] ||
  die "CASE_TABLE_NAME in services/api/.env is not a dev case table — this proof runs against dev only."
[[ -x "$API_DIR/.venv/bin/python" ]] || die "services/api/.venv is missing — run ./services/api/scripts/dev-setup.sh."

for command in aws jq; do require_command "$command"; done
export_temporary_aws_credentials
export AWS_DEFAULT_REGION="$AWS_REGION_VALUE"
unset AWS_PROFILE
export CASE_TABLE_NAME CASE_ACCESS_LOG_TABLE_NAME

exec "$API_DIR/.venv/bin/python" - <<'PY'
import base64
import os
import sys
import time
import uuid

import boto3
from botocore.exceptions import ClientError
from insolvia_core.adapters.aws.access_log import DynamoDbAccessLog
from insolvia_core.adapters.aws.filing_credentials import (
    DynamoDbFilingAuthorizationStore,
    DynamoDbFilingCredentialStore,
    KmsCredentialOpener,
    KmsCredentialSealer,
    filing_credentials_key_alias,
    filing_credentials_table_name,
)
from insolvia_core.filing_authorization import (
    TEXT_VERSION,
    sign_authorization,
    text_json,
)
from insolvia_core.filing_credentials import (
    AuthorizationRequiredError,
    CredentialUnavailableError,
    encryption_context,
    enrol_credential,
    open_credential,
    parse_enrolment,
    revoke_credential,
    withdraw_authorization,
)

case_table = os.environ["CASE_TABLE_NAME"]
table = filing_credentials_table_name(case_table)
alias = filing_credentials_key_alias(case_table)
role_name = table.removesuffix("-filing-credentials") + "-filing-role"
account = boto3.client("sts").get_caller_identity()
role_arn = f"arn:aws:iam::{account['Account']}:role/{role_name}"

FIRM = "proof-firm-00000000"
ATTORNEY = str(uuid.uuid4())
FILING = "proof-filing-" + uuid.uuid4().hex[:8]
password = "FAKE-ECF-PASSWORD-not-a-real-one"
seed = base64.b32encode(os.urandom(20)).decode("ascii")


def step(text: str) -> None:
    print(f"\n== {text}", flush=True)


print(f"developer : {account['Arn']}")
print(f"vault     : {table} / {alias}")
print(f"worker    : {role_arn}")

api_store = DynamoDbFilingCredentialStore(table)
api_authorizations = DynamoDbFilingAuthorizationStore(table)
api_log = DynamoDbAccessLog(os.environ["CASE_ACCESS_LOG_TABLE_NAME"])
api_sealer = KmsCredentialSealer(alias)


def api_enrol(login: str, secret_seed: str):
    return enrol_credential(
        parse_enrolment({"login": login, "password": password, "totp_seed": secret_seed}),
        firm_id=FIRM,
        attorney_id=ATTORNEY,
        sealer=api_sealer,
        store=api_store,
        access_log=api_log,
        authorizations=api_authorizations,
    )


# ── 0. no signed authorization, no enrolment ────────────────────
step("0. enrol before signing the authorization (guardrail 2)")
try:
    api_enrol("FAKE-ECF-USER", seed)
except AuthorizationRequiredError as refusal:
    print(f"REFUSED: AuthorizationRequiredError: {refusal}")
else:
    sys.exit("FAIL: a credential enrolled without a signed authorization")
assert api_store.list_for_attorney(FIRM, ATTORNEY) == ()
print("nothing was sealed or stored for this attorney")

step("0b. the attorney signs the current text")
now = time.time()
signed = sign_authorization(
    {"text_version": TEXT_VERSION, "text_digest": text_json()["digest"]},
    firm_id=FIRM,
    attorney_id=ATTORNEY,
    # The API passes the token's auth_time; this script signs in to
    # nothing, so it stands in a just-now one (see the header).
    authenticated_at=int(now),
    now=now,
    store=api_authorizations,
    access_log=api_log,
)
print(f"signed authorization {signed.authorization_id}: version {signed.text_version}, sha256 {signed.text_digest[:12]}...")

# ── 1. enrol, as the API's stand-in ─────────────────────────────
step("1. enrol seals (developer = the API's principal in dev)")
credential = api_enrol("FAKE-ECF-USER", seed)
print(f"enrolled under authorization {credential.authorization_ref}")
stored = boto3.client("dynamodb").get_item(
    TableName=table,
    Key={
        "PK": {"S": f"ATTORNEY#{FIRM}#{ATTORNEY}"},
        "SK": {"S": f"CREDENTIAL#{credential.credential_id}"},
    },
    ConsistentRead=True,
)["Item"]
assert password not in repr(stored) and seed not in repr(stored)
print(f"sealed credential {credential.credential_id}; item attributes: {sorted(stored)}")
print("the stored item contains neither the password nor the seed")

# ── 2. the API's principal is refused Decrypt ───────────────────
step("2. the API's principal asks KMS to open it")
context = encryption_context(
    firm_id=FIRM, attorney_id=ATTORNEY, credential_id=credential.credential_id
)
try:
    KmsCredentialOpener(alias).open(credential.envelope, context=context)
except ClientError as error:
    print(f"REFUSED: {error.response['Error']['Code']}: {error.response['Error']['Message']}")
else:
    sys.exit("FAIL: the developer (API stand-in) opened the vault")

# ── 3. the worker's role opens ──────────────────────────────────
step("3. the filing worker's role opens it")
assumed = boto3.client("sts").assume_role(
    RoleArn=role_arn, RoleSessionName="dev-filing-vault-proof"
)["Credentials"]
worker = boto3.session.Session(
    aws_access_key_id=assumed["AccessKeyId"],
    aws_secret_access_key=assumed["SecretAccessKey"],
    aws_session_token=assumed["SessionToken"],
)
print(f"assumed   : {worker.client('sts').get_caller_identity()['Arn']}")
worker_store = DynamoDbFilingCredentialStore(table, client=worker.client("dynamodb"))
worker_log = DynamoDbAccessLog(os.environ["CASE_ACCESS_LOG_TABLE_NAME"])
worker_log.client = worker.client("dynamodb")
worker_opener = KmsCredentialOpener(alias, client=worker.client("kms"))
worker_authorizations = DynamoDbFilingAuthorizationStore(
    table, client=worker.client("dynamodb")
)


def worker_open(credential_id=None):
    return open_credential(
        firm_id=FIRM,
        attorney_id=ATTORNEY,
        credential_id=credential_id or credential.credential_id,
        filing_id=FILING,
        purpose="dev_proof",
        opener=worker_opener,
        store=worker_store,
        authorizations=worker_authorizations,
        access_log=worker_log,
    )


secret = worker_open()
assert secret.password == password and secret.totp_seed == seed
print("OPENED: the plaintext matches what was sealed (values not printed)")
print(f"access row written: credential.open, filing {FILING}, purpose dev_proof")

# ── 4. the wrong context fails ──────────────────────────────────
step("4. the worker opens the same envelope under another attorney's context")
try:
    worker_opener.open(credential.envelope, context={**context, "attorney_id": str(uuid.uuid4())})
except ClientError as error:
    print(f"REFUSED: {error.response['Error']['Code']}")
else:
    sys.exit("FAIL: an envelope opened under the wrong context")

# ── 5. revoke destroys; the next open fails ─────────────────────
step("5. revoke (as the API's stand-in), then the worker opens again")
assert revoke_credential(
    credential.credential_id,
    firm_id=FIRM,
    attorney_id=ATTORNEY,
    store=api_store,
    access_log=api_log,
)
print(f"revoked: item present afterwards? {api_store.get(FIRM, ATTORNEY, credential.credential_id) is not None}")
try:
    worker_open()
except CredentialUnavailableError:
    print("REFUSED: CredentialUnavailableError — nothing left to open")
else:
    sys.exit("FAIL: a revoked credential opened")

# ── 6. withdrawal revokes; the next open fails ──────────────────
step("6. enrol a second login, the worker opens it, then the attorney withdraws")
second_seed = base64.b32encode(os.urandom(20)).decode("ascii")
second = api_enrol("FAKE-ECF-USER-2", second_seed)
assert worker_open(second.credential_id).totp_seed == second_seed
print("OPENED by the worker while the authorization is in force")
destroyed = withdraw_authorization(
    firm_id=FIRM,
    attorney_id=ATTORNEY,
    authorizations=api_authorizations,
    store=api_store,
    access_log=api_log,
)
print(f"withdrawn: {destroyed} credential(s) destroyed")
print(f"credential item present afterwards? {api_store.get(FIRM, ATTORNEY, second.credential_id) is not None}")
print(f"current authorization present afterwards? {api_authorizations.get_current(FIRM, ATTORNEY) is not None}")
history = boto3.client("dynamodb").get_item(
    TableName=table,
    Key={
        "PK": {"S": f"ATTORNEY#{FIRM}#{ATTORNEY}"},
        "SK": {"S": f"AUTHORIZATION#{signed.authorization_id}"},
    },
    ConsistentRead=True,
)["Item"]
print(f"history record kept: status {history['status']['S']}, withdrawn at {history['withdrawnAt']['S']}")
try:
    worker_open(second.credential_id)
except CredentialUnavailableError:
    print("REFUSED: CredentialUnavailableError - withdrawn, nothing left to open")
else:
    sys.exit("FAIL: a credential opened after its authorization was withdrawn")
try:
    api_enrol("FAKE-ECF-USER-3", seed)
except AuthorizationRequiredError:
    print("REFUSED: a new enrolment after withdrawal - AuthorizationRequiredError")
else:
    sys.exit("FAIL: a credential enrolled after the authorization was withdrawn")

print("\nALL HELD: guardrail 3 (steps 1-5) and guardrail 2 (steps 0 and 6).")
PY
