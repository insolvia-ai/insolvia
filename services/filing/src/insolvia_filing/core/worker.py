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
    When the driver opens the case by Case Upload (`driver.case_upload`),
    Debtor.txt is built here too (services/api core/case_upload.py): each
    debtor's sealed tax id opened through a logged `taxid.read` (purpose
    `case_upload`, principal the approving attorney), the file validated
    against the AO spec, held in memory, uploaded FIRST, and never stored.
    A file that cannot be built or does not validate → handed_back
    (`case_upload_invalid`) before the credential is opened.
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

from insolvia_api.core import case_upload as case_upload_package
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
from insolvia_core.case_numbers import parse_case_number, petition_date
from insolvia_core.cases import FILING_WORKER_ACTOR, file_case, is_filed
from insolvia_core.errors import ApiError, ValidationError
from insolvia_core.filing_credentials import CredentialUnavailableError, open_credential
from insolvia_core.filings import (
    BEFORE_SUBMIT,
    TERMINAL,
    Confirmation,
    FiledCase,
    Filing,
    Step,
    may_transition,
)

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
    from insolvia_core.documents import Document
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
        FilingStore,
        TaxIdCipher,
        TaxIdStore,
    )

    from .ports import HttpClient, KillSwitch

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
    # The sealed tax ids, opened only for the Case Upload file (purpose
    # `case_upload`) — under the role's Decrypt grant fenced to the tax-id
    # encryption context (infra/modules/filing_worker, TaxIdOpen).
    tax_id_store: TaxIdStore
    tax_id_cipher: TaxIdCipher
    kill_switch: KillSwitch
    fence: HostFence
    # A fresh, fenced HTTP client per run (one session's cookies).
    http: Callable[[], HttpClient]
    # The local composition's fake driver; None everywhere deployed.
    fake_driver: DriverFactory | None = None
    # The record's clock: claims, leases, the approval's expiry.
    clock: Callable[[], float] = time.time
    # The COURT's clock — a TOTP code is valid for the court's 30-second step,
    # which is wall time whatever the record's clock says.
    wall_clock: Callable[[], float] = time.time
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

    def _move(
        self, state: str, *, filed_case: FiledCase | None = None, **changes: object
    ) -> bool:
        """Advance the record to `state`, conditional on the state and the
        attempt this run last saw — and, with `filed_case`, file the case in
        the same transaction (ADR 0024 PR 8). False when anything else moved
        first — the caller decides what that means, touching nothing until
        it has re-read."""
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
        if not self.deps.filings.transition(
            moved, expected_state=current.state, filed_case=filed_case
        ):
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
        if not self._move(
            "handed_back",
            hand_back=hand_back_note(
                reason, stage=current.state, court_said=court_said
            ),
        ):
            # Something else moved the record first (a redelivery that found
            # this attempt dead): it decided, not this run.
            return FilingResult("noop", current.filing_id, "race")
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
        if not self._move(
            "outcome_unknown",
            unknown_reason=reason,
            hand_back=outcome_unknown_note(
                reason, stage=current.state, court_said=court_said
            ),
        ):
            return FilingResult("noop", current.filing_id, "race")
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

    def case_upload(self) -> FilingFile:
        """Debtor.txt, built now from the stores — the records the
        approval's digest binds — with the full SSNs from the logged read.
        In memory only: returned to the caller for the upload, never stored,
        never logged. A file that cannot be built or does not validate is a
        stop; the problems name fields, never values, and only their codes
        are logged."""
        deps = self.deps
        approval = self.approval
        case = deps.case_store.read_for_worker(approval.case_id)
        deps.access_log.record(
            record_access(
                case_id=approval.case_id,
                principal=approval.attorney_id,
                action="case.read",
                outcome="allowed" if case is not None else "denied",
                purpose=case_upload_package.TAX_ID_PURPOSE,
                filing_id=approval.filing_id,
            )
        )
        if case is None:
            raise _StopError("approval_unavailable")
        try:
            built = case_upload_package.case_upload_file(
                case,
                principal=approval.attorney_id,
                as_of=datetime.fromtimestamp(deps.clock(), UTC).date(),
                debtor_store=deps.debtor_store,
                entity_store=deps.entity_store,
                tax_id_store=deps.tax_id_store,
                tax_id_cipher=deps.tax_id_cipher,
                access_log=deps.access_log,
            )
        except case_upload_package.CaseUploadError as refused:
            logger.info(
                "case upload file refused",
                extra={
                    "filing_id": approval.filing_id,
                    "problems": sorted(
                        {f"{p.record}.{p.field}:{p.code}" for p in refused.problems}
                    ),
                },
            )
            raise _StopError("case_upload_invalid") from None
        return FilingFile(
            position=0,
            key=case_upload_package.DOCUMENT_KEY,
            file_name=built.file_name,
            handling=case_upload_package.DOCUMENT_KEY,
            content=built.content,
            sha256=built.sha256,
        )

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
    if driver.case_upload:
        files = [run.case_upload(), *files]
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
    wall_clock = run.deps.wall_clock
    session.sign_in(login, password, lambda: totp_at(seed, wall_clock()))


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
        ref = confirmation_ref(filing.case_id, filing.filing_id)
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


