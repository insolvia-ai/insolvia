"""Case records: validation, identity, and the stored item shape.

The first product entity (issue 8.3). The logical model this implements is
docs/reference/case-data-model.md — but only its `case` root entity. Debtors,
claims, assets and the rest are separate items under the same partition and
arrive with the intake work; nothing here forecloses them.

Everything in this module is pure: no Flask, no boto3, no clock beyond
datetime.now. The item shape lives here rather than in an adapter so the
DynamoDB and in-memory stores cannot drift apart, exactly as core/waitlist.py
does for the waitlist table.
"""

from __future__ import annotations

import base64
import binascii
import json
import re
import uuid
from collections.abc import Mapping
from dataclasses import dataclass, replace
from datetime import UTC, datetime

from insolvia_core.errors import FieldValidationError, ValidationError

# The chapters an individual debtor can file under. 9 and 15 are municipal and
# cross-border and will never appear in this product.
CHAPTERS = (7, 11, 12, 13)

# Lifecycle, deliberately small. "filed" is terminal for this issue — what
# happens after filing is the forms milestone's problem.
STATUSES = ("intake", "ready_to_file", "filed")

# A bankruptcy court district identifier. Kept loose on purpose: the
# authoritative list belongs to the forms/e-filing work, which will have the
# CM/ECF court codes, and inventing a half-list here would be a constraint the
# next person has to unpick rather than a validation anyone wanted.
_DISTRICT_RE = re.compile(r"^[A-Za-z0-9 .\-/]{2,64}$")

MAX_LIST_LIMIT = 100
DEFAULT_LIST_LIMIT = 50


@dataclass(frozen=True)
class Case:
    """A case record — the META item of its partition.

    A CASE BELONGS TO A FIRM, not to whoever opened it. `owner_principal` used
    to be a Cognito `sub` and that was the single-user assumption in its purest
    form: a colleague at the same firm got a 404 on their own firm's matter,
    and two people could not work one case at all.

    `created_by` keeps the individual, and keeps it as what it actually is — an
    audit fact about who opened the matter, not a permission. It grants nothing
    on its own; the creator reaches the case through the assignment written
    alongside it (see `create_case`), exactly as any other colleague does.
    """

    id: str
    firm_id: str
    created_by: str
    chapter: int
    district: str
    status: str
    created_at: str
    updated_at: str
    # THE PINS (docs/reference/effective-dating.md, "Float, then pin").
    # None until packet assembly runs: a floating case resolves every
    # regulatory series as of today, and nothing is recorded. Packet assembly
    # (issue #96) is the ONE writer: it resolves once and records what it
    # used, in the same operation that stores the packet, so "what forms did
    # this filing use" is answerable forever. RE-assembly before filing
    # re-pins; a filed case never re-resolves.
    #
    # `form_revisions` maps form series id -> `effective_date[+sequence]`
    # (FormRelease.pin). `constants_set_id` is the pinned release id of the
    # `code/dollar-amounts` series (issue #99) — assembly resolves it as of
    # the same date as the forms and records both in one write.
    form_revisions: Mapping[str, str] | None = None
    constants_set_id: str | None = None


@dataclass(frozen=True)
class CaseAssignment:
    """One person linked to one case — MyCase's "linked to a case".

    An item in the CASE's partition rather than a list on the case record, and
    that is the design rather than an implementation detail: the row IS the
    entry in the `by-assignee` index, so linking somebody to a matter and
    making it appear in their listing are a single write. A list attribute
    would need a second write to an index that could then disagree with it.
    """

    case_id: str
    subject: str
    # Denormalised from the case, and it has to be: GSI2SK is
    # "<created_at>#<case_id>", so an assignment must carry the CASE's creation
    # time to sort into the same order the firm-wide listing uses. Its own
    # timestamp would order somebody's list by when they were added to matters
    # rather than by when the matters were opened.
    case_created_at: str
    assigned_at: str
    assigned_by: str


@dataclass(frozen=True)
class CaseDraft:
    """A validated creation request, before server-generated identity."""

    chapter: int
    district: str


