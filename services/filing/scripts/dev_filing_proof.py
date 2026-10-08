"""The body of dev-filing-proof.sh — run it through that script, which checks
it is aimed at dev, exports the developer's short-lived credentials and sets
the path. Prints outcomes and ids, never a value."""

from __future__ import annotations

import dataclasses
import json
import os
import sys
import time
import uuid
from datetime import UTC, datetime

import boto3
from botocore.exceptions import ClientError
from fake_cmecf import FakeCmEcf
from insolvia_api.adapters.aws.filing_approval_store import DynamoDbFilingApprovalStore
from insolvia_api.adapters.aws.filing_queue import SqsFilingQueue
from insolvia_api.adapters.aws.packet_store import DynamoDbPacketStore
from insolvia_api.core.filing_approval import approve_filing, basis_for_case
from insolvia_api.core.filing_outcome import parse_resolution, resolve_filing
from insolvia_api.core.jobs import new_job
from insolvia_api.core.packet_assembly import (
    PACKET_ASSEMBLY_KIND,
    CaseData,
    PacketAssemblyDeps,
    run_packet_assembly,
)
from insolvia_core import courts
from insolvia_core.adapters.aws.access_log import DynamoDbAccessLog
from insolvia_core.adapters.aws.case_entity_store import DynamoDbCaseEntityStore
from insolvia_core.adapters.aws.case_store import DynamoDbCaseStore
from insolvia_core.adapters.aws.debtor_store import DynamoDbDebtorStore
from insolvia_core.adapters.aws.document_blobs import S3DocumentBlobStore
from insolvia_core.adapters.aws.document_store import DynamoDbDocumentStore
from insolvia_core.adapters.aws.filing_credentials import (
    DynamoDbFilingAuthorizationStore,
    DynamoDbFilingCredentialStore,
    KmsCredentialSealer,
    filing_credentials_key_alias,
    filing_credentials_table_name,
)
from insolvia_core.adapters.aws.filing_store import DynamoDbFilingStore
from insolvia_core.adapters.aws.tax_id_cipher import KmsTaxIdCipher, case_key_alias
from insolvia_core.adapters.aws.tax_id_store import DynamoDbTaxIdStore
from insolvia_core.cases import FILING_WORKER_ACTOR, assign_case
from insolvia_core.errors import ConflictError
from insolvia_core.filing_authorization import (
    TEXT_VERSION,
    sign_authorization,
    text_json,
)
from insolvia_core.filing_credentials import (
    enrol_credential,
    parse_enrolment,
    withdraw_authorization,
)
from insolvia_core.tax_ids import TaxIdInput, store_tax_id
from insolvia_filing.adapters.http.fenced_client import FencedHttpClient
from insolvia_filing.core.config import load_config
from insolvia_filing.core.drivers.fake import FakeCmEcfDriver
from insolvia_filing.core.worker import FilingDeps, run_filing
from insolvia_filing.entrypoints.compose import compose
from tests.unit.test_packet_assembly import reference_case_data

TABLE = os.environ["CASE_TABLE_NAME"]
QUEUE = os.environ["FILING_QUEUE_URL"]
ROLE = os.environ["FILING_WORKER_ROLE_ARN"]
FIRM = "proof-firm-00000000"
ATTORNEY = str(uuid.uuid4())


def step(text: str) -> None:
    print(f"\n== {text}", flush=True)


def fail(text: str) -> None:
    sys.exit(f"FAIL: {text}")


# ── The developer's half (standing in for the API's role) ───────
dev = boto3.session.Session()
print(f"developer : {dev.client('sts').get_caller_identity()['Arn']}")
print(f"case table: {TABLE}")
print(f"queue     : {QUEUE.rsplit('/', 1)[-1]}")