# How many times `_capture` re-reads a case that moved between its read and
# its write before it leaves the filing for a person (`case_not_recorded`).
# A person editing the case's status at the same moment is the only thing
# that moves it; three reads outlast any honest race.
CAPTURE_ATTEMPTS: Final = 3


class _CaseNotRecordedError(Exception):
    """The court's confirmation cannot be written onto the case."""


def _capture(run: _Run) -> FilingResult:
    """submitted -> filed: the receipt stored with the case, and THE CASE
    FILED (ADR 0024 PR 8) — `status=filed`, the court's case number and the
    petition date, with a history row naming this filing — in the same
    conditional write that moves the record to `filed`.

    Touches no court, so a redelivery may finish it, and every step is
    idempotent: the receipt document's id is derived from the filing
    (`receipt_document`), so a second run finds the first run's row instead
    of writing another; the transaction lands once, because the record's
    condition is `submitted` and this attempt.

    A case that is ALREADY filed (a person recorded it by hand first) is
    left exactly as they wrote it — the record alone moves to `filed`. A
    case that moved underneath the write is read again, up to
    CAPTURE_ATTEMPTS times. Anything that stops the case being recorded —
    a receipt that cannot be stored, a confirmation whose number or date
    cannot be read, a case that keeps moving — ends `outcome_unknown` with
    the court's confirmation kept, so the attorney resolves it as filed
    (core/filing_outcome in the API) and can never record it as not filed."""
    deps = run.deps
    filing = run.filing
    assert filing is not None
    assert filing.confirmation is not None
    try:
        document = _store_receipt(run)
    except Exception:
        logger.exception(
            "could not store the filing receipt", extra={"filing_id": filing.filing_id}
        )
        return run.outcome_unknown("capture_interrupted")
    for _ in range(CAPTURE_ATTEMPTS):
        try:
            filed_case = _filed_case(run)
        except _CaseNotRecordedError:
            return run.outcome_unknown("case_not_recorded")
        if run._move("filed", receipt_document_id=document.id, filed_case=filed_case):
            deps.access_log.record(
                record_access(
                    case_id=filing.case_id,
                    principal=filing.attorney_id,
                    action="filing.submit",
                    purpose="case_filed" if filed_case is not None else "filed",
                    filing_id=filing.filing_id,
                )
            )
            return FilingResult("filed", filing.filing_id)
        stored = deps.filings.get(filing.case_id, filing.filing_id)
        if (
            stored is None
            or stored.state != filing.state
            or stored.attempt_id != filing.attempt_id
        ):
            # The record moved: whoever moved it decided.
            return FilingResult("noop", filing.filing_id, "race")
        # The record is still ours, so the CASE moved under the write: read
        # it again.
    return run.outcome_unknown("case_not_recorded")


def _store_receipt(run: _Run) -> Document:
    """The receipt PDF and its Document row — once. A redelivery that finds
    the row reuses it (the bytes were written before the row was)."""
    deps = run.deps
    filing = run.filing
    assert filing is not None
    assert filing.confirmation is not None
    content = render_receipt(filing, filing.confirmation)
    document = receipt_document(filing, content)
    existing = deps.document_store.get(filing.case_id, document.id)
    if existing is not None:
        return existing
    deps.blobs.put_bytes(
        document.storage_ref, content=content, content_type=RECEIPT_CONTENT_TYPE
    )
    deps.document_store.create(document)
    return document


def _filed_case(run: _Run) -> FiledCase | None:
    """The case half of the `filed` write, read now: None when there is no
    case to move (it is already filed, or gone); _CaseNotRecordedError when
    the confirmation cannot be written onto it."""
    deps = run.deps
    filing = run.filing
    assert filing is not None
    confirmation = filing.confirmation
    assert confirmation is not None
    case = deps.case_store.read_for_worker(filing.case_id)
    if case is None or case.deleted or is_filed(case.status):
        return None
    filed_on = petition_date(confirmation.filed_at)
    if (
        filed_on is None
        or parse_case_number(confirmation.case_number, require_office=True) is None
    ):
        logger.warning(
            "the court's confirmation cannot be recorded on the case",
            extra={"filing_id": filing.filing_id},
        )
        raise _CaseNotRecordedError
    try:
        after, change = file_case(
            case,
            case_number=confirmation.case_number,
            filed_at=filed_on,
            changed_by=FILING_WORKER_ACTOR,
            filing_id=filing.filing_id,
        )
    except ApiError as refused:
        raise _CaseNotRecordedError from refused
    return FiledCase(case=after, expected_status=case.status, status_change=change)