@dataclass(frozen=True)
class CaseChanges:
    """A validated PATCH body. None means "leave unchanged" — which is why
    these are Optional rather than defaulted: a client that omits `district`
    must not silently blank it."""

    chapter: int | None = None
    district: str | None = None
    status: str | None = None


@dataclass(frozen=True)
class CasePage:
    """One page of the cases a caller may see, newest first."""

    cases: tuple[Case, ...]
    next_cursor: str | None


def _timestamp() -> str:
    """Microsecond-precision UTC with a literal Z.

    Fixed width and Z-suffixed is not cosmetic: GSI1SK sorts
    lexicographically, so a "+00:00" offset is the same instant that sorts
    wrong and silently misorders GET /v1/cases. The infra module's comment
    says the same thing from the other side.

    MICROseconds rather than the milliseconds core/waitlist.py uses, because
    the two tables order differently. GSI1SK is "<createdAt>#<id>" and the id
    is a random uuid4, so two cases sharing a timestamp sort arbitrarily
    rather than by creation order. Milliseconds made that reachable by two
    ordinary back-to-back requests; microseconds make it vanishingly
    unlikely. It remains a tie-break, not a guarantee — if strict total
    ordering is ever needed, the id has to become sortable, and that is a
    schema change rather than a formatting one.
    """
    return datetime.now(UTC).isoformat(timespec="microseconds").replace("+00:00", "Z")


def _parse_chapter(value: object, errors: dict[str, str]) -> int | None:
    # bool is an int in Python, and `True` would otherwise validate as
    # chapter 1 and fail the membership test with a confusing message.
    if isinstance(value, bool) or not isinstance(value, int):
        errors["chapter"] = "Chapter must be a number."
        return None
    if value not in CHAPTERS:
        errors["chapter"] = (
            "Chapter must be one of " + ", ".join(str(c) for c in CHAPTERS) + "."
        )
        return None
    return value


def _parse_district(value: object, errors: dict[str, str]) -> str | None:
    if not isinstance(value, str) or not value.strip():
        errors["district"] = "A filing district is required."
        return None
    district = value.strip()
    if not _DISTRICT_RE.match(district):
        errors["district"] = "That doesn't look like a district identifier."
        return None
    return district


def _parse_status(value: object, errors: dict[str, str]) -> str | None:
    if not isinstance(value, str) or value not in STATUSES:
        errors["status"] = "Status must be one of " + ", ".join(STATUSES) + "."
        return None
    return value


def parse_case_creation(payload: Mapping[str, object]) -> CaseDraft:
    """Validate POST /v1/cases. Unknown keys are ignored.

    Status is deliberately NOT accepted here: every case starts at "intake",
    and letting a client create one already marked "filed" would be a lie the
    server told on its behalf.
    """
    errors: dict[str, str] = {}
    chapter = _parse_chapter(payload.get("chapter"), errors)
    district = _parse_district(payload.get("district"), errors)
    # The None checks are redundant with `errors` but they are what narrows
    # the types, and a redundant guard beats an assert that a future -O strips.
    if errors or chapter is None or district is None:
        raise FieldValidationError(errors)
    return CaseDraft(chapter=chapter, district=district)


def parse_case_update(payload: Mapping[str, object]) -> CaseChanges:
    """Validate PATCH /v1/cases/<id>.

    Only supplied keys are validated, so a caller changing the district alone
    is not forced to resend the chapter. An empty body is rejected rather than
    treated as a no-op — it is far more likely to be a client bug than an
    intent, and a silent 200 would hide it.
    """
    errors: dict[str, str] = {}
    changes: dict[str, object] = {}

    if "chapter" in payload:
        chapter = _parse_chapter(payload["chapter"], errors)
        if chapter is not None:
            changes["chapter"] = chapter
    if "district" in payload:
        district = _parse_district(payload["district"], errors)
        if district is not None:
            changes["district"] = district
    if "status" in payload:
        status = _parse_status(payload["status"], errors)
        if status is not None:
            changes["status"] = status

    if errors:
        raise FieldValidationError(errors)
    if not changes:
        raise ValidationError("no supported fields to update")
    return CaseChanges(**changes)  # type: ignore[arg-type]


