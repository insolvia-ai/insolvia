"""THE FILING WORKER — one filing job, start to end (ADR 0024 PR 7).

A job is the API's ids-only message (services/api core/filing_approval.
filing_job_message). Everything else is re-read from the stores under the
worker's own role. The run, in order — each numbered step either goes on or
STOPS, and a stop is final:

 1. Parse the message; find its approval; a message that names no approval
    of that filing is dropped (logged, nothing written).
 2. A RECORD ALREADY EXISTS — this is a redelivery. Never a second run:
      terminal                       → nothing to do
      at_final_submit                → outcome_unknown (the mark is down; the
                                       court may have it; never retried)
      submitted                      → finish storing the receipt (no court)
      claimed/signed_in/uploading    → within the lease: another attempt is
                                       live, leave it; past the lease: that
                                       attempt died before the mark —
                                       handed_back (`interrupted`)
 3. CLAIM: create the record (`claimed`), conditional on there being none.
 4. CONSUME the approval — services/api's own `consume_approval`, over the
    digest recomputed from the stores by the API's own `basis_for_case`.
    Refused (expired, used, voided, changed) → handed_back.
 5. The kill switch. Off → handed_back.
 6. The driver. None for this court (every real court today) → handed_back.
 7. The fence, for every origin the driver will use — BEFORE the credential
    is opened. Outside the allowlist → handed_back.
 8. The documents: every packet file the filing set files, its bytes from the
    approved packet, each checked against the SHA-256 the approval bound.
    A document prepared outside Insolvia that must go in WITH the petition
    → handed_back; one the court takes as its own event → a follow-up.
 9. OPEN the credential (`sign_in`), sign in (TOTP computed as the court asks),
    → signed_in; open the case → uploading; upload.
10. RE-CHECK, immediately before the final submit: the kill switch, the
    credential (opened again, `final_submit_recheck` — a revocation or a
    withdrawn authorization stops it here), the approval (still this
    filing's, still consumed) and its digest recomputed from the stores. Any
    miss → handed_back.
11. MARK: `at_final_submit`, durably, conditional on this attempt.
12. SUBMIT. Anything but a recognised confirmation → outcome_unknown.
13. `submitted`, with the court's case number and entries, the moment it is
    read; then the receipt stored with the case → `filed`.

Steps 3-10 stop with `handed_back`; nothing before step 11 can have reached
the court. The password and the seed live in one local variable for the
length of the run, are passed to the driver and to `totp_at`, and appear in no
log line, record or exception (tests/unit/test_worker.py captures the logs and
the stored items and searches them).
"""

from __future__ import annotations

import hashlib
import io
import logging
import time
import uuid
import zipfile
from collections.abc import Callable, Sequence
from dataclasses import dataclass, replace
from datetime import UTC, date, datetime
from typing import TYPE_CHECKING, Final

from insolvia_api.core.filing_approval import (
    ApprovalBasis,
    ApprovalUnavailableError,
    FilingApproval,
    basis_for_case,
    consume_approval,
    parse_filing_job_message,
)
from insolvia_api.core.filing_set import FilingDocument
from insolvia_api.core.packets import Packet
from insolvia_core.access_log import record_access
from insolvia_core.errors import ValidationError
from insolvia_core.filing_credentials import CredentialUnavailableError, open_credential

from .config import DEFAULT_LEASE_SECONDS
from .drivers.base import (
    CaseOpening,
    CourtConfirmation,
    CourtDriver,
    CourtSession,
    DriverFactory,
    FilingFile,
    HandBackError,
    SubmitUncertainError,
    resolve_driver,
)
from .fence import HostFence, HostNotAllowedError
from .filings import (
    BEFORE_SUBMIT,
    TERMINAL,
    Confirmation,
    Filing,
    Step,
    may_transition,
)
from .hand_back import hand_back_note, outcome_unknown_note
from .receipt import (
    CONFIRMATION_CONTENT_TYPE,
    RECEIPT_CONTENT_TYPE,
    confirmation_ref,
    receipt_document,
    render_receipt,
)
from .totp import totp_at

if TYPE_CHECKING:
    from insolvia_api.core.ports import FilingApprovalStore, PacketStore
    from insolvia_core.ports import (
        AccessLog,
        CaseEntityStore,
        CaseStore,
        DebtorStore,
        DocumentBlobStore,
        DocumentStore,
        FilingAuthorizationStore,
        FilingCredentialOpener,
        FilingCredentialStore,
    )

    from .ports import FilingStore, HttpClient, KillSwitch