case_store = DynamoDbCaseStore(TABLE)
debtor_store = DynamoDbDebtorStore(TABLE)
entity_store = DynamoDbCaseEntityStore(TABLE)
packet_store = DynamoDbPacketStore(TABLE)
access_log = DynamoDbAccessLog(os.environ["CASE_ACCESS_LOG_TABLE_NAME"])
tax_id_store = DynamoDbTaxIdStore(TABLE)
tax_id_cipher = KmsTaxIdCipher(case_key_alias(TABLE))
vault = filing_credentials_table_name(TABLE)
credentials = DynamoDbFilingCredentialStore(vault)
authorizations = DynamoDbFilingAuthorizationStore(vault)
approvals = DynamoDbFilingApprovalStore(TABLE)
# The API's view of the filing records (ADR 0024 PR 8): the resolution of a
# hand-back is the API's write, so the developer stands in for it here.
api_filings = DynamoDbFilingStore(TABLE)
queue = SqsFilingQueue(QUEUE)
dev_sqs = dev.client("sqs")
blobs = S3DocumentBlobStore(os.environ["CASE_DOCUMENT_BUCKET"])
documents = DynamoDbDocumentStore(TABLE)
assembly = PacketAssemblyDeps(
    case_store=case_store,
    debtor_store=debtor_store,
    entity_store=entity_store,
    packet_store=packet_store,
    blobs=blobs,
    access_log=access_log,
    tax_id_store=tax_id_store,
    tax_id_cipher=tax_id_cipher,
)
release = courts.resolve(datetime.now(UTC).date())
district = release.district("flmb")
assert district is not None
reference = reference_case_data()

court = FakeCmEcf(slow_seconds=4.0, hang_seconds=4.0)
court.start()
account = court.account_json()
print(f"fake court: {court.base_url} (in-process, loopback)")

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
        {
            "login": account["login"],
            "password": account["password"],
            "totp_seed": account["totp_seed"],
            "courts": ["flmb"],
        }
    ),
    firm_id=FIRM,
    attorney_id=ATTORNEY,
    sealer=KmsCredentialSealer(filing_credentials_key_alias(TABLE)),
    store=credentials,
    access_log=access_log,
    authorizations=authorizations,
)
print(
    f"signed the authorization; enrolled the FAKE court's login {login.credential_id}"
)


def ready_case() -> str:
    """A fresh copy of the reference case under the fake firm, assembled for
    real into the dev bucket."""
    case_id = str(uuid.uuid4())
    case = dataclasses.replace(
        reference.case,
        id=case_id,
        firm_id=FIRM,
        court="flmb",
        division=district.divisions[0].code,
        district=district.name,
    )
    case_store.create(case, assign_case(case, subject=ATTORNEY, assigned_by=ATTORNEY))
    for debtor in reference.debtors:
        debtor_store.create(dataclasses.replace(debtor, case_id=case_id))
        digits = reference.tax_ids.get(debtor.filing_role)
        if debtor.tax_id is not None and digits is not None:
            store_tax_id(
                TaxIdInput(kind=debtor.tax_id.kind, value=digits),
                existing=debtor.tax_id,
                firm_id=FIRM,
                case_id=case_id,
                cipher=tax_id_cipher,
                store=tax_id_store,
            )
    for member in dataclasses.fields(CaseData):
        if member.name in ("case", "debtors", "tax_ids"):
            continue
        for entity in getattr(reference, member.name):
            entity_store.create(dataclasses.replace(entity, case_id=case_id))
    result = run_packet_assembly(
        new_job(PACKET_ASSEMBLY_KIND, case_id=case_id, created_by=ATTORNEY), assembly
    )
    if result["outcome"] != "assembled":
        fail(f"assembly did not assemble: {result.get('problems')}")
    return case_id


def approve(case_id: str):
    stored = case_store.read_for_worker(case_id)
    assert stored is not None
    data, basis = basis_for_case(
        stored,
        debtor_store=debtor_store,
        entity_store=entity_store,
        packet_store=packet_store,
        as_of=datetime.now(UTC).date(),
    )
    if not basis.ready:
        fail(f"the reference case is not ready: {basis.blockers}")
    at = time.time()
    return approve_filing(
        {"digest": basis.digest, "credential_id": login.credential_id},
        data=data,
        basis=basis,
        firm_id=FIRM,
        attorney_id=ATTORNEY,
        role="attorney",
        authenticated_at=int(at),
        now=at,
        credentials=credentials,
        authorizations=authorizations,
        approvals=approvals,
        filings=api_filings,
        queue=queue,
        access_log=access_log,
    )


# ── The worker's half: AS ITS ROLE ──────────────────────────────
assumed = dev.client("sts").assume_role(
    RoleArn=ROLE, RoleSessionName="dev-filing-proof"
)["Credentials"]
role = boto3.session.Session(
    aws_access_key_id=assumed["AccessKeyId"],
    aws_secret_access_key=assumed["SecretAccessKey"],
    aws_session_token=assumed["SessionToken"],
    region_name=os.environ["AWS_DEFAULT_REGION"],
)
print(f"worker    : {role.client('sts').get_caller_identity()['Arn']}")
# compose() builds its adapters on boto3's DEFAULT session — so point the
# default at the role for exactly that call, and back at the developer after.
boto3.DEFAULT_SESSION = role
os.environ["FAKE_CMECF_URL"] = court.base_url
worker: FilingDeps = compose(load_config())
worker_sqs = role.client("sqs")
worker_filings = DynamoDbFilingStore(TABLE, client=role.client("dynamodb"))
boto3.DEFAULT_SESSION = dev