def create_case(
    draft: CaseDraft, *, firm_id: str, created_by: str
) -> tuple[Case, CaseAssignment]:
    """Stamp a draft with server-generated identity, and link its creator.

    Both keys come from the caller's resolved accessor, never from the request
    body — a client cannot create a case in another firm because it has no way
    to say so.

    THE ASSIGNMENT IS NOT OPTIONAL AND IT IS RETURNED HERE, not left to the
    route, because the failure it prevents is silent and total: a paralegal
    without `access_all_cases` who opens a matter and is not linked to it has
    created a case they cannot see, cannot list, and cannot reach by id. It
    would look exactly like the case never being saved.

    They are returned as a pair for the store to write TOGETHER. Two separate
    writes have a window in which the first succeeded and the second did not,
    and that window produces precisely the invisible case above — so
    CaseStore.create takes both and writes them in one transaction.

    An admin gets one too. It costs one small item and it means "who is on
    this matter" has one answer rather than "the assignees, plus whoever
    created it, plus anyone with access_all_cases".
    """
    now = _timestamp()
    case = Case(
        id=str(uuid.uuid4()),
        firm_id=firm_id,
        created_by=created_by,
        chapter=draft.chapter,
        district=draft.district,
        status="intake",
        created_at=now,
        updated_at=now,
    )
    return case, assign_case(case, subject=created_by, assigned_by=created_by)


def assign_case(case: Case, *, subject: str, assigned_by: str) -> CaseAssignment:
    """Link `subject` to `case`. `assigned_by` is the person doing the linking
    — the same value as `subject` when a case is being opened, and a firm
    admin's when someone is added to an existing matter."""
    return CaseAssignment(
        case_id=case.id,
        subject=subject,
        case_created_at=case.created_at,
        assigned_at=_timestamp(),
        assigned_by=assigned_by,
    )


def apply_changes(case: Case, changes: CaseChanges) -> Case:
    """A new Case with the supplied changes applied and updated_at refreshed."""
    updates = {
        field: value
        for field, value in (
            ("chapter", changes.chapter),
            ("district", changes.district),
            ("status", changes.status),
        )
        if value is not None
    }
    return replace(case, updated_at=_timestamp(), **updates)  # type: ignore[arg-type]


def pin_case(
    case: Case, *, form_revisions: Mapping[str, str], constants_set_id: str
) -> Case:
    """The pinned case packet assembly writes (effective-dating.md).

    A new Case with `form_revisions` and `constants_set_id` recorded and
    updated_at refreshed. Re-pinning an already-pinned case is the
    re-assembly rule working as designed: the new pins replace the old ones
    outright, because the new packet is now the one the pins describe. Both
    pins are required — a packet that resolved its forms also resolved its
    dollar amounts, and recording one without the other would make "what did
    this filing use" half-answerable.
    """
    return replace(
        case,
        form_revisions=dict(form_revisions),
        constants_set_id=constants_set_id,
        updated_at=_timestamp(),
    )


def partition_key(case_id: str) -> str:
    return f"CASE#{case_id}"


def firm_key(firm_id: str) -> str:
    return f"FIRM#{firm_id}"


def assignee_key(subject: str) -> str:
    return f"ASSIGNEE#{subject}"


def listing_sort_key(created_at: str, case_id: str) -> str:
    """The value BOTH listing indexes sort on, defined once.

    GSI1SK on the case and GSI2SK on its assignments must be built the same
    way, or the two listings order differently and a user whose
    `access_all_cases` is flipped sees their cases jump around. Sorting is
    lexicographic, which is why `_timestamp` is fixed-width and Z-suffixed.
    """
    return f"{created_at}#{case_id}"


