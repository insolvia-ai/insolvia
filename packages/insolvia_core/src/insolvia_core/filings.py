"""The filing record and its state machine (ADR 0024, "Idempotency — a retry
never double-files").

Written by TWO services, which is why it lives here (this package's
admission rule): the filing worker (services/filing) claims and advances it,
and the API reads it for the approval screen and records the attorney's
RESOLUTION of a hand-back on it (ADR 0024 PR 8). One shape, one owner.

One record per approved filing, in the case's own partition:

    PK  CASE#<case_id>
    SK  FILING#<filing_id>     the approval's filing id — one approval, one
                               filing, one record

The states are the ADR's, in its order. `approved` is the APPROVAL's
`pending` (services/api core/filing_approval.py) — the record does not exist
until a worker claims the job:

    claimed ─► signed_in ─► uploading ─► at_final_submit ─► submitted ─► filed
       │           │            │               │               │
       └───────────┴────────────┴─► handed_back └─► outcome_unknown ◄┘

- Everything before `at_final_submit` is safe to abandon: nothing reached
  the court's docket. Any stop there is `handed_back`, with a reason.
- `at_final_submit` is written DURABLY before the click that commits the
  filing. From that write on, nothing is ever retried: a crash, a timeout, a
  lost or unrecognisable response ends `outcome_unknown`, and a redelivered
  job that finds the mark ends `outcome_unknown` too. Somebody reconciles it
  against the court's own record before anything else happens.
- `submitted` is written with the court's confirmation (case number, time,
  docket entries) the moment it is read; `filed` follows once the receipt is
  stored with the case. `submitted` → `filed` touches no court, so it is the
  one step a redelivery may finish.
- `handed_back`, `outcome_unknown` and `filed` are terminal.

EVERY TRANSITION IS A CONDITIONAL WRITE on (state, attempt): the attempt
that claimed the record is the only one that advances it, and any other
actor — a redelivered message finding a dead attempt — can only end it, and
only from the state it read. Two consumers can never both submit.

`filed` here is the FILING's state. The CASE's `status=filed` is written in
the SAME transaction as the filing's own move to `filed` (`FiledCase`, the
store's `transition`), so the two cannot disagree.

## Resolving a hand-back (ADR 0024 PR 8)

`handed_back` and `outcome_unknown` end the WORKER's part; the attorney
then says what became of the filing, once (`Resolution`, written by the
API's core/filing_outcome.py, conditional on the record having none):

- `filed`      "I filed it" (or "the court has it") — the case moves to
               `filed` with the case number and petition date they read on
               the docket, in the same transaction as the resolution;
- `not_filed`  "the court has no such case" — the case is free for a new
               approval. On `outcome_unknown` this is refused while the
               attempt could still be live, and whenever the worker captured
               a confirmation (the court said it docketed it).

The resolution never changes `state`: what the worker did and what the
attorney found are two facts, and the record keeps both.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from dataclasses import dataclass, field
from typing import Final, Literal, get_args

from insolvia_core.cases import Case, StatusChange, partition_key
from insolvia_core.errors import ValidationError

FilingState = Literal[
    "claimed",
    "signed_in",
    "uploading",
    "at_final_submit",
    "submitted",
    "filed",
    "handed_back",
    "outcome_unknown",
]
STATES: Final = get_args(FilingState)
TERMINAL: Final = frozenset({"filed", "handed_back", "outcome_unknown"})
# Before the mark: abandoning here cannot have reached the docket.
BEFORE_SUBMIT: Final = frozenset({"claimed", "signed_in", "uploading"})

TRANSITIONS: Final[Mapping[str, frozenset[str]]] = {
    "claimed": frozenset({"signed_in", "handed_back"}),
    "signed_in": frozenset({"uploading", "handed_back"}),
    "uploading": frozenset({"at_final_submit", "handed_back"}),
    "at_final_submit": frozenset({"submitted", "outcome_unknown"}),
    "submitted": frozenset({"filed", "outcome_unknown"}),
    "filed": frozenset(),
    "handed_back": frozenset(),
    "outcome_unknown": frozenset(),
}

_SORT_PREFIX: Final = "FILING#"


def sort_key(filing_id: str) -> str:
    return f"{_SORT_PREFIX}{filing_id}"


def may_transition(current: str, target: str) -> bool:
    return target in TRANSITIONS.get(current, frozenset())


@dataclass(frozen=True)
class Step:
    """One entry of the record's history: the state entered and when."""

    state: str
    at: str


