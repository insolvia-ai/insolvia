"""The filing record and its state machine (ADR 0024, "Idempotency — a retry
never double-files").

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

`filed` here is the FILING's state. The CASE's `status=filed`, its court
case number and the pin freeze are ADR 0024 PR 8.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from dataclasses import dataclass, field
from typing import Final, Literal, get_args

from insolvia_core.cases import partition_key
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
    )


def filing_json(filing: Filing) -> dict[str, object]:
    """What a reader of the record sees (PR 8's status surface will serve
    this). Ids, states and the court's own facts — nothing secret."""
    body: dict[str, object] = {
        "filingId": filing.filing_id,
        "approvalId": filing.approval_id,
        "state": filing.state,
        "court": filing.court,
        "claimedAt": filing.claimed_at,
        "updatedAt": filing.updated_at,
        "history": [{"state": s.state, "at": s.at} for s in filing.history],
    }
    body.update(outcome_fields(filing))
    return body