logger = logging.getLogger(__name__)

# What the worker's access rows say they are for.
PURPOSE_SIGN_IN: Final = "sign_in"
PURPOSE_RECHECK: Final = "final_submit_recheck"
PURPOSE_DIGEST: Final = "filing_digest"

FOLLOW_UP_FEE: Final = "pay_fee"


@dataclass(frozen=True)
class FilingDeps:
    environment: str
    filings: FilingStore
    approvals: FilingApprovalStore
    case_store: CaseStore
    debtor_store: DebtorStore
    entity_store: CaseEntityStore
    packet_store: PacketStore
    blobs: DocumentBlobStore
    document_store: DocumentStore
    credentials: FilingCredentialStore
    authorizations: FilingAuthorizationStore
    opener: FilingCredentialOpener
    access_log: AccessLog
    kill_switch: KillSwitch
    fence: HostFence
    # A fresh, fenced HTTP client per run (one session's cookies).
    http: Callable[[], HttpClient]
    # The local composition's fake driver; None everywhere deployed.
    fake_driver: DriverFactory | None = None
    clock: Callable[[], float] = time.time
    lease_seconds: int = DEFAULT_LEASE_SECONDS


@dataclass(frozen=True)
class FilingResult:
    """What one message came to. `outcome` is a filing state, or `dropped`
    (no such approval) or `noop` (a redelivery with nothing to do)."""

    outcome: str
    filing_id: str | None = None
    reason: str | None = None


class _StopError(Exception):
    """Internal: a hand-back decided by the worker itself."""

    def __init__(self, reason: str, *, court_said: str | None = None) -> None:
        super().__init__(reason)
        self.reason = reason
        self.court_said = court_said


def _iso(moment: float) -> str:
    return (
        datetime.fromtimestamp(moment, UTC)
        .isoformat(timespec="milliseconds")
        .replace("+00:00", "Z")
    )