@dataclass(frozen=True)
class HandBackNote:
    """Why the run stopped and what the attorney does now. `stage` is the
    state it stopped in; `court_said` is the court's own message where there
    was one (a validation text, never a page dump); `link` is the app segment
    under /cases/<id>/ — the filing-set checklist (ADR 0024 PR 3), every
    hand-back's landing page."""

    reason: str
    stage: str
    title: str
    action: str
    court_said: str | None = None
    link: str = "packet"


@dataclass(frozen=True)
class Confirmation:
    """What the court's confirmation screen said, captured the moment it was
    read. `page_ref` is the stored page itself (a blob under the case)."""

    case_number: str
    filed_at: str
    docket_entries: tuple[str, ...]
    receipt_number: str | None = None
    fee_due: str | None = None
    page_ref: str | None = None


ResolutionOutcome = Literal["filed", "not_filed"]
RESOLUTION_OUTCOMES: Final = get_args(ResolutionOutcome)
# The states a person resolves: everything else is the worker's to finish.
RESOLVABLE: Final = frozenset({"handed_back", "outcome_unknown"})


@dataclass(frozen=True)
class Resolution:
    """The attorney's answer to a hand-back: what the court's docket shows.

    `docket_checked_at` is when they confirmed they checked the court's own
    docket — required for either outcome, and the reason the record can be
    trusted to free (or file) the case. `case_number`, `filed_at` (a form
    date — the petition date) and `confirmation_document_id` (an uploaded
    case document, optional) are the `filed` outcome's."""

    outcome: ResolutionOutcome
    resolved_by: str
    resolved_at: str
    docket_checked_at: str
    case_number: str | None = None
    filed_at: str | None = None
    confirmation_document_id: str | None = None


@dataclass(frozen=True)
class Filing:
    filing_id: str
    case_id: str
    approval_id: str
    firm_id: str
    attorney_id: str
    credential_id: str
    court: str
    division: str
    state: FilingState
    attempt_id: str
    claimed_at: str
    lease_expires_at: int
    updated_at: str
    history: tuple[Step, ...] = ()
    driver: str | None = None
    hand_back: HandBackNote | None = None
    # outcome_unknown's reason; the action is always "reconcile".
    unknown_reason: str | None = None
    confirmation: Confirmation | None = None
    receipt_document_id: str | None = None
    # What the attorney still does in their own session after a filing that
    # went in: pay the fee (always, until ADR 0024 PR 11), and file any
    # document prepared outside Insolvia that the court takes as its own event.
    follow_ups: tuple[str, ...] = field(default_factory=tuple)
    resolution: Resolution | None = None


def is_resolvable(filing: Filing) -> bool:
    """Whether a person may still resolve this filing."""
    return filing.state in RESOLVABLE and filing.resolution is None


def frees_case(filing: Filing) -> bool:
    """Whether this filing no longer stands in the way of a new approval:
    the attorney resolved it as never having reached the court. The ONE
    question `approve_filing` asks of a consumed approval's filing."""
    return (
        filing.state in RESOLVABLE
        and filing.resolution is not None
        and filing.resolution.outcome == "not_filed"
    )


@dataclass(frozen=True)
class FiledCase:
    """The case half of a write that files it: the case as it will be
    stored, the status it must still have, and the history row — written in
    the same transaction as the filing record (`FilingStore.transition` /
    `resolve`), so the case is `filed` exactly when its filing says so."""

    case: Case
    expected_status: str
    status_change: StatusChange


# ── Item shapes (one owner: both stores use these) ──────────────