def deliver(case_id: str, deps: FilingDeps = worker):
    """Receive this case's job from the real queue AS THE ROLE and run it —
    the poller's loop, one message. Returns (result, receipt handle); the
    message is deleted only on a clean return, as the Lambda mapping does."""
    for _ in range(10):
        response = worker_sqs.receive_message(
            QueueUrl=QUEUE,
            MaxNumberOfMessages=10,
            WaitTimeSeconds=5,
            VisibilityTimeout=60,
        )
        for message in response.get("Messages", []):
            body = json.loads(message["Body"])
            if body.get("case_id") != case_id:
                worker_sqs.change_message_visibility(
                    QueueUrl=QUEUE,
                    ReceiptHandle=message["ReceiptHandle"],
                    VisibilityTimeout=0,
                )
                continue
            result = run_filing(message["Body"], deps)
            worker_sqs.delete_message(
                QueueUrl=QUEUE, ReceiptHandle=message["ReceiptHandle"]
            )
            return result
    fail(f"no message for case {case_id} arrived")
    raise AssertionError


def filing_of(case_id: str, approval):
    stored = worker_filings.get(case_id, approval.filing_id)
    assert stored is not None
    return stored


dev_ddb = dev.client("dynamodb")


def case_facts(case_id: str):
    """The case as stored, its match key (read off the raw item — it is
    never on the wire), and its lifecycle history."""
    case = case_store.read_for_worker(case_id)
    assert case is not None
    item = dev_ddb.get_item(
        TableName=TABLE,
        Key={"PK": {"S": f"CASE#{case_id}"}, "SK": {"S": "META"}},
        ConsistentRead=True,
    )["Item"]
    key = item.get("caseNumberKey", {}).get("S")
    return case, key, case_store.status_history(case_id)


def show_case(case_id: str) -> None:
    case, key, history = case_facts(case_id)
    print(
        f"case      : status={case.status} caseNumber={case.case_number}"
        f" filedAt={case.filed_at} caseNumberKey={key}"
    )
    for row in history:
        print(
            f"history   : {row.from_status} -> {row.to_status} by {row.changed_by}"
            f" (filing {row.filing_id}) at {row.changed_at}"
        )


# ── 1. files end to end ─────────────────────────────────────────
step("1. a ready case, approved, filed by the worker (as its role) against the fake")
court.reset_counts()
case_id = ready_case()
approval = approve(case_id)
print(f"case {case_id}: approved {approval.approval_id}; one message on the real queue")
result = deliver(case_id)
filed = filing_of(case_id, approval)
print(f"outcome   : {result.outcome}")
print(f"history   : {' -> '.join(s.state for s in filed.history)}")
print(
    f"court     : case number {filed.confirmation.case_number},"
    f" filed at {filed.confirmation.filed_at}"
)
print(
    f"fake court: {court.submissions} submission(s),"
    f" {court.uploads} document(s) received"
)
if result.outcome != "filed" or court.submissions != 1:
    fail("the reference case did not file exactly once")
receipt = documents.get(case_id, filed.receipt_document_id)
assert receipt is not None
pdf = blobs.get_bytes(receipt.storage_ref)
page = blobs.get_bytes(filed.confirmation.page_ref)
if not pdf or not pdf.startswith(b"%PDF") or not page:
    fail("the receipt or the court's page was not stored")
print(
    f"receipt   : document {receipt.id} ({receipt.kind}, {len(pdf)} bytes)"
    f" at {receipt.storage_ref}"
)
print(f"page      : {filed.confirmation.page_ref} ({len(page)} bytes)")
print(f"follow-ups: {list(filed.follow_ups)}")
show_case(case_id)
case, key, history = case_facts(case_id)
if (
    case.status != "filed"
    or case.case_number != filed.confirmation.case_number
    or case.filed_at != filed.confirmation.filed_at[:10]
    or key != f"flmb:{filed.confirmation.case_number}"
    or [(h.to_status, h.changed_by, h.filing_id) for h in history]
    != [("filed", FILING_WORKER_ACTOR, approval.filing_id)]
):
    fail("the worker's filed did not file the case, once, in the same write")
