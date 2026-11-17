"""The attorney's answer to a hand-back (ADR 0024 PR 8, filed-state capture).

When the filing worker stops — `handed_back` before the final submit, or
`outcome_unknown` after it — the case is in limbo: its approval is consumed,
so `approve_filing` refuses a second one ("a second approval would be a
second filing"), and nothing says whether the court has the case. This
module is the ONE way out, and it is the attorney's word about the court's
own docket, recorded on the filing (`insolvia_core.filings.Resolution`):

- `filed`      "the court has it" — they filed it in their own CM/ECF
               session from the checklist, or the worker's submit went in
               after all. They give the case number and the petition date
               they read on the docket, optionally with the court's notice
               uploaded as a case document. The CASE moves to `filed` in the
               SAME transaction as the resolution, with a history row naming
               the filing (`cases.file_case`, the lifecycle's own move).
- `not_filed`  "the court has no such case" — the case is free for a new
               approval (`filings.frees_case`, which `approve_filing` asks).

Either way the attorney must confirm they checked the court's docket
(`docket_checked: true`), and the record says when.

## Why `not_filed` cannot cause a double filing

A wrong "not filed" on a filing the court has is the one way this module
could put a second petition on the docket. Four things stand between the
two:

1. A CONFIRMATION WAS CAPTURED → refused outright. When the worker read the
   court's confirmation (it then ended `outcome_unknown` only because the
   receipt or the case could not be recorded), the court has said, in its
   own words, that it docketed the case; nobody's recollection overrides
   that. Only `filed` is accepted, and its number must be the court's.
2. THE ATTEMPT COULD STILL BE LIVE → refused until the claimed attempt's
   lease has run out (`lease_expires_at`, the Lambda's own timeout). A
   redelivery marks a filing `outcome_unknown` the moment it finds the
   `at_final_submit` mark, which can be while the first attempt is still
   waiting on the court's answer; a docket checked in that window can be
   empty and wrong a second later.
3. THE ATTORNEY CHECKED THE DOCKET, and says so (`docket_checked`) — the
   instruction every `outcome_unknown` note gives is to look the debtor up
   on the court's own case query first. The confirmation is recorded with
   who and when, in the record and in the access log.
4. A NEW APPROVAL IS STILL A NEW APPROVAL. `not_filed` files nothing: the
   next filing needs the attorney to review the filing set and approve it
   again after a fresh sign-in, under a new filing id the worker has never
   seen — an old job redelivered finds its own (terminal) record and stops.

Who: ONLY THE ATTORNEY WHOSE LOGIN THE FILING USED (`filing.attorney_id`,
the approver) — the person whose court account the docket would show, and
the person the authorization made answerable for it. No fresh sign-in: a
resolution submits nothing to any court, and the act that could (the next
approval) already demands one. A `filed` resolution moves the case through
the lifecycle exactly as a person's PATCH would, so it is gated on the same
`cases` add/edit permission at the route.

A resolution is written once — conditional on the filing still being in the
state it was read in and having none (`FilingStore.resolve`).
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, replace
from datetime import UTC, date, datetime, timedelta
from typing import TYPE_CHECKING, Final

from insolvia_core.access_log import record_access
from insolvia_core.case_numbers import case_number_key, parse_case_number
from insolvia_core.cases import file_case, is_filed
from insolvia_core.documents import STATUS_STORED
from insolvia_core.errors import (
    ConflictError,
    FieldValidationError,
    ForbiddenError,
    ValidationError,
)
from insolvia_core.filings import (
    RESOLUTION_OUTCOMES,
    FiledCase,
    Filing,
    Resolution,
    ResolutionOutcome,
    is_resolvable,
)

if TYPE_CHECKING:
    from insolvia_core.cases import Case
    from insolvia_core.ports import AccessLog, DocumentStore, FilingStore

ALLOWED_KEYS: Final = frozenset(
    {"outcome", "docket_checked", "case_number", "filed_at", "confirmation_document_id"}
)
# The petition date may be a day either side of the clock's: the court's
# local date and UTC differ by up to one.
_DATE_SLACK: Final = timedelta(days=1)


@dataclass(frozen=True)
class ResolutionRequest:
    outcome: ResolutionOutcome
    case_number: str | None = None
    filed_at: str | None = None
    confirmation_document_id: str | None = None


class ResolutionRefusedError(ConflictError):
    """The filing cannot be resolved this way (409), with the reason."""

    def __init__(self, reason: str, message: str) -> None:
        super().__init__(message)
        self.reason = reason


class NotTheFilingAttorneyError(ForbiddenError):
    """Only the attorney whose login the filing used may resolve it."""


def _form_date(value: object) -> date | None:
    if not isinstance(value, str):
        return None
    try:
        parsed = datetime.strptime(value.strip(), "%Y-%m-%d").date()
    except ValueError:
        return None
    return parsed if parsed.isoformat() == value.strip() else None


def parse_resolution(payload: object) -> ResolutionRequest:
    """`POST .../filings/<id>/resolution`'s body: `{outcome, docket_checked,
    case_number?, filed_at?, confirmation_document_id?}`."""
    if not isinstance(payload, Mapping):
        raise ValidationError("request body must be a JSON object")
    unknown = set(payload) - ALLOWED_KEYS
    if unknown:
        raise ValidationError("unsupported fields: " + ", ".join(sorted(unknown)))
    errors: dict[str, str] = {}
    outcome = payload.get("outcome")
    if outcome not in RESOLUTION_OUTCOMES:
        errors["outcome"] = "Outcome must be filed or not_filed."
    if payload.get("docket_checked") is not True:
        errors["docket_checked"] = (
            "Confirm you checked the court's own docket for this debtor."
        )
    case_number: str | None = None
    filed_at: str | None = None
    document_id: str | None = None
    if outcome == "filed":
        raw_number = payload.get("case_number")
        if not isinstance(raw_number, str) or not raw_number.strip():
            errors["case_number"] = "Enter the case number the court's docket shows."
        elif len(raw_number) > 40 or (
            parse_case_number(raw_number, require_office=True) is None
        ):
            errors["case_number"] = (
                "Enter the case number as the docket prints it, e.g. 6:26-bk-10000."
            )
        else:
            case_number = raw_number.strip()
        parsed_date = _form_date(payload.get("filed_at"))
        if parsed_date is None:
            errors["filed_at"] = "Enter the petition date in YYYY-MM-DD form."
        else:
            filed_at = parsed_date.isoformat()
        raw_document = payload.get("confirmation_document_id")
        if raw_document is not None:
            if not isinstance(raw_document, str) or not raw_document:
                errors["confirmation_document_id"] = "Must be a document id."
            else:
                document_id = raw_document
    elif outcome == "not_filed":
        for key in ("case_number", "filed_at", "confirmation_document_id"):
            if payload.get(key) is not None:
                errors[key] = "A filing that is not on the docket has no " + (
                    "case number." if key == "case_number" else "filing details."
                )
    if errors:
        raise FieldValidationError(errors)
    return ResolutionRequest(
        outcome=outcome,  # type: ignore[arg-type]
        case_number=case_number,
        filed_at=filed_at,
        confirmation_document_id=document_id,
    )


def _iso(moment: float) -> str:
    return (
        datetime.fromtimestamp(moment, UTC)
        .isoformat(timespec="milliseconds")
        .replace("+00:00", "Z")
    )


def resolve_filing(
    request: ResolutionRequest,
    *,
    filing: Filing,
    case: Case,
    principal: str,
    now: float,
    filings: FilingStore,
    documents: DocumentStore,
    access_log: AccessLog,
) -> Filing:
    """Record the attorney's resolution of `filing`, and file `case` with it
    when the outcome is `filed`. Refuses, in order — each a denied
    `filing.resolve` row naming the refusal:

    1. a caller who is not the filing's attorney (403);
    2. a filing that is not a hand-back or an unknown outcome, or is
       already resolved (409 `not_resolvable`);
    3. `not_filed` when the worker captured the court's confirmation (409
       `court_confirmed`), when the attempt's lease has not run out (409
       `attempt_may_be_live`), or when the case is already filed (409
       `case_filed`);
    4. `filed` with a number other than the court's confirmation, a
       petition date in the future or before the filing was claimed, or a
       confirmation document that is not a stored document of this case
       (400, keyed to the field).
    """

    def refuse(reason: str) -> None:
        access_log.record(
            record_access(
                case_id=case.id,
                principal=principal,
                action="filing.resolve",
                outcome="denied",
                purpose=reason,
                filing_id=filing.filing_id,
            )
        )

    if principal != filing.attorney_id:
        refuse("not_the_attorney")
        raise NotTheFilingAttorneyError(
            "Only the attorney whose court login this filing used can record"
            " what became of it."
        )
    if not is_resolvable(filing):
        refuse("not_resolvable")
        raise ResolutionRefusedError(
            "not_resolvable",
            "This filing's outcome is already recorded."
            if filing.resolution is not None
            else "Only a filing that was handed back, or whose outcome is"
            " unknown, can be resolved.",
        )
    confirmation = filing.confirmation
    if request.outcome == "not_filed":
        if confirmation is not None:
            refuse("court_confirmed")
            raise ResolutionRefusedError(
                "court_confirmed",
                "The court confirmed this filing as case"
                f" {confirmation.case_number}. Record it as filed.",
            )
        if filing.state == "outcome_unknown" and now < filing.lease_expires_at:
            refuse("attempt_may_be_live")
            raise ResolutionRefusedError(
                "attempt_may_be_live",
                "The filing attempt may still be waiting on the court. Check"
                " the docket again after " + _iso(filing.lease_expires_at) + ".",
            )
        if is_filed(case.status):
            refuse("case_filed")
            raise ResolutionRefusedError(
                "case_filed", "This case is already recorded as filed."
            )
    errors: dict[str, str] = {}
    if request.outcome == "filed":
        assert request.case_number is not None
        assert request.filed_at is not None
        if confirmation is not None and case_number_key(
            filing.court, confirmation.case_number
        ) not in (None, case_number_key(filing.court, request.case_number)):
            errors["case_number"] = (
                "The court confirmed this filing as case"
                f" {confirmation.case_number}; enter that number."
            )
        petition = date.fromisoformat(request.filed_at)
        today = datetime.fromtimestamp(now, UTC).date()
        claimed = datetime.fromisoformat(filing.claimed_at.replace("Z", "+00:00"))
        if petition > today + _DATE_SLACK:
            errors["filed_at"] = "The petition date cannot be in the future."
        elif petition < claimed.date() - _DATE_SLACK:
            errors["filed_at"] = (
                "The petition date cannot be before this filing was approved."
            )
        if request.confirmation_document_id is not None:
            document = documents.get(case.id, request.confirmation_document_id)
            if document is None or document.status != STATUS_STORED:
                errors["confirmation_document_id"] = (
                    "That is not an uploaded document of this case."
                )
    if errors:
        refuse("invalid")
        raise FieldValidationError(errors)

    at = _iso(now)
    resolved = replace(
        filing,
        updated_at=at,
        resolution=Resolution(
            outcome=request.outcome,
            resolved_by=principal,
            resolved_at=at,
            docket_checked_at=at,
            case_number=request.case_number,
            filed_at=request.filed_at,
            confirmation_document_id=request.confirmation_document_id,
        ),
    )
    filed_case: FiledCase | None = None
    if request.outcome == "filed" and not is_filed(case.status):
        assert request.case_number is not None
        assert request.filed_at is not None
        after, change = file_case(
            case,
            case_number=request.case_number,
            filed_at=request.filed_at,
            changed_by=principal,
            filing_id=filing.filing_id,
        )
        filed_case = FiledCase(
            case=after, expected_status=case.status, status_change=change
        )
    if not filings.resolve(
        resolved, expected_state=filing.state, filed_case=filed_case
    ):
        refuse("changed")
        raise ConflictError(
            "The filing or the case changed while this was being recorded —"
            " reload it and try again."
        )
    access_log.record(
        record_access(
            case_id=case.id,
            principal=principal,
            action="filing.resolve",
            purpose=request.outcome,
            filing_id=filing.filing_id,
        )
    )
    return resolved