def _hand_back_item(note: HandBackNote) -> dict[str, object]:
    item: dict[str, object] = {
        "reason": note.reason,
        "stage": note.stage,
        "title": note.title,
        "action": note.action,
        "link": note.link,
    }
    if note.court_said is not None:
        item["courtSaid"] = note.court_said
    return item


def _confirmation_item(confirmation: Confirmation) -> dict[str, object]:
    item: dict[str, object] = {
        "caseNumber": confirmation.case_number,
        "filedAt": confirmation.filed_at,
        "docketEntries": list(confirmation.docket_entries),
    }
    for key, value in (
        ("receiptNumber", confirmation.receipt_number),
        ("feeDue", confirmation.fee_due),
        ("pageRef", confirmation.page_ref),
    ):
        if value is not None:
            item[key] = value
    return item


def _resolution_item(resolution: Resolution) -> dict[str, object]:
    item: dict[str, object] = {
        "outcome": resolution.outcome,
        "resolvedBy": resolution.resolved_by,
        "resolvedAt": resolution.resolved_at,
        "docketCheckedAt": resolution.docket_checked_at,
    }
    for key, value in (
        ("caseNumber", resolution.case_number),
        ("filedAt", resolution.filed_at),
        ("confirmationDocumentId", resolution.confirmation_document_id),
    ):
        if value is not None:
            item[key] = value
    return item


def outcome_fields(filing: Filing) -> dict[str, object]:
    """The optional members a transition may set, as stored attributes."""
    item: dict[str, object] = {}
    if filing.driver is not None:
        item["driver"] = filing.driver
    if filing.hand_back is not None:
        item["handBack"] = _hand_back_item(filing.hand_back)
    if filing.unknown_reason is not None:
        item["unknownReason"] = filing.unknown_reason
    if filing.confirmation is not None:
        item["confirmation"] = _confirmation_item(filing.confirmation)
    if filing.receipt_document_id is not None:
        item["receiptDocumentId"] = filing.receipt_document_id
    if filing.follow_ups:
        item["followUps"] = list(filing.follow_ups)
    if filing.resolution is not None:
        item["resolution"] = _resolution_item(filing.resolution)
    return item


def filing_item(filing: Filing) -> dict[str, object]:
    item: dict[str, object] = {
        "PK": partition_key(filing.case_id),
        "SK": sort_key(filing.filing_id),
        "filingId": filing.filing_id,
        "caseId": filing.case_id,
        "approvalId": filing.approval_id,
        "firmId": filing.firm_id,
        "attorneyId": filing.attorney_id,
        "credentialId": filing.credential_id,
        "court": filing.court,
        "division": filing.division,
        "state": filing.state,
        "attemptId": filing.attempt_id,
        "claimedAt": filing.claimed_at,
        "leaseExpiresAt": filing.lease_expires_at,
        "updatedAt": filing.updated_at,
        "history": [{"state": s.state, "at": s.at} for s in filing.history],
    }
    item.update(outcome_fields(filing))
    return item


def _text(item: Mapping[str, object], key: str) -> str:
    value = item.get(key)
    if not isinstance(value, str) or not value:
        raise ValidationError(f"stored filing is missing {key}")
    return value


def _optional(item: Mapping[str, object], key: str) -> str | None:
    value = item.get(key)
    return value if isinstance(value, str) else None


def _strings(value: object) -> tuple[str, ...]:
    if not isinstance(value, Sequence) or isinstance(value, str):
        return ()
    return tuple(str(entry) for entry in value)