class _Run:
    """One message's run. Holds the current record and writes every
    transition through `_move`, the one place a state changes."""

    def __init__(self, deps: FilingDeps, approval: FilingApproval) -> None:
        self.deps = deps
        self.approval = approval
        self.filing: Filing | None = None

    # ── the record ──────────────────────────────────────────────

    def _move(self, state: str, **changes: object) -> bool:
        """Advance the record to `state`, conditional on the state and the
        attempt this run last saw. False when anything else moved first —
        the caller stops, touching nothing further."""
        current = self.filing
        assert current is not None
        if not may_transition(current.state, state):
            raise RuntimeError(f"no transition {current.state} -> {state}")
        now = _iso(self.deps.clock())
        moved = replace(
            current,
            state=state,  # type: ignore[arg-type]
            updated_at=now,
            history=(*current.history, Step(state=state, at=now)),
            **changes,  # type: ignore[arg-type]
        )
        if not self.deps.filings.transition(moved, expected_state=current.state):
            logger.warning(
                "filing transition lost a race; stopping",
                extra={"filing_id": current.filing_id, "to": state},
            )
            return False
        self.filing = moved
        logger.info(
            "filing state",
            extra={
                "filing_id": moved.filing_id,
                "case_id": moved.case_id,
                "state": state,
            },
        )
        return True

    def hand_back(self, reason: str, *, court_said: str | None = None) -> FilingResult:
        current = self.filing
        assert current is not None
        self._move(
            "handed_back",
            hand_back=hand_back_note(
                reason, stage=current.state, court_said=court_said
            ),
        )
        self.deps.access_log.record(
            record_access(
                case_id=current.case_id,
                principal=current.attorney_id,
                action="filing.hand_back",
                purpose=reason,
                filing_id=current.filing_id,
            )
        )
        return FilingResult("handed_back", current.filing_id, reason)

    def outcome_unknown(
        self, reason: str, *, court_said: str | None = None
    ) -> FilingResult:
        current = self.filing
        assert current is not None
        self._move(
            "outcome_unknown",
            unknown_reason=reason,
            hand_back=outcome_unknown_note(
                reason, stage=current.state, court_said=court_said
            ),
        )
        self.deps.access_log.record(
            record_access(
                case_id=current.case_id,
                principal=current.attorney_id,
                action="filing.submit",
                purpose=f"outcome_unknown:{reason}",
                filing_id=current.filing_id,
            )
        )
        return FilingResult("outcome_unknown", current.filing_id, reason)

    # ── reads ───────────────────────────────────────────────────

    def basis(self) -> ApprovalBasis:
        """The approval's digest, recomputed from the stores now — with the
        API's own composition (`basis_for_case`), so the two cannot differ."""
        deps = self.deps
        case = deps.case_store.read_for_worker(self.approval.case_id)
        deps.access_log.record(
            record_access(
                case_id=self.approval.case_id,
                principal=self.approval.attorney_id,
                action="case.read",
                outcome="allowed" if case is not None else "denied",
                purpose=PURPOSE_DIGEST,
                filing_id=self.approval.filing_id,
            )
        )
        if case is None:
            raise _StopError("approval_unavailable")
        today: date = datetime.fromtimestamp(deps.clock(), UTC).date()
        _, basis = basis_for_case(
            case,
            debtor_store=deps.debtor_store,
            entity_store=deps.entity_store,
            packet_store=deps.packet_store,
            as_of=today,
        )
        return basis

    def open_secret(self, purpose: str) -> tuple[str, str, str]:
        """(login, password, seed) — or a stop. The `credential.open` row is
        written by open_credential itself, before it reads anything."""
        deps = self.deps
        approval = self.approval
        try:
            secret = open_credential(
                firm_id=approval.firm_id,
                attorney_id=approval.attorney_id,
                credential_id=approval.credential_id,
                filing_id=approval.filing_id,
                purpose=purpose,
                opener=deps.opener,
                store=deps.credentials,
                authorizations=deps.authorizations,
                access_log=deps.access_log,
            )
        except CredentialUnavailableError as error:
            raise _StopError("credential_unavailable") from error
        credential = deps.credentials.get(
            approval.firm_id, approval.attorney_id, approval.credential_id
        )
        if credential is None:
            raise _StopError("credential_unavailable")
        return credential.login, secret.password, secret.totp_seed

    def files(self, basis: ApprovalBasis) -> tuple[list[FilingFile], list[str]]:
        """The documents to upload, in docket order, with their bytes checked
        against the approval; and the follow-ups for what the court takes
        separately from documents prepared outside Insolvia."""
        filing_set = basis.filing_set
        packet = filing_set.packet
        if packet is None or packet.id != self.approval.packet_id:
            raise _StopError("approval_unavailable")
        follow_ups: list[str] = []
        wanted: list[tuple[int, FilingDocument]] = []
        for position, document in enumerate(filing_set.documents, start=1):
            if document.handling == "not_filed":
                continue
            if document.source == "outside":
                if document.handling == "file":
                    raise _StopError("outside_document_required")
                follow_ups.append(f"file_separately:{document.key}")
                continue
            wanted.append((position, document))
        content = self.deps.blobs.get_bytes(packet.storage_ref)
        if content is None or hashlib.sha256(content).hexdigest() != packet.sha256:
            raise _StopError("document_mismatch")
        return _extract(packet, content, wanted), follow_ups


def _extract(
    packet: Packet, content: bytes, wanted: Sequence[tuple[int, FilingDocument]]
) -> list[FilingFile]:
    files: list[FilingFile] = []
    with zipfile.ZipFile(io.BytesIO(content)) as archive:
        names = set(archive.namelist())
        for position, document in wanted:
            part = document.part
            if part is None or part.sha256 is None or part.name not in names:
                raise _StopError("document_mismatch")
            data = archive.read(part.name)
            if hashlib.sha256(data).hexdigest() != part.sha256:
                raise _StopError("document_mismatch")
            files.append(
                FilingFile(
                    position=position,
                    key=document.key,
                    file_name=document.file_name,
                    handling=document.handling,
                    content=data,
                    sha256=part.sha256,
                )
            )
    return files


# ── The entry point ─────────────────────────────────────────────


