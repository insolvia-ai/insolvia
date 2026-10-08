#!/usr/bin/env bash
#
# Prove the per-filing approval (ADR 0024 PR 6, guardrail 1) on THIS
# MACHINE's real dev AWS — the real case table, document bucket and case key,
# the real vault, and the real filing queue — with fake values only:
#
#   ./services/api/scripts/dev-filing-approval-proof.sh
#
#   0. A READY CASE. The unit tier's reference case (every form projects
#      cleanly) is written into the dev case table under a new case id and a
#      fixed, obviously fake firm, and the REAL packet-assembly worker renders
#      it into the dev bucket — so every document has measured bytes and a
#      SHA-256. The attorney signs the authorization and enrols a fake court
#      login for flmb in the real vault.
#   1. APPROVAL ENQUEUES EXACTLY ONE MESSAGE on insolvia-dev-<id>-filing, and
#      its body is the five ids-only keys: no password, no seed, no digest,
#      no case data.
#   2. A DOCUMENT CHANGE VOIDS IT: the packet is re-assembled, the worker's
#      consume recomputes the digest and is refused (`changed`); the record
#      says `voided`; nothing is enqueued.
#   3. A DEBTOR EDIT VOIDS IT: a second approval, then a debtor field edited;
#      the status read voids it; nothing is enqueued.
#   4. SINGLE USE: a third approval is consumed once (the conditional write
#      against the real table), the second consume is refused; nothing is
#      enqueued by either.
#   5. NO SECOND FILING: approving again while that filing is in flight is
#      refused; nothing is enqueued.
#   6. A STALE SIGN-IN IS REFUSED: nothing is recorded or enqueued.
#
# Then the proof's own messages are received and deleted (any other message
# on the queue is left as it was), and the fake login is destroyed by
# withdrawing the authorization.
#
# WHAT IS LEFT BEHIND, said so nobody mistakes it for a leak: one case
# partition per run under the fake firm `proof-firm-00000000` — nothing a
# real firm can reach — and its two packets in the dev bucket. Like
# test_case_lifecycle's copy, it is the cost of proving against the real
# tables, and `scripts/dev-aws-reset.sh` clears it with everything else.
#
# The approver's fresh `auth_time` is a stand-in (this script signs in to
# nothing); the API's fresh-sign-in check against the real pool is the
# integration tier's (tests/integration/test_filing_approval.py), and the
# route's is the unit tier's. The CONSUME is the filing worker's act (ADR
# 0024 PR 7, not built): the developer performs it here under their own
# principal, exactly as the local worker poller will.
#
# NOTHING REAL, NOTHING PRINTED. The login is FAKE-ECF-USER, the password a
# fake literal, the TOTP seed minted from os.urandom; the debtors are the
# unit tier's fictional reference household. The script prints outcomes and
# ids, never a value.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
API_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

# shellcheck source=/dev/null
source "$REPO_ROOT/scripts/dev-aws-common.sh"

env_file="$API_DIR/.env"
[[ -f "$env_file" ]] || die "services/api/.env is missing — run ./services/api/scripts/dev-setup.sh first."
read_env() { sed -n "s/^$1=//p" "$env_file" | tail -n 1; }
profile_from_env="$(read_env AWS_PROFILE)"
[[ -n "$profile_from_env" ]] && AWS_PROFILE_VALUE="$profile_from_env"
CASE_TABLE_NAME="$(read_env CASE_TABLE_NAME)"
CASE_ACCESS_LOG_TABLE_NAME="$(read_env CASE_ACCESS_LOG_TABLE_NAME)"
CASE_DOCUMENT_BUCKET="$(read_env CASE_DOCUMENT_BUCKET)"
FILING_QUEUE_URL="$(read_env FILING_QUEUE_URL)"
[[ "$CASE_TABLE_NAME" == insolvia-dev-*-cases ]] ||
  die "CASE_TABLE_NAME in services/api/.env is not a dev case table — this proof runs against dev only."
