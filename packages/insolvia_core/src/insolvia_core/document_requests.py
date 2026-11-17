"""Document request checklists (ADR 0023 PR 5 / issue #364): what a firm asks
its clients for, and which of it each case has received.

Two records, at two scopes, and they are different kinds of fact:

THE CHECKLIST is the firm's — the documents it asks every client for. A
default ships in the code (`DEFAULT_CHECKLIST`: pay stubs, tax returns, bank
statements, vehicle titles, mortgage statements, identification — the
paperwork every consumer case needs). A firm may replace it with its own
list. STORAGE follows `questionnaire.py` exactly, for the same reasons: one
item per firm in the firm's own partition (`SK = DOCUMENT_CHECKLIST`), never
attributes on the hot META row; ABSENT MEANS THE DEFAULT, and reset deletes
the item rather than writing a copy, so a later improvement to the default
reaches every firm that never customised it. A save is the whole list.

A REQUEST is the case's — one document asked of this client, with a status:

    requested  asked for, nothing has arrived
    received   at least one upload against it completed (`document_ids`)
    waived     staff decided the case does not need it

Requests are COPIES of checklist entries, not references to them. A firm
editing its checklist next month must not rewrite what a client was already
asked for — the request is a record of what was asked, and when.

HOW A CASE GETS ITS REQUESTS: an explicit staff act — "Request the
checklist" on the case (`requests_from_checklist`), or one request at a time.
Not on case open, for three reasons: a case has more than one writer (the
API's route and the admin service's seeder), so a hook on one of them would
leave the other's cases without requests and nobody would notice; a firm that
does not use the portal would get a list of outstanding documents on every
case that means nothing; and every case opened before this feature can adopt
it the same way a new one does. Applying the checklist twice adds only the
entries the case does not already have (by title), so the act is safe to
repeat after the firm adds to its list.

`received` IS NEVER SET BY HAND. It is what a completed upload against the
request does (`satisfy`) — through the portal, or through the staff upload
route naming a `requestId`. A document handed over on paper is scanned and
uploaded against its request, or the request is waived; a status that says
"received" with no document behind it would be the progress bar asserting
something the case file cannot show.

NOTHING HERE IS CASE DATA. A request and the documents uploaded against it
are records of a transfer, not assertions about the debtor's affairs (see
`documents.py` on why a document carries no provenance). What a document
SAYS enters the case only through extraction and review, as ever.
"""

from __future__ import annotations

import uuid
from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass, replace
from typing import Final

from insolvia_core import cases, firms
from insolvia_core.documents import KINDS
from insolvia_core.errors import FieldValidationError, ValidationError

from .fields import narrative, text, timestamp

# ── The firm's checklist ────────────────────────────────────────────

# Beside firms.py's META/USER#, library_creditors' LIBCREDITOR# and
# questionnaire's QUESTIONNAIRE in the same partition. One per firm.
CHECKLIST_SORT_KEY: Final = "DOCUMENT_CHECKLIST"

# A consumer case's paperwork is a dozen kinds of document at the outside;
# forty is room for a firm that itemises (each account's statements, each
# vehicle) and small enough that a save is one bounded item.
MAX_CHECKLIST_ITEMS: Final = 40
MAX_TITLE: Final = 120
MAX_DESCRIPTION: Final = 1000


@dataclass(frozen=True)
class ChecklistItem:
    """One document a firm asks for. `kind` is the document kind an upload
    against it is filed under (`documents.KINDS`) — the client never chooses
    one — and `description` is what the client reads under the title."""

    title: str
    kind: str
    description: str | None = None