def run_filing(body: str, deps: FilingDeps) -> FilingResult:
    """One filing job, from its queue message body. Returns normally on every
    outcome a run can decide; an exception escapes only before a record
    exists (an unreachable store at claim time), where an SQS retry is safe
    because nothing has happened — and every later attempt goes through
    step 2 first."""
    try:
        job = parse_filing_job_message(body)
    except ValidationError:
        logger.warning("dropped a message that is not a filing job")
        return FilingResult("dropped", reason="not_a_filing_job")
    approval = deps.approvals.get(job["case_id"], job["approval_id"])
    if approval is None or approval.filing_id != job["filing_id"]:
        logger.warning(
            "dropped a filing job naming no approval of that filing",
            extra={"case_id": job["case_id"], "filing_id": job["filing_id"]},
        )
        return FilingResult("dropped", job["filing_id"], "no_such_approval")

    run = _Run(deps, approval)
    existing = deps.filings.get(approval.case_id, approval.filing_id)
    if existing is not None:
        run.filing = existing
        return _resume(run)

    now = deps.clock()
    claimed = Filing(
        filing_id=approval.filing_id,
        case_id=approval.case_id,
        approval_id=approval.approval_id,
        firm_id=approval.firm_id,
        attorney_id=approval.attorney_id,
        credential_id=approval.credential_id,
        court=approval.court,
        division=approval.division,
        state="claimed",
        attempt_id=str(uuid.uuid4()),
        claimed_at=_iso(now),
        lease_expires_at=int(now) + deps.lease_seconds,
        updated_at=_iso(now),
        history=(Step(state="claimed", at=_iso(now)),),
    )
    if not deps.filings.claim(claimed):
        # Another consumer claimed it between our read and our write.
        return FilingResult("noop", approval.filing_id, "claimed_elsewhere")
    run.filing = claimed
    logger.info(
        "filing claimed",
        extra={"filing_id": claimed.filing_id, "case_id": claimed.case_id},
    )
    try:
        return _file(run)
    except _StopError as stop:
        return _stop_before_submit(run, stop.reason, stop.court_said)
    except HandBackError as stop:
        return _stop_before_submit(run, stop.reason, stop.court_said)
    except HostNotAllowedError:
        return _stop_before_submit(run, "host_not_allowed", None)
    except Exception:
        # Before the mark, an unexpected error is still a hand-back: nothing
        # reached the court, and the worker never retries a filing.
        current = run.filing
        if current is not None and current.state in BEFORE_SUBMIT:
            logger.exception(
                "filing worker error before the final submit",
                extra={"filing_id": current.filing_id},
            )
            return run.hand_back("worker_error")
        raise


def _stop_before_submit(run: _Run, reason: str, court_said: str | None) -> FilingResult:
    current = run.filing
    assert current is not None
    if current.state not in BEFORE_SUBMIT:
        # Cannot happen by construction (every stop below is raised before the
        # mark); refuse to dress an after-mark state as a hand-back.
        raise RuntimeError(f"a hand-back requested in {current.state}")
    return run.hand_back(reason, court_said=court_said)


def _resume(run: _Run) -> FilingResult:
    """Step 2: a record exists. Never starts a second filing."""
    filing = run.filing
    assert filing is not None
    deps = run.deps
    if filing.state in TERMINAL:
        return FilingResult("noop", filing.filing_id, filing.state)
    if filing.state == "at_final_submit":
        return run.outcome_unknown("interrupted_after_submit_mark")
    if filing.state == "submitted":
        return _capture(run)
    if deps.clock() < filing.lease_expires_at:
        return FilingResult("noop", filing.filing_id, "attempt_in_flight")
    return run.hand_back("interrupted")


def _file(run: _Run) -> FilingResult:
    deps = run.deps
    approval = run.approval

    # 4. consume — the API's own function over the API's own digest.
    basis = run.basis()
    try:
        consume_approval(
            approval.case_id,
            approval.approval_id,
            basis=basis,
            now=deps.clock(),
            approvals=deps.approvals,
            access_log=deps.access_log,
        )
    except ApprovalUnavailableError as refused:
        logger.info(
            "approval refused at consume",
            extra={"filing_id": approval.filing_id, "refusal": refused.reason},
        )
        raise _StopError("approval_unavailable") from refused

    # 5. the kill switch.
    if not deps.kill_switch.submissions_enabled():
        raise _StopError("submissions_disabled")

    # 6. the driver.
    driver: CourtDriver | None = resolve_driver(
        approval.court, environment=deps.environment, fake=deps.fake_driver
    )
    if driver is None:
        raise _StopError("court_not_verified")

    # 7. the fence, before anything is decrypted.
    for origin in driver.base_urls:
        deps.fence.check(origin)

    # 8. the documents, checked against the approval.
    files, follow_ups = run.files(basis)
    joint = _joint(run)

    # 9. sign in, open, upload.
    session: CourtSession = driver.start(deps.http())
    _sign_in(run, session)
    if not run._move("signed_in", driver=driver.driver_id):
        return FilingResult("noop", approval.filing_id, "race")
    session.open_case(
        CaseOpening(
            court=approval.court,
            division=approval.division,
            chapter=_chapter(run),
            joint=joint,
        )
    )
    if not run._move("uploading"):
        return FilingResult("noop", approval.filing_id, "race")
    session.upload(files)

    # 10. re-check everything, immediately before the final submit.
    if not deps.kill_switch.submissions_enabled():
        raise _StopError("submissions_disabled")
    run.open_secret(PURPOSE_RECHECK)
    stored = deps.approvals.get(approval.case_id, approval.approval_id)
    if (
        stored is None
        or stored.status != "consumed"
        or stored.filing_id != approval.filing_id
        or stored.digest != approval.digest
    ):
        raise _StopError("approval_unavailable")
    if run.basis().digest != approval.digest:
        raise _StopError("changed_after_approval")

    # 11. the mark, durably, before the click.
    if not run._move("at_final_submit"):
        return FilingResult("noop", approval.filing_id, "race")

    # 12. the click. From here nothing is ever retried.
    try:
        confirmation = session.final_submit()
    except SubmitUncertainError as uncertain:
        return run.outcome_unknown(uncertain.reason, court_said=uncertain.court_said)
    except Exception:
        logger.exception("final submit failed", extra={"filing_id": approval.filing_id})
        return run.outcome_unknown("submit_error")
    return _submitted(run, confirmation, follow_ups)