[[ "$FILING_QUEUE_URL" == */insolvia-dev-*-filing ]] ||
  die "FILING_QUEUE_URL in services/api/.env is not a dev filing queue — run ./scripts/dev-aws-setup.sh."
[[ -n "$CASE_ACCESS_LOG_TABLE_NAME" && -n "$CASE_DOCUMENT_BUCKET" ]] ||
  die "CASE_ACCESS_LOG_TABLE_NAME / CASE_DOCUMENT_BUCKET are not in services/api/.env."
[[ -x "$API_DIR/.venv/bin/python" ]] || die "services/api/.venv is missing — run ./services/api/scripts/dev-setup.sh."

for command in aws jq; do require_command "$command"; done
export_temporary_aws_credentials
export AWS_DEFAULT_REGION="$AWS_REGION_VALUE"
unset AWS_PROFILE
export CASE_TABLE_NAME CASE_ACCESS_LOG_TABLE_NAME CASE_DOCUMENT_BUCKET FILING_QUEUE_URL

cd "$API_DIR"
exec "$API_DIR/.venv/bin/python" - <<'PY'
import base64
import dataclasses
import json
import os
import sys
import time
import uuid
from datetime import UTC, datetime

import boto3

# This service's own source (the venv installs only its dependencies), and
# the unit tier's reference case: the one case the goldens prove projects
# every form cleanly. Importing it from tests/ keeps one owner.
sys.path[:0] = [os.path.join(os.getcwd(), "src"), os.getcwd()]
from tests.unit.test_packet_assembly import reference_case_data  # noqa: E402

from insolvia_api.adapters.aws.filing_approval_store import DynamoDbFilingApprovalStore  # noqa: E402
from insolvia_core.adapters.aws.filing_store import DynamoDbFilingStore  # noqa: E402
from insolvia_api.adapters.aws.filing_queue import SqsFilingQueue  # noqa: E402
from insolvia_api.adapters.aws.packet_store import DynamoDbPacketStore  # noqa: E402
from insolvia_api.core.filing_approval import (  # noqa: E402
    FILING_JOB_KEYS,
    ApprovalUnavailableError,
    approval_basis,
    approve_filing,
    consume_approval,
    current_approval,
    parse_filing_job_message,
)
from insolvia_api.core.jobs import new_job  # noqa: E402
from insolvia_api.core.packet_assembly import (  # noqa: E402
    PACKET_ASSEMBLY_KIND,
    CaseData,
    PacketAssemblyDeps,
    read_case_data,
    run_packet_assembly,
)
from insolvia_core import courts  # noqa: E402
from insolvia_core.adapters.aws.access_log import DynamoDbAccessLog  # noqa: E402
from insolvia_core.adapters.aws.case_entity_store import DynamoDbCaseEntityStore  # noqa: E402
from insolvia_core.adapters.aws.case_store import DynamoDbCaseStore  # noqa: E402
from insolvia_core.adapters.aws.debtor_store import DynamoDbDebtorStore  # noqa: E402
from insolvia_core.adapters.aws.document_blobs import S3DocumentBlobStore  # noqa: E402
from insolvia_core.adapters.aws.filing_credentials import (  # noqa: E402
    DynamoDbFilingAuthorizationStore,
    DynamoDbFilingCredentialStore,
    KmsCredentialSealer,
    filing_credentials_key_alias,
    filing_credentials_table_name,
)
from insolvia_core.adapters.aws.tax_id_cipher import KmsTaxIdCipher, case_key_alias  # noqa: E402
from insolvia_core.adapters.aws.tax_id_store import DynamoDbTaxIdStore  # noqa: E402
from insolvia_core.auth import ReauthenticationRequiredError  # noqa: E402
from insolvia_core.cases import assign_case  # noqa: E402
from insolvia_core.errors import ConflictError  # noqa: E402
from insolvia_core.filing_authorization import TEXT_VERSION, sign_authorization, text_json  # noqa: E402
from insolvia_core.filing_credentials import (  # noqa: E402
    enrol_credential,
    parse_enrolment,
    withdraw_authorization,
)
from insolvia_core.tax_ids import TaxIdInput, store_tax_id  # noqa: E402