# In the order a debtor usually gathers them: what comes from an employer,
# the tax authority and the bank, then what is in a drawer at home.
DEFAULT_CHECKLIST: Final = (
    ChecklistItem(
        title="Pay stubs for the last six months",
        kind="pay_stub",
        description=(
            "Every pay stub from every job you and your spouse have had in the "
            "last six months. If you are paid in cash or are self-employed, a "
            "record of what you were paid and when."
        ),
    ),
    ChecklistItem(
        title="Tax returns for the last two years",
        kind="tax_return",
        description=(
            "Your federal and state tax returns for the last two years, with "
            "every schedule and form that was filed with them."
        ),
    ),
    ChecklistItem(
        title="Bank statements",
        kind="bank_statement",
        description=(
            "The last six months of statements for every checking, savings and "
            "other account you have, including accounts held jointly."
        ),
    ),
    ChecklistItem(
        title="Vehicle titles",
        kind="other",
        description=(
            "The title for each car, truck, motorcycle or other vehicle you own, "
            "and the latest statement for any loan on it."
        ),
    ),
    ChecklistItem(
        title="Mortgage statements",
        kind="other",
        description=(
            "The most recent statement for each mortgage or home-equity loan on "
            "property you own. If you rent, a copy of your lease."
        ),
    ),
    ChecklistItem(
        title="Identification",
        kind="identification",
        description=(
            "A government-issued photo ID, and a document showing your Social "
            "Security number."
        ),
    ),
)


@dataclass(frozen=True)
class DocumentChecklist:
    """A firm's stored checklist — the item that exists only once the firm
    has saved one. An EMPTY `items` is a stored choice ("we ask for nothing
    by default"), distinct from no item at all (the shipped default)."""

    firm_id: str
    items: tuple[ChecklistItem, ...]
    updated_at: str
    updated_by: str


def resolve_checklist(config: DocumentChecklist | None) -> tuple[ChecklistItem, ...]:
    return DEFAULT_CHECKLIST if config is None else config.items


def _parse_item(
    entry: object, path: str, errors: dict[str, str]
) -> ChecklistItem | None:
    if not isinstance(entry, Mapping):
        errors[path] = "Must be an object."
        return None
    title = text(entry.get("title"), f"{path}.title", errors, limit=MAX_TITLE)
    if title is None and f"{path}.title" not in errors:
        errors[f"{path}.title"] = "A title is required."
    kind = entry.get("kind")
    if not isinstance(kind, str) or kind not in KINDS:
        errors[f"{path}.kind"] = "Must be one of " + ", ".join(KINDS) + "."
        kind = None
    description = narrative(
        entry.get("description"), f"{path}.description", errors, limit=MAX_DESCRIPTION
    )
    if title is None or kind is None or f"{path}.description" in errors:
        return None
    return ChecklistItem(title=title, kind=kind, description=description)


def parse_checklist_update(payload: Mapping[str, object]) -> tuple[ChecklistItem, ...]:
    """Validate PUT `/v1/firm/document-checklist`: `{"items": [...]}`, each
    `{title, kind, description}`, the WHOLE list. Errors are keyed
    `items.<index>.<field>`. Two entries with the same title are refused —
    applying the checklist to a case dedupes by title, so a duplicate would
    be an entry that can never be requested."""
    raw = payload.get("items")
    if not isinstance(raw, list):
        raise FieldValidationError({"items": "Must be a list of documents."})
    if len(raw) > MAX_CHECKLIST_ITEMS:
        raise FieldValidationError(
            {"items": f"A checklist has at most {MAX_CHECKLIST_ITEMS} documents."}
        )
    errors: dict[str, str] = {}
    items: list[ChecklistItem] = []
    seen: set[str] = set()
    for index, entry in enumerate(raw):
        item = _parse_item(entry, f"items.{index}", errors)
        if item is None:
            continue
        if title_key(item.title) in seen:
            errors[f"items.{index}.title"] = "Another document has this title."
            continue
        seen.add(title_key(item.title))
        items.append(item)
    if errors:
        raise FieldValidationError(errors)
    return tuple(items)


def build_checklist(
    items: Sequence[ChecklistItem], *, firm_id: str, updated_by: str
) -> DocumentChecklist:
    return DocumentChecklist(
        firm_id=firm_id,
        items=tuple(items),
        updated_at=timestamp(),
        updated_by=updated_by,
    )