stored_case = case_store.read_for_worker(case_id)
assert stored_case is not None
_, after = basis_for_case(
    stored_case,
    debtor_store=debtor_store,
    entity_store=entity_store,
    packet_store=packet_store,
    as_of=datetime.now(UTC).date(),
)
print(f"approvable: blockers={list(after.blockers)} (the pins are frozen)")
if "filed" not in after.blockers:
    fail("a filed case could still be approved")

# ── 2. a redelivered job ────────────────────────────────────────
step("2. the same job delivered again")
dev_sqs.send_message(
    QueueUrl=QUEUE,
    MessageBody=json.dumps(
        {
            "kind": "filing.submit",
            "version": 1,
            "approval_id": approval.approval_id,
            "filing_id": approval.filing_id,
            "case_id": case_id,
        }
    ),
)
again = deliver(case_id)
print(f"outcome   : {again.outcome} ({again.reason})")
print(f"fake court: {court.submissions} submission(s)")
if again.outcome != "noop" or court.submissions != 1:
    fail("a redelivered job submitted again")
if len(case_facts(case_id)[2]) != 1:
    fail("a redelivered job wrote a second history row")


# ── 3. a crash after the final submit ───────────────────────────
class Crash(BaseException):
    """A process dying after the click — nothing in the worker catches it."""


class CrashAfterSubmit:
    def __init__(self, base_url: str) -> None:
        self._inner = FakeCmEcfDriver(base_url)

    @property
    def driver_id(self) -> str:
        return self._inner.driver_id

    @property
    def base_urls(self):
        return self._inner.base_urls

    def start(self, http):
        session = self._inner.start(http)
        submit = session.final_submit

        def submit_then_die():
            submit()
            raise Crash

        session.final_submit = submit_then_die
        return session


step("3. the worker dies right after the final submit; SQS redelivers")
court.reset_counts()
case_id = ready_case()
approval = approve(case_id)
crashing = dataclasses.replace(
    worker, fake_driver=lambda: CrashAfterSubmit(court.base_url)
)
handle = None
for _ in range(10):
    response = worker_sqs.receive_message(
        QueueUrl=QUEUE, MaxNumberOfMessages=10, WaitTimeSeconds=5, VisibilityTimeout=60
    )
    for message in response.get("Messages", []):
        if json.loads(message["Body"]).get("case_id") != case_id:
            worker_sqs.change_message_visibility(
                QueueUrl=QUEUE,
                ReceiptHandle=message["ReceiptHandle"],
                VisibilityTimeout=0,
            )
            continue
        try:
            run_filing(message["Body"], crashing)
        except Crash:
            handle = message["ReceiptHandle"]
        break
    if handle:
        break
if handle is None:
    fail("the crash run did not happen")
print(
    f"crashed   : record left at {filing_of(case_id, approval).state};"
    " message NOT deleted"
)
worker_sqs.change_message_visibility(
    QueueUrl=QUEUE, ReceiptHandle=handle, VisibilityTimeout=0
)
redelivered = deliver(case_id)
print(f"redelivery: {redelivered.outcome} ({redelivered.reason})")
print(f"fake court: {court.submissions} submission(s)")
if redelivered.outcome != "outcome_unknown" or court.submissions != 1:
    fail("a crash after the final submit was retried")

# ── 4. every fault mode ─────────────────────────────────────────
step("4. every fault mode, one fresh case each")
EXPECTED = {
    "bad_login": "handed_back",
    "totp_rejected": "handed_back",
    "slow": "handed_back",
    "upload_500": "handed_back",
    "duplicate_case": "handed_back",
    "unexpected_screen": "handed_back",
    "timeout_after_submit": "outcome_unknown",
    "receipt_missing": "outcome_unknown",
}
# A two-second request timeout, so the slow and hang faults (four seconds)
# exceed it; the deployed default is a minute.
quick = dataclasses.replace(
    worker, http=lambda: FencedHttpClient(worker.fence, timeout=2.0)
)
for fault, expected in EXPECTED.items():
    court.reset_counts()
    court.set_fault(fault)
    case_id = ready_case()
    approval = approve(case_id)
    outcome = deliver(case_id, quick)
    stored = filing_of(case_id, approval)
    note = stored.hand_back
    print(
        f"{fault:21} -> {stored.state:15} reason={note.reason if note else None:28}"
        f" submissions={court.submissions}"
    )
    if stored.state != expected or note is None or outcome.outcome != expected:
        fail(f"{fault} did not end {expected}")
court.set_fault("none")