case_table = os.environ["CASE_TABLE_NAME"]
queue_url = os.environ["FILING_QUEUE_URL"]
FIRM = "proof-firm-00000000"
ATTORNEY = str(uuid.uuid4())
CASE_ID = str(uuid.uuid4())
PASSWORD = "FAKE-ECF-PASSWORD-not-a-real-one"
SEED = base64.b32encode(os.urandom(20)).decode("ascii")


def step(text: str) -> None:
    print(f"\n== {text}", flush=True)


def fail(text: str) -> None:
    sys.exit(f"FAIL: {text}")


print(f"developer : {boto3.client('sts').get_caller_identity()['Arn']}")
print(f"case table: {case_table}")
print(f"queue     : {queue_url.rsplit('/', 1)[-1]}")

case_store = DynamoDbCaseStore(case_table)
debtor_store = DynamoDbDebtorStore(case_table)
entity_store = DynamoDbCaseEntityStore(case_table)
packet_store = DynamoDbPacketStore(case_table)
access_log = DynamoDbAccessLog(os.environ["CASE_ACCESS_LOG_TABLE_NAME"])
tax_id_store = DynamoDbTaxIdStore(case_table)
tax_id_cipher = KmsTaxIdCipher(case_key_alias(case_table))
vault_table = filing_credentials_table_name(case_table)
credentials = DynamoDbFilingCredentialStore(vault_table)
authorizations = DynamoDbFilingAuthorizationStore(vault_table)
approvals = DynamoDbFilingApprovalStore(case_table)
filings = DynamoDbFilingStore(case_table)
queue = SqsFilingQueue(queue_url)
sqs = boto3.client("sqs")
deps = PacketAssemblyDeps(
    case_store=case_store,
    debtor_store=debtor_store,
    entity_store=entity_store,
    packet_store=packet_store,
    blobs=S3DocumentBlobStore(os.environ["CASE_DOCUMENT_BUCKET"]),
    access_log=access_log,
    tax_id_store=tax_id_store,
    tax_id_cipher=tax_id_cipher,
)
release = courts.resolve(datetime.now(UTC).date())


# ── 0. a ready case, a signature and a login ────────────────────
step("0. write the reference case into the dev table and assemble it for real")
reference = reference_case_data()
district = release.district("flmb")
assert district is not None
case = dataclasses.replace(
    reference.case,
    id=CASE_ID,
    firm_id=FIRM,
    court="flmb",
    division=district.divisions[0].code,
    district=district.name,
)
case_store.create(case, assign_case(case, subject=ATTORNEY, assigned_by=ATTORNEY))
for debtor in reference.debtors:
    debtor_store.create(dataclasses.replace(debtor, case_id=CASE_ID))
    digits = reference.tax_ids.get(debtor.filing_role)
    if debtor.tax_id is not None and digits is not None:
        store_tax_id(
            TaxIdInput(kind=debtor.tax_id.kind, value=digits),
            existing=debtor.tax_id,
            firm_id=FIRM,
            case_id=CASE_ID,
            cipher=tax_id_cipher,
            store=tax_id_store,
        )
entities = 0
for member in dataclasses.fields(CaseData):
    if member.name in ("case", "debtors", "tax_ids"):
        continue
    for entity in getattr(reference, member.name):
        entity_store.create(dataclasses.replace(entity, case_id=CASE_ID))
        entities += 1
print(f"case {CASE_ID}: {len(reference.debtors)} debtors, {entities} records")


def assemble() -> str:
    result = run_packet_assembly(
        new_job(PACKET_ASSEMBLY_KIND, case_id=CASE_ID, created_by=ATTORNEY), deps
    )
    if result["outcome"] != "assembled":
        fail(f"assembly did not assemble: {result.get('problems')}")
    return str(result["packet"]["id"])