def _item_block(item: ChecklistItem) -> dict[str, object]:
    block: dict[str, object] = {"title": item.title, "kind": item.kind}
    if item.description is not None:
        block["description"] = item.description
    return block


def checklist_item(config: DocumentChecklist) -> dict[str, object]:
    """The exact stored item, shared by both FirmStore implementations.

    PK  FIRM#<firm_id>
    SK  DOCUMENT_CHECKLIST

    No GSI keys (`firms.firm_item` on why the by-subject index stays
    people-only). `description` is sparse.
    """
    return {
        "PK": firms.partition_key(config.firm_id),
        "SK": CHECKLIST_SORT_KEY,
        "firmId": config.firm_id,
        "items": [_item_block(item) for item in config.items],
        "updatedAt": config.updated_at,
        "updatedBy": config.updated_by,
    }


def checklist_from_item(item: Mapping[str, object]) -> DocumentChecklist:
    """Inverse of `checklist_item`. Raises ValidationError on a row this
    package did not write."""
    try:
        raw_items = item["items"]
        if not isinstance(raw_items, list):
            raise ValueError("items is not a list")
        parsed: list[ChecklistItem] = []
        for raw in raw_items:
            if not isinstance(raw, Mapping):
                raise ValueError("an item is not a map")
            description = raw.get("description")
            parsed.append(
                ChecklistItem(
                    title=str(raw["title"]),
                    kind=str(raw["kind"]),
                    description=str(description) if description is not None else None,
                )
            )
        return DocumentChecklist(
            firm_id=str(item["firmId"]),
            items=tuple(parsed),
            updated_at=str(item["updatedAt"]),
            updated_by=str(item["updatedBy"]),
        )
    except (KeyError, ValueError) as error:
        raise ValidationError(
            f"stored document checklist item is malformed: {error}"
        ) from error


def _checklist_item_json(item: ChecklistItem) -> dict[str, object]:
    return {"title": item.title, "kind": item.kind, "description": item.description}


def checklist_json(config: DocumentChecklist | None) -> dict[str, object]:
    """The firm's view (`/v1/firm/document-checklist`): the list in force,
    and the shipped default beside it so the screen can offer it back.
    `isDefault` says nothing is stored; `updatedAt`/`updatedBy` are explicit
    nulls then, `questionnaire_json`'s rule."""
    return {
        "isDefault": config is None,
        "updatedAt": config.updated_at if config is not None else None,
        "updatedBy": config.updated_by if config is not None else None,
        "items": [_checklist_item_json(i) for i in resolve_checklist(config)],
        "defaultItems": [_checklist_item_json(i) for i in DEFAULT_CHECKLIST],
    }


# ── A case's requests ───────────────────────────────────────────────

STATUS_REQUESTED: Final = "requested"
STATUS_RECEIVED: Final = "received"
STATUS_WAIVED: Final = "waived"
STATUSES: Final = (STATUS_REQUESTED, STATUS_RECEIVED, STATUS_WAIVED)

# What staff may set by hand — see the module docstring for why `received`
# is not one of them.
SETTABLE_STATUSES: Final = (STATUS_REQUESTED, STATUS_WAIVED)


@dataclass(frozen=True)
class DocumentRequest:
    """One document asked of a case's client.

    `document_ids` are the COMPLETED uploads against it, in the order they
    completed — a client may send a statement in three files. `received_at`
    is when the first one completed, and stays even if staff later reopen
    the request: it records when something arrived, not the current status.
    """

    id: str
    case_id: str
    title: str
    kind: str
    description: str | None
    status: str
    document_ids: tuple[str, ...]
    created_at: str
    created_by: str
    updated_at: str
    received_at: str | None = None


def title_key(title: str) -> str:
    """The identity two requests (or two checklist entries) share when they
    are the same document: the title, case- and whitespace-insensitive."""
    return " ".join(title.split()).casefold()