# ── 5. a hand-back, resolved by the attorney ────────────────────
def resolve(case_id: str, approval, body: dict[str, object]):
    """The API's resolution, as the developer standing in for its role."""
    case = case_store.read_for_worker(case_id)
    assert case is not None
    return resolve_filing(
        parse_resolution({"docket_checked": True, **body}),
        filing=filing_of(case_id, approval),
        case=case,
        principal=ATTORNEY,
        now=time.time(),
        filings=api_filings,
        documents=documents,
        access_log=access_log,
    )


step("5a. handed back (the court refused the login); the attorney files it themself")
court.reset_counts()
court.set_fault("bad_login")
case_id = ready_case()
approval = approve(case_id)
outcome = deliver(case_id)
court.set_fault("none")
print(f"worker    : {outcome.outcome} ({outcome.reason})")
by_hand = f"6:26-bk-{20000 + int(time.time()) % 10000:05d}-FAK"
today = datetime.now(UTC).date().isoformat()
resolved = resolve(
    case_id, approval, {"outcome": "filed", "case_number": by_hand, "filed_at": today}
)
print(
    f"resolved  : {resolved.resolution.outcome} by the attorney,"
    f" docket checked at {resolved.resolution.docket_checked_at}"
)
show_case(case_id)
case, key, history = case_facts(case_id)
if (
    case.status != "filed"
    or case.case_number != by_hand
    or key != f"flmb:{by_hand.rsplit('-', 1)[0]}"
    or [(h.to_status, h.changed_by, h.filing_id) for h in history]
    != [("filed", ATTORNEY, approval.filing_id)]
):
    fail("a hand-back resolved as filed did not file the case")

step("5b. handed back; the attorney checked the docket: NOT filed; approve again")
court.reset_counts()
court.set_fault("bad_login")
case_id = ready_case()
approval = approve(case_id)
deliver(case_id)
court.set_fault("none")
try:
    approve(case_id)
except ConflictError as refused_while_unresolved:
    print(f"refused   : {refused_while_unresolved}")
else:
    fail("a second approval was made over an unresolved hand-back")
resolve(case_id, approval, {"outcome": "not_filed"})
second = approve(case_id)
print(f"approved  : {second.approval_id} (a new filing, {second.filing_id})")
outcome = deliver(case_id)
print(f"worker    : {outcome.outcome}; fake court {court.submissions} submission(s)")
show_case(case_id)
if outcome.outcome != "filed" or court.submissions != 1:
    fail("the case freed by not_filed did not file exactly once")

# ── 6. the role is narrow ───────────────────────────────────────
step("6. what the worker's role may NOT do")
role_s3 = role.client("s3")
role_ddb = role.client("dynamodb")


def refused(what: str, call) -> None:
    try:
        call()
    except ClientError as error:
        code = error.response["Error"]["Code"]
        print(f"REFUSED   : {what} ({code})")
        return
    fail(f"the role was allowed to {what}")


refused(
    "read an uploaded source document's key",
    lambda: role_s3.get_object(
        Bucket=os.environ["CASE_DOCUMENT_BUCKET"], Key=f"cases/{case_id}/{uuid.uuid4()}"
    ),
)
refused(
    "write outside its filing prefix",
    lambda: role_s3.put_object(
        Bucket=os.environ["CASE_DOCUMENT_BUCKET"],
        Key=f"cases/{case_id}/{uuid.uuid4()}",
        Body=b"x",
    ),
)
refused(
    "delete a row of the case table",
    lambda: role_ddb.delete_item(
        TableName=TABLE, Key={"PK": {"S": f"CASE#{case_id}"}, "SK": {"S": "META"}}
    ),
)
refused(
    "scan the case table",
    lambda: role_ddb.scan(TableName=TABLE, Limit=1),
)

# ── clean up ────────────────────────────────────────────────────
step("clean up")
destroyed = withdraw_authorization(
    firm_id=FIRM,
    attorney_id=ATTORNEY,
    authorizations=authorizations,
    store=credentials,
    access_log=access_log,
)
court.stop()
print(
    f"withdrew the authorization; {destroyed} fake login destroyed; fake court stopped"
)
print(
    "\nALL HELD: filed end to end once as the worker's role, and the case filed with"
    " the court's number, date, match key and one history row in the same write; a"
    " redelivery and a crash after the submit never submitted again; every fault"
    " mode stopped with a reason; a hand-back resolved as filed filed the case, and"
    " one resolved as not filed freed it for a new approval that filed once; the"
    " role is refused outside its grants."
)