packet_id = assemble()
print(f"assembled packet {packet_id} into the dev bucket")


def data() -> CaseData:
    stored = case_store.read_for_worker(CASE_ID)
    assert stored is not None
    return read_case_data(stored, debtor_store=debtor_store, entity_store=entity_store)


def basis():
    today = datetime.now(UTC).date()
    return approval_basis(
        data(), packets=packet_store.list_for_case(CASE_ID), release=release, as_of=today
    )


first_basis = basis()
if not first_basis.ready:
    fail(f"the reference case is not ready: {first_basis.blockers}")
files = [d for d in first_basis.filing_set.documents if d.part is not None]
print(f"ready: {len(files)} packet files, each with its own SHA-256; digest {first_basis.digest[:16]}...")

now = time.time()
sign_authorization(
    {"text_version": TEXT_VERSION, "text_digest": text_json()["digest"]},
    firm_id=FIRM,
    attorney_id=ATTORNEY,
    authenticated_at=int(now),
    now=now,
    store=authorizations,
    access_log=access_log,
)
login = enrol_credential(
    parse_enrolment(
        {"login": "FAKE-ECF-USER", "password": PASSWORD, "totp_seed": SEED, "courts": ["flmb"]}
    ),
    firm_id=FIRM,
    attorney_id=ATTORNEY,
    sealer=KmsCredentialSealer(filing_credentials_key_alias(case_table)),
    store=credentials,
    access_log=access_log,
    authorizations=authorizations,
)
print(f"signed the authorization; enrolled fake login {login.credential_id} for flmb")


def approve(*, signed_in_ago: int = 0):
    at = time.time()
    current = basis()
    return approve_filing(
        {"digest": current.digest},
        data=data(),
        basis=current,
        firm_id=FIRM,
        attorney_id=ATTORNEY,
        role="attorney",
        authenticated_at=int(at) - signed_in_ago,
        now=at,
        credentials=credentials,
        authorizations=authorizations,
        approvals=approvals,
        filings=filings,
        queue=queue,
        access_log=access_log,
    )


def consume(approval):
    return consume_approval(
        CASE_ID,
        approval.approval_id,
        basis=basis(),
        now=time.time(),
        approvals=approvals,
        access_log=access_log,
    )


ours: dict[str, str] = {}  # approval id -> receipt handle


def drain() -> list[dict[str, str]]:
    """Receive what is on the queue now. Ours are kept (and deleted at the
    end); anybody else's is made visible again at once."""
    found: list[dict[str, str]] = []
    for _ in range(3):
        response = sqs.receive_message(
            QueueUrl=queue_url, MaxNumberOfMessages=10, WaitTimeSeconds=2, VisibilityTimeout=300
        )
        for message in response.get("Messages", []):
            body = message["Body"]
            try:
                job = parse_filing_job_message(body)
            except Exception:
                job = None
            if job is not None and job["case_id"] == CASE_ID:
                raw = json.loads(body)
                if set(raw) != FILING_JOB_KEYS:
                    fail("a filing job carries more than its five keys")
                for secret in (PASSWORD, SEED, first_basis.digest, login.credential_id):
                    if secret in body:
                        fail("a filing job carries a secret or the digest")
                ours[job["approval_id"]] = message["ReceiptHandle"]
                found.append(job)
            else:
                sqs.change_message_visibility(
                    QueueUrl=queue_url,
                    ReceiptHandle=message["ReceiptHandle"],
                    VisibilityTimeout=0,
                )
    return found


def expect_nothing_enqueued(what: str) -> None:
    extra = drain()
    if extra:
        fail(f"{what} enqueued {len(extra)} message(s)")
    print(f"queue: nothing enqueued by {what}")


# ── 1. approval enqueues exactly one message ────────────────────
step("1. approve (fresh sign-in, the login's owner) — exactly one message")
first = approve()
jobs = drain()
if [j["approval_id"] for j in jobs] != [first.approval_id]:
    fail(f"expected exactly one message for {first.approval_id}, got {jobs}")