def parse_request_creation(payload: Mapping[str, object]) -> ChecklistItem:
    """Validate POST `/v1/cases/<case_id>/document-requests` — one ad-hoc
    request, the same three fields as a checklist entry."""
    errors: dict[str, str] = {}
    item = _parse_item(payload, "request", errors)
    if item is None or errors:
        # Keyed by field, not `request.<field>`: this body IS the entry.
        raise FieldValidationError(
            {key.removeprefix("request."): value for key, value in errors.items()}
        )
    return item


def parse_request_update(payload: Mapping[str, object]) -> str:
    """Validate PATCH `.../document-requests/<id>`: `{"status": ...}`, one of
    `SETTABLE_STATUSES`."""
    status = payload.get("status")
    if status == STATUS_RECEIVED:
        raise FieldValidationError(
            {
                "status": "A request is received when a document is uploaded "
                "against it, not by setting it."
            }
        )
    if not isinstance(status, str) or status not in SETTABLE_STATUSES:
        raise FieldValidationError(
            {"status": "Must be one of " + ", ".join(SETTABLE_STATUSES) + "."}
        )
    return status


def create_request(
    item: ChecklistItem, *, case_id: str, created_by: str
) -> DocumentRequest:
    now = timestamp()
    return DocumentRequest(
        id=str(uuid.uuid4()),
        case_id=case_id,
        title=item.title,
        kind=item.kind,
        description=item.description,
        status=STATUS_REQUESTED,
        document_ids=(),
        created_at=now,
        created_by=created_by,
        updated_at=now,
    )


def requests_from_checklist(
    checklist: Iterable[ChecklistItem],
    existing: Iterable[DocumentRequest],
    *,
    case_id: str,
    created_by: str,
) -> tuple[DocumentRequest, ...]:
    """The requests applying `checklist` to a case ADDS — every entry whose
    title the case has not already requested, in whatever status. Waived
    counts as already requested: staff decided against it once, and
    re-applying the checklist must not quietly ask again."""
    have = {title_key(request.title) for request in existing}
    added: list[DocumentRequest] = []
    for item in checklist:
        key = title_key(item.title)
        if key in have:
            continue
        have.add(key)
        added.append(create_request(item, case_id=case_id, created_by=created_by))
    return tuple(added)


def set_status(request: DocumentRequest, status: str) -> DocumentRequest:
    """Staff's hand: waive, or reopen. Reopening a request that has documents
    against it makes it `requested` again — "what you sent is not enough" —
    and keeps the documents; the next completed upload receives it again."""
    if status not in SETTABLE_STATUSES:
        raise ValueError(f"{status!r} is not settable by hand")
    return replace(request, status=status, updated_at=timestamp())


def satisfy(request: DocumentRequest, document_id: str) -> DocumentRequest:
    """A completed upload against the request. Idempotent: completing the
    same document twice records it once.

    A WAIVED request stays waived and still records the document: the upload
    was authorised before staff waived it (both create routes refuse a
    waived request), and the bytes are on the case either way. Staff's
    decision is not overturned by an upload racing it."""
    if document_id in request.document_ids:
        return request
    now = timestamp()
    return replace(
        request,
        status=STATUS_WAIVED if request.status == STATUS_WAIVED else STATUS_RECEIVED,
        document_ids=(*request.document_ids, document_id),
        received_at=request.received_at or now,
        updated_at=now,
    )


def accepts_uploads(request: DocumentRequest) -> bool:
    """Whether a new upload may be authorised against the request. A waived
    one is closed; a received one is not, because a second file for the same
    request (the other half of a statement) is ordinary."""
    return request.status != STATUS_WAIVED


@dataclass(frozen=True)
class Progress:
    """Arrived against outstanding, over the requests the case still needs.
    `total` excludes waived requests: a waived request is neither."""

    total: int
    received: int
    outstanding: int
    waived: int


def progress(requests: Iterable[DocumentRequest]) -> Progress:
    received = outstanding = waived = 0
    for request in requests:
        if request.status == STATUS_RECEIVED:
            received += 1
        elif request.status == STATUS_WAIVED:
            waived += 1
        else:
            outstanding += 1
    return Progress(
        total=received + outstanding,
        received=received,
        outstanding=outstanding,
        waived=waived,
    )