def case_item(case: Case) -> dict[str, object]:
    """The exact stored item shape, shared by both CaseStore implementations.

    PK      CASE#<id>
    SK      META                    the case root; child entities take
                                    DEBTOR#<id>, CLAIM#<id>, ... later
    GSI1PK  FIRM#<firm_id>          the by-firm listing. Sparse: only this
    GSI1SK  <createdAt>#<id>        item carries them, one entry per case.

    NO GSI2 KEYS. The by-assignee index is fed by the assignment items below,
    which is what makes it sparse in the opposite direction — a case with no
    assignees simply is not in it.

    The pins are stored only when present — the same absent-means-absent rule
    the job record's `failure`/`result` follow, so an unpinned (floating) case
    row looks exactly as it did before assembly existed.
    """
    item: dict[str, object] = {
        "PK": partition_key(case.id),
        "SK": "META",
        "GSI1PK": firm_key(case.firm_id),
        "GSI1SK": listing_sort_key(case.created_at, case.id),
        "id": case.id,
        "firmId": case.firm_id,
        "createdBy": case.created_by,
        "chapter": case.chapter,
        "district": case.district,
        "status": case.status,
        "createdAt": case.created_at,
        "updatedAt": case.updated_at,
    }
    if case.form_revisions is not None:
        item["formRevisions"] = dict(case.form_revisions)
    if case.constants_set_id is not None:
        item["constantsSetId"] = case.constants_set_id
    return item


def case_from_item(item: Mapping[str, object]) -> Case:
    """Inverse of case_item. Raises ValidationError on an item this service
    did not write — a corrupt row should fail loudly here rather than become
    a half-populated Case that reaches a caller.

    `firmId` IS REQUIRED, with no fallback to the old `ownerPrincipal`. A row
    written before firms existed has no firm, and the tempting reading —
    "treat the old owner as the firm" — would put a Cognito subject where a
    firm id goes and quietly build a one-person tenant whose id is somebody's
    identity. Loud is right: those rows are dev probe data, and
    scripts/dev-aws-reset.sh is the answer.
    """
    try:
        raw_revisions = item.get("formRevisions")
        form_revisions = (
            {str(series): str(pin) for series, pin in raw_revisions.items()}
            if isinstance(raw_revisions, Mapping)
            else None
        )
        raw_constants = item.get("constantsSetId")
        chapter = item["chapter"]
        if not isinstance(chapter, (int, str)):
            raise ValueError(f"chapter is {chapter!r}")
        return Case(
            id=str(item["id"]),
            firm_id=str(item["firmId"]),
            created_by=str(item["createdBy"]),
            chapter=int(chapter),
            district=str(item["district"]),
            status=str(item["status"]),
            created_at=str(item["createdAt"]),
            updated_at=str(item["updatedAt"]),
            form_revisions=form_revisions,
            constants_set_id=str(raw_constants) if raw_constants is not None else None,
        )
    except (KeyError, ValueError) as error:
        raise ValidationError(f"stored case item is malformed: {error}") from error


def assignment_sort_key(subject: str) -> str:
    return f"ASSIGNEE#{subject}"


def assignment_item(assignment: CaseAssignment) -> dict[str, str | int]:
    """The exact stored item shape for a case assignment.

    PK      CASE#<case_id>              the case's own partition, so reading a
    SK      ASSIGNEE#<subject>          case and "am I linked to it" is one
                                        BatchGetItem rather than two queries
    GSI2PK  ASSIGNEE#<subject>          the by-assignee listing
    GSI2SK  <caseCreatedAt>#<case_id>   the CASE's timestamp — see the entity

    The projection is ALL, so a query on by-assignee returns these rows in
    full. They are not cases: the store reads the ids out of them and fetches
    the case records. Projecting the case onto the assignment instead would be
    a copy that goes stale the moment a district changes.
    """
    return {
        "PK": partition_key(assignment.case_id),
        "SK": assignment_sort_key(assignment.subject),
        "GSI2PK": assignee_key(assignment.subject),
        "GSI2SK": listing_sort_key(assignment.case_created_at, assignment.case_id),
        "caseId": assignment.case_id,
        "subject": assignment.subject,
        "caseCreatedAt": assignment.case_created_at,
        "assignedAt": assignment.assigned_at,
        "assignedBy": assignment.assigned_by,
    }


def assignment_from_item(item: Mapping[str, str | int]) -> CaseAssignment:
    try:
        return CaseAssignment(
            case_id=str(item["caseId"]),
            subject=str(item["subject"]),
            case_created_at=str(item["caseCreatedAt"]),
            assigned_at=str(item["assignedAt"]),
            assigned_by=str(item["assignedBy"]),
        )
    except KeyError as error:
        raise ValidationError(
            f"stored case assignment item is malformed: {error}"
        ) from error