def _sign_in(run: _Run, session: CourtSession) -> None:
    """Open the credential and sign in. The password and the seed are locals
    of THIS frame and nothing else: the TOTP is computed inside the court's
    ask (`totp_at` at that moment), and both die when this returns."""
    login, password, seed = run.open_secret(PURPOSE_SIGN_IN)
    clock = run.deps.clock
    session.sign_in(login, password, lambda: totp_at(seed, clock()))


def _chapter(run: _Run) -> int:
    case = run.deps.case_store.read_for_worker(run.approval.case_id)
    return case.chapter if case is not None else 0


def _joint(run: _Run) -> bool:
    return len(run.deps.debtor_store.list_for_case(run.approval.case_id)) > 1


def _submitted(
    run: _Run, confirmation: CourtConfirmation, follow_ups: list[str]
) -> FilingResult:
    """13. Record what the court said, the moment it was read — then store
    the receipt. The page is stored before the state moves so the record can
    point at it; a page that cannot be stored is not a reason to lose the
    case number."""
    deps = run.deps
    filing = run.filing
    assert filing is not None
    page_ref: str | None = None
    try:
        ref = confirmation_ref(filing.case_id)
        deps.blobs.put_bytes(
            ref, content=confirmation.page, content_type=CONFIRMATION_CONTENT_TYPE
        )
        page_ref = ref
    except Exception:
        logger.exception(
            "could not store the confirmation page",
            extra={"filing_id": filing.filing_id},
        )
    captured = Confirmation(
        case_number=confirmation.case_number,
        filed_at=confirmation.filed_at,
        docket_entries=confirmation.docket_entries,
        receipt_number=confirmation.receipt_number,
        fee_due=confirmation.fee_due,
        page_ref=page_ref,
    )
    if not run._move(
        "submitted",
        confirmation=captured,
        follow_ups=(FOLLOW_UP_FEE, *follow_ups),
    ):
        return FilingResult("noop", filing.filing_id, "race")
    deps.access_log.record(
        record_access(
            case_id=filing.case_id,
            principal=filing.attorney_id,
            action="filing.submit",
            purpose="submitted",
            filing_id=filing.filing_id,
        )
    )
    return _capture(run)


def _capture(run: _Run) -> FilingResult:
    """submitted -> filed: the receipt, stored with the case. Touches no
    court, so a redelivery may finish it; if it cannot, the record keeps the
    court's case number and ends outcome_unknown for a human to reconcile."""
    deps = run.deps
    filing = run.filing
    assert filing is not None
    assert filing.confirmation is not None
    try:
        content = render_receipt(filing, filing.confirmation)
        document = receipt_document(filing, content)
        deps.blobs.put_bytes(
            document.storage_ref, content=content, content_type=RECEIPT_CONTENT_TYPE
        )
        deps.document_store.create(document)
    except Exception:
        logger.exception(
            "could not store the filing receipt", extra={"filing_id": filing.filing_id}
        )
        return run.outcome_unknown("capture_interrupted")
    if not run._move("filed", receipt_document_id=document.id):
        return FilingResult("noop", filing.filing_id, "race")
    return FilingResult("filed", filing.filing_id)