expires = datetime.fromtimestamp(first.expires_at, UTC).isoformat()
print(f"approval {first.approval_id} pending, expires {expires}")
print(f"queue: exactly one message, keys {sorted(FILING_JOB_KEYS)}; no secret, no digest in it")

# ── 2. a document change voids it ───────────────────────────────
step("2. re-assemble the packet (a document changed), then the worker consumes")
assemble()
try:
    consume(first)
except ApprovalUnavailableError as refused:
    print(f"REFUSED: consume -> {refused.reason}")
else:
    fail("an approval was consumed after its documents changed")
stored = approvals.get(CASE_ID, first.approval_id)
print(f"record: {stored.status} ({stored.void_reason})")
expect_nothing_enqueued("the void")

# ── 3. a debtor edit voids it ───────────────────────────────────
step("3. approve again, edit a debtor field, read the status")
second = approve()
if [j["approval_id"] for j in drain()] != [second.approval_id]:
    fail("the second approval did not enqueue exactly one message")
debtor = debtor_store.get(CASE_ID, filing_role="debtor_1")
assert debtor is not None
debtor_store.put(
    dataclasses.replace(
        debtor,
        phone="555-0199",
        updated_at=datetime.now(UTC).isoformat(timespec="milliseconds").replace("+00:00", "Z"),
    )
)
seen = current_approval(
    CASE_ID, basis=basis(), principal=ATTORNEY, now=time.time(), approvals=approvals, access_log=access_log
)
print(f"status read: {seen.status} ({seen.void_reason})")
if seen.status != "voided":
    fail("a debtor edit did not void the approval")
expect_nothing_enqueued("the void")

# ── 4. single use ───────────────────────────────────────────────
step("4. approve a third time; the worker consumes it twice")
third = approve()
if [j["approval_id"] for j in drain()] != [third.approval_id]:
    fail("the third approval did not enqueue exactly one message")
print(f"consume #1: {consume(third).status}")
try:
    consume(third)
except ApprovalUnavailableError as refused:
    print(f"REFUSED: consume #2 -> {refused.reason}")
else:
    fail("an approval was consumed twice")
# And the table's own condition, not only the status read before it: the raw
# conditional write for a second use is refused by DynamoDB.
if approvals.consume(
    CASE_ID,
    third.approval_id,
    digest=third.digest,
    now=int(time.time()),
    consumed_at="proof-second-use",
):
    fail("DynamoDB accepted a second consume")
print("REFUSED: the conditional UpdateItem for a second use (ConditionalCheckFailed)")
expect_nothing_enqueued("either consume")

# ── 5. no second filing ─────────────────────────────────────────
step("5. approve again while that filing is in flight")
try:
    approve()
except ConflictError as refused:
    print(f"REFUSED: {refused}")
else:
    fail("a second approval was made while a filing was in flight")
expect_nothing_enqueued("the refused approval")

# ── 6. a stale sign-in ──────────────────────────────────────────
step("6. approve with a sign-in ten minutes old")
try:
    approve(signed_in_ago=600)
except ReauthenticationRequiredError:
    print("REFUSED: ReauthenticationRequiredError")
else:
    fail("a stale sign-in approved a filing")
expect_nothing_enqueued("the stale approval")

# ── clean up the proof's own messages and login ─────────────────
step("clean up")
for approval_id, handle in ours.items():
    sqs.delete_message(QueueUrl=queue_url, ReceiptHandle=handle)
print(f"deleted the proof's {len(ours)} message(s): {sorted(ours)}")
destroyed = withdraw_authorization(
    firm_id=FIRM,
    attorney_id=ATTORNEY,
    authorizations=authorizations,
    store=credentials,
    access_log=access_log,
)
print(f"withdrew the authorization; {destroyed} fake login destroyed")

print("\nALL HELD: one message per approval, ids only; a change voids; used once; no second filing; a stale sign-in refused.")
PY