def case_json(case: Case) -> dict[str, object]:
    """The API representation.

    `firmId` is absent because every caller who can see this case is in that
    firm by construction, so it would echo their own tenant id back at them.

    `createdBy` IS present, and it is new. It was absent when it was
    `ownerPrincipal` — a single-owner model made it the caller's own subject
    and therefore worthless — but with several colleagues on one matter "who
    opened this" is a thing the case list has to show, and the subject is what
    the firm's own staff list is keyed by (core/firms.firm_user_json), so the
    client can resolve it to a name without a second endpoint.
    """
    body: dict[str, object] = {
        "id": case.id,
        "createdBy": case.created_by,
        "chapter": case.chapter,
        "district": case.district,
        "status": case.status,
        "createdAt": case.created_at,
        "updatedAt": case.updated_at,
    }
    # Present only once packet assembly has pinned the case — absent, not
    # null, before that (the failure/result rule in core/jobs.job_json). The
    # client renders which printed revisions the packet used; it never sends
    # these back (parse_case_update does not accept them).
    if case.form_revisions is not None:
        body["formRevisions"] = dict(case.form_revisions)
    if case.constants_set_id is not None:
        body["constantsSetId"] = case.constants_set_id
    return body


def parse_list_limit(raw: str | None) -> int:
    if raw is None or raw == "":
        return DEFAULT_LIST_LIMIT
    try:
        limit = int(raw)
    except ValueError as error:
        raise ValidationError("limit must be a number") from error
    if limit < 1 or limit > MAX_LIST_LIMIT:
        raise ValidationError(f"limit must be between 1 and {MAX_LIST_LIMIT}")
    return limit


# The two listing indexes, and which one a caller reads depends on their
# permissions (core/access.Accessor.sees_every_case). A cursor names the index
# it was minted against for the reason the infra module's comment gives from
# the other side: the two return DIFFERENT SETS, so a start key from one is
# meaningless against the other.
#
# The failure this prevents is quiet. Flip a user's `access_all_cases` between
# two pages and, without the tag, page two resumes a by-assignee scan position
# inside a by-firm query: DynamoDB accepts the key, returns whatever sorts
# after it, and the user silently never sees the cases in between. With the
# tag it is a 400 and the client starts the listing again — which is the
# correct answer, because their listing genuinely changed.
INDEX_BY_FIRM = "by-firm"
INDEX_BY_ASSIGNEE = "by-assignee"


def encode_cursor(key: Mapping[str, str], *, index: str) -> str:
    """Opaque pagination cursor. Base64 rather than the raw key so the client
    cannot come to depend on the table's attribute names.

    Opaque, NOT signed. A caller can decode and rewrite one, and that buys
    them nothing: the start key only chooses where a query begins, and the
    query itself is already pinned to their own firm or their own subject. It
    is not an authorization token and must never become one.
    """
    payload = {"i": index, "k": dict(sorted(key.items()))}
    raw = json.dumps(payload, separators=(",", ":"), sort_keys=True)
    return base64.urlsafe_b64encode(raw.encode()).decode()


def decode_cursor(cursor: str, *, index: str) -> dict[str, str]:
    """Inverse of encode_cursor, rejecting anything this service did not mint
    and anything minted against a different index.

    The value is attacker-controlled and goes on to become a DynamoDB
    ExclusiveStartKey, so it is validated to a flat string map here rather
    than passed through — an unchecked dict would let a caller shape the
    query's start key.
    """
    try:
        raw = base64.urlsafe_b64decode(cursor.encode())
        decoded = json.loads(raw)
    except (ValueError, binascii.Error) as error:
        raise ValidationError("cursor is not valid") from error
    if not isinstance(decoded, dict) or decoded.get("i") != index:
        # One message for both failures. "That cursor is for the other index"
        # would tell a caller something about how their own permissions are
        # evaluated, and the client's action is the same either way: start the
        # listing again.
        raise ValidationError("cursor is not valid")
    key = decoded.get("k")
    if not isinstance(key, dict) or not all(
        isinstance(name, str) and isinstance(value, str) for name, value in key.items()
    ):
        raise ValidationError("cursor is not valid")
    return key