def filing_from_item(item: Mapping[str, object]) -> Filing:
    state = _text(item, "state")
    if state not in STATES:
        raise ValidationError(f"stored filing has state {state!r}")
    raw_hand_back = item.get("handBack")
    hand_back = None
    if isinstance(raw_hand_back, Mapping):
        hand_back = HandBackNote(
            reason=_text(raw_hand_back, "reason"),
            stage=_text(raw_hand_back, "stage"),
            title=_text(raw_hand_back, "title"),
            action=_text(raw_hand_back, "action"),
            court_said=_optional(raw_hand_back, "courtSaid"),
            link=_optional(raw_hand_back, "link") or "packet",
        )
    raw_confirmation = item.get("confirmation")
    confirmation = None
    if isinstance(raw_confirmation, Mapping):
        confirmation = Confirmation(
            case_number=_text(raw_confirmation, "caseNumber"),
            filed_at=_text(raw_confirmation, "filedAt"),
            docket_entries=_strings(raw_confirmation.get("docketEntries")),
            receipt_number=_optional(raw_confirmation, "receiptNumber"),
            fee_due=_optional(raw_confirmation, "feeDue"),
            page_ref=_optional(raw_confirmation, "pageRef"),
        )
    raw_resolution = item.get("resolution")
    resolution = None
    if isinstance(raw_resolution, Mapping):
        outcome = _text(raw_resolution, "outcome")
        if outcome not in RESOLUTION_OUTCOMES:
            raise ValidationError(f"stored filing has resolution {outcome!r}")
        resolution = Resolution(
            outcome=outcome,  # type: ignore[arg-type]
            resolved_by=_text(raw_resolution, "resolvedBy"),
            resolved_at=_text(raw_resolution, "resolvedAt"),
            docket_checked_at=_text(raw_resolution, "docketCheckedAt"),
            case_number=_optional(raw_resolution, "caseNumber"),
            filed_at=_optional(raw_resolution, "filedAt"),
            confirmation_document_id=_optional(
                raw_resolution, "confirmationDocumentId"
            ),
        )
    history_raw = item.get("history")
    history: list[Step] = []
    if isinstance(history_raw, Sequence) and not isinstance(history_raw, str):
        for entry in history_raw:
            if isinstance(entry, Mapping):
                history.append(Step(state=_text(entry, "state"), at=_text(entry, "at")))
    lease = item.get("leaseExpiresAt")
    if isinstance(lease, bool) or not isinstance(lease, int | float | str):
        raise ValidationError("stored filing is missing leaseExpiresAt")
    return Filing(
        filing_id=_text(item, "filingId"),
        case_id=_text(item, "caseId"),
        approval_id=_text(item, "approvalId"),
        firm_id=_text(item, "firmId"),
        attorney_id=_text(item, "attorneyId"),
        credential_id=_text(item, "credentialId"),
        court=_text(item, "court"),
        division=_optional(item, "division") or "",
        state=state,  # type: ignore[arg-type]
        attempt_id=_text(item, "attemptId"),
        claimed_at=_text(item, "claimedAt"),
        lease_expires_at=int(lease),
        updated_at=_text(item, "updatedAt"),
        history=tuple(history),
        driver=_optional(item, "driver"),
        hand_back=hand_back,
        unknown_reason=_optional(item, "unknownReason"),
        confirmation=confirmation,
        receipt_document_id=_optional(item, "receiptDocumentId"),
        follow_ups=_strings(item.get("followUps")),
        resolution=resolution,
    )


def filing_json(filing: Filing) -> dict[str, object]:
    """What a reader of the record sees — the approval screen's `filing`
    (services/api routes/filing_approval.py). Ids, states, the court's own
    facts and the resolution — nothing secret. `pageRef` (a storage key) is
    dropped: the page is not served, and a key is not a client's business.
    `resolvable` says whether the attorney may still resolve it."""
    body: dict[str, object] = {
        "filingId": filing.filing_id,
        "approvalId": filing.approval_id,
        "attorneyId": filing.attorney_id,
        "state": filing.state,
        "court": filing.court,
        "claimedAt": filing.claimed_at,
        "updatedAt": filing.updated_at,
        "history": [{"state": s.state, "at": s.at} for s in filing.history],
        "resolvable": is_resolvable(filing),
    }
    body.update(outcome_fields(filing))
    confirmation = body.get("confirmation")
    if isinstance(confirmation, dict):
        confirmation.pop("pageRef", None)
    return body