def progress_json(value: Progress) -> dict[str, int]:
    return {
        "total": value.total,
        "received": value.received,
        "outstanding": value.outstanding,
        "waived": value.waived,
    }


def sort_key(request_id: str) -> str:
    return f"DOCREQUEST#{request_id}"


def list_order(request: DocumentRequest) -> tuple[str, str]:
    """Creation order, tie-broken by id — the order the checklist listed
    them in, since one apply stamps them in sequence. The sort key is a
    random uuid, so both stores sort against this, as `tasks.list_order`."""
    return (request.created_at, request.id)


def request_item(request: DocumentRequest) -> dict[str, object]:
    """The exact stored item, shared by both DocumentRequestStore
    implementations.

    PK  CASE#<case_id>         the case's own partition, beside its documents
    SK  DOCREQUEST#<id>

    No GSI keys: a request is reached only through its case.
    """
    item: dict[str, object] = {
        "PK": cases.partition_key(request.case_id),
        "SK": sort_key(request.id),
        "id": request.id,
        "caseId": request.case_id,
        "title": request.title,
        "kind": request.kind,
        "status": request.status,
        "documentIds": list(request.document_ids),
        "createdAt": request.created_at,
        "createdBy": request.created_by,
        "updatedAt": request.updated_at,
    }
    if request.description is not None:
        item["description"] = request.description
    if request.received_at is not None:
        item["receivedAt"] = request.received_at
    return item


def request_from_item(item: Mapping[str, object]) -> DocumentRequest:
    try:
        raw_ids = item.get("documentIds", [])
        if not isinstance(raw_ids, list | tuple):
            raise ValueError("documentIds is not a list")
        status = str(item["status"])
        if status not in STATUSES:
            raise ValueError(f"unknown status {status!r}")
        description = item.get("description")
        received_at = item.get("receivedAt")
        return DocumentRequest(
            id=str(item["id"]),
            case_id=str(item["caseId"]),
            title=str(item["title"]),
            kind=str(item["kind"]),
            description=str(description) if description is not None else None,
            status=status,
            document_ids=tuple(str(i) for i in raw_ids),
            created_at=str(item["createdAt"]),
            created_by=str(item["createdBy"]),
            updated_at=str(item["updatedAt"]),
            received_at=str(received_at) if received_at is not None else None,
        )
    except (KeyError, ValueError) as error:
        raise ValidationError(
            f"stored document request item is malformed: {error}"
        ) from error


def request_json(request: DocumentRequest) -> dict[str, object]:
    """Staff's view. `createdBy` is omitted for `document_json`'s reason —
    a subject identifier in a body buys the screen nothing."""
    return {
        "id": request.id,
        "caseId": request.case_id,
        "title": request.title,
        "kind": request.kind,
        "description": request.description,
        "status": request.status,
        "documentIds": list(request.document_ids),
        "createdAt": request.created_at,
        "updatedAt": request.updated_at,
        "receivedAt": request.received_at,
    }


def requests_json(requests: Sequence[DocumentRequest]) -> dict[str, object]:
    """`GET /v1/cases/<case_id>/document-requests`: the requests and the
    case overview's progress, computed from the same list so they agree."""
    return {
        "requests": [request_json(r) for r in requests],
        "progress": progress_json(progress(requests)),
    }


def portal_request_json(
    request: DocumentRequest, own_uploads: Sequence[Mapping[str, object]]
) -> dict[str, object]:
    """The CLIENT's view of one request: what is asked, its status, and the
    client's OWN uploads against it (`documents.portal_document_json`, built
    by the route from documents it filtered to this client). No case id, no
    `documentIds` — those would name staff uploads too — and no timestamps
    about who on the firm's side did what."""
    return {
        "id": request.id,
        "title": request.title,
        "kind": request.kind,
        "description": request.description,
        "status": request.status,
        "uploads": list(own_uploads),
    }
