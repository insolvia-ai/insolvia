"""Candidate records — the write half of the MCP surface (mcp-surface.md).

An agent never writes case data (ADR 0013). What `propose_case_records`
stores is the data model's `extraction_candidate`, generalised exactly as
docs/reference/case-data-model.md now records: `document_id` becomes optional
(an agent proposal has no source document), an `origin` block says which OAuth
client and which subject proposed it, and `withdrawn` joins the status
vocabulary as a second terminal state beside `rejected`.

One review queue, one status vocabulary, one confirmation act: extraction
(8.7/8.8) writes the same rows with a `document_id` and an extraction origin,
and the review UI (8.9) accepts, corrects or rejects both streams without
knowing which is which beyond the origin it displays. Acceptance — which
writes the real case record with machine-source provenance and the
confirmation pair — belongs to the review flow in services/api; there is
deliberately no code path in the MCP service that turns a candidate into
case data.

WHERE THIS LIVES: this module began in services/mcp (issue #262) with a note
that it would graduate here the day the review flow became its second
importer — the core package's admission rule (ADR 0012) is a concrete second
importer, never "will be shared". 8.7-8.9 are that importer: the extraction
workers write these rows and the review routes read them, so the module and
its adapters moved verbatim, exactly as the case domain did (ADR 0016). It
owns the case partition's `CANDIDATE#` namespace, registered in
insolvia_core.case_collections.RESERVED_SK_NAMESPACES.

Pure: no boto3, no framework, no clock beyond datetime.now via fields.timestamp.
"""

from __future__ import annotations

import uuid
from collections.abc import Mapping
from dataclasses import dataclass, replace
from typing import Final

from insolvia_core.case_collections import COLLECTIONS
from insolvia_core.case_entities import parse_entity
from insolvia_core.cases import partition_key
from insolvia_core.debtors import parse_debtor
from insolvia_core.errors import (
    ConflictError,
    FieldValidationError,
    ForbiddenError,
    ValidationError,
)
from insolvia_core.fields import prune_body, timestamp

# The status vocabulary. `withdrawn` is the MCP surface's addition: only the
# proposing subject may set it, and only while `pending`. Withdrawn candidates
# are RETAINED like rejected ones — they measure agent quality the same way
# corrections measure extraction quality.
PENDING: Final = "pending"
ACCEPTED: Final = "accepted"
CORRECTED: Final = "corrected"
REJECTED: Final = "rejected"
WITHDRAWN: Final = "withdrawn"
STATUSES: Final = (PENDING, ACCEPTED, CORRECTED, REJECTED, WITHDRAWN)

# Which channels can originate a candidate. Extraction's channel is named now
# so the enum exists when 8.7 writes its first row, mirroring how the
# extraction_review feature was listed before the feature shipped.
ORIGIN_CHANNELS: Final = ("mcp", "extraction")

# 1-25 proposals per call (mcp-surface.md § Limits). A bigger batch is a
# mistake or an attack; a harness with more records makes more calls.
MAX_PROPOSALS_PER_CALL: Final = 25

MAX_NOTE: Final = 2000
MAX_EXTERNAL_REF_FIELD: Final = 500

# The entity types a proposal may target: every generic collection plus the
# debtor. NOT `documents` (bytes stay in the app's presigned flow — a harness
# cannot propose a file it has no way to hand us) and NOT the case root
# (ADR 0013: an agent never initiates a matter; mcp-surface.md records the
# case-candidate shape as an open question, not a v1 feature).
PROPOSABLE_ENTITY_TYPES: Final = (*COLLECTIONS, "debtors")


@dataclass(frozen=True)
class CandidateOrigin:
    """Who proposed this, through what — attribution the way `uploaded_by`
    attributes a document. Taken from the VERIFIED TOKEN, never from an
    argument: a harness cannot claim to be another client or another user."""

    channel: str
    client_id: str
    subject: str


@dataclass(frozen=True)
class ExternalRef:
    """Where a harness sourced the record — the provenance pointer that
    survived ADR 0013's deletion of the sync seam. Carried onto the case
    record at acceptance."""

    system: str
    external_id: str
    external_url: str | None = None


@dataclass(frozen=True)
class Candidate:
    id: str
    case_id: str
    entity_type: str
    # The proposed record, shaped like the target entity's wire shape. Parsed
    # for shape and type before storage (see parse_proposal); provenance is
    # NOT required here — it is written at acceptance, by the reviewing human,
    # with `source: imported` and the confirmation fields.
    payload: Mapping[str, object]
    status: str
    origin: CandidateOrigin
    created_at: str
    updated_at: str
    document_id: str | None = None
    external_ref: ExternalRef | None = None
    note: str | None = None
    # The extraction stream's fields (data model: `extraction_candidate`'s
    # `confidence, locator`): how sure the model was of this record, and
    # where on the source document it read it. Absent on MCP proposals — an
    # agent's say-so has no confidence score and no page to point at.
    confidence: float | None = None
    locator: Mapping[str, object] | None = None
    # Review outcome — written by 8.9's review flow, only read here.
    confirmed_by: str | None = None
    confirmed_at: str | None = None
    corrected_payload: Mapping[str, object] | None = None
    resulting_record_id: str | None = None


@dataclass(frozen=True)
class ProposalDraft:
    """One validated proposal, before the server stamps identity on it."""

    entity_type: str
    payload: Mapping[str, object]
    external_ref: ExternalRef | None
    note: str | None


def _validate_payload_shape(entity_type: str, payload: Mapping[str, object]) -> None:
    """Shape and type only, absent values accepted everywhere — the same
    progressive-intake rule the API's parse functions enforce, run through the
    SAME parse functions so the two surfaces cannot disagree about what a
    creditor looks like. Provenance is not enforced: a candidate is not case
    data yet, and the confirmation act is what mints its provenance."""
    if entity_type == "debtors":
        parse_debtor(payload, enforce_provenance=False)
        return
    kind = COLLECTIONS[entity_type]
    parse_entity(kind, payload, enforce_provenance=False)


def _parse_external_ref(
    value: object, index: int, errors: dict[str, str]
) -> ExternalRef | None:
    if value is None:
        return None
    prefix = f"proposals[{index}].externalRef"
    if not isinstance(value, Mapping):
        errors[prefix] = "externalRef must be an object."
        return None
    system = value.get("system")
    external_id = value.get("externalId")
    external_url = value.get("externalUrl")
    ref_errors = False
    if not isinstance(system, str) or not system.strip():
        errors[f"{prefix}.system"] = "A source system name is required."
        ref_errors = True
    if not isinstance(external_id, str) or not external_id.strip():
        errors[f"{prefix}.externalId"] = "A source record id is required."
        ref_errors = True
    if external_url is not None and (
        not isinstance(external_url, str) or not external_url.strip()
    ):
        errors[f"{prefix}.externalUrl"] = "externalUrl must be a string."
        ref_errors = True
    for field_name, field_value in (
        ("system", system),
        ("externalId", external_id),
        ("externalUrl", external_url),
    ):
        if isinstance(field_value, str) and len(field_value) > MAX_EXTERNAL_REF_FIELD:
            errors[f"{prefix}.{field_name}"] = (
                f"Must be at most {MAX_EXTERNAL_REF_FIELD} characters."
            )
            ref_errors = True
    # The None checks are redundant with `ref_errors` but they are what
    # narrows the types — same pattern as insolvia_core.cases.
    if ref_errors or not isinstance(system, str) or not isinstance(external_id, str):
        return None
    return ExternalRef(
        system=system.strip(),
        external_id=external_id.strip(),
        external_url=external_url.strip()
        if isinstance(external_url, str) and external_url.strip()
        else None,
    )


def parse_proposals(value: object) -> tuple[ProposalDraft, ...]:
    """Validate a `propose_case_records` batch. Unknown keys are ignored.

    The batch is validated WHOLE before anything is stored: a request whose
    third proposal is malformed stores nothing, because "we kept the first
    two" is a partial write the caller has no way to discover from a
    validation error.
    """
    if not isinstance(value, list | tuple):
        raise ValidationError("proposals must be an array")
    if not 1 <= len(value) <= MAX_PROPOSALS_PER_CALL:
        raise ValidationError(
            f"proposals must contain between 1 and {MAX_PROPOSALS_PER_CALL} items"
        )

    errors: dict[str, str] = {}
    drafts: list[ProposalDraft] = []
    for index, raw in enumerate(value):
        if not isinstance(raw, Mapping):
            errors[f"proposals[{index}]"] = "Each proposal must be an object."
            continue
        entity_type = raw.get("entityType")
        if entity_type not in PROPOSABLE_ENTITY_TYPES:
            errors[f"proposals[{index}].entityType"] = (
                "entityType must be one of: " + ", ".join(PROPOSABLE_ENTITY_TYPES) + "."
            )
            continue
        payload = raw.get("payload")
        if not isinstance(payload, Mapping):
            errors[f"proposals[{index}].payload"] = "payload must be an object."
            continue
        try:
            _validate_payload_shape(entity_type, payload)
        except FieldValidationError as error:
            for path, message in error.fields.items():
                errors[f"proposals[{index}].payload.{path}"] = message
            continue
        note = raw.get("note")
        if note is not None and not isinstance(note, str):
            errors[f"proposals[{index}].note"] = "note must be a string."
            continue
        if isinstance(note, str) and len(note) > MAX_NOTE:
            errors[f"proposals[{index}].note"] = (
                f"Must be at most {MAX_NOTE} characters."
            )
            continue
        external_ref = _parse_external_ref(raw.get("externalRef"), index, errors)
        drafts.append(
            ProposalDraft(
                entity_type=entity_type,
                payload=payload,
                external_ref=external_ref,
                note=note.strip() if isinstance(note, str) and note.strip() else None,
            )
        )

    if errors:
        raise FieldValidationError(errors)
    return tuple(drafts)


def create_candidate(
    draft: ProposalDraft,
    *,
    case_id: str,
    origin: CandidateOrigin,
    document_id: str | None = None,
    confidence: float | None = None,
    locator: Mapping[str, object] | None = None,
) -> Candidate:
    """A fresh pending candidate. The three keyword extras are the extraction
    stream's (8.7/8.8): the source document, the model's confidence, and the
    page anchor. MCP callers pass none of them — an agent proposal has no
    source document (mcp-surface.md)."""
    now = timestamp()
    return Candidate(
        id=str(uuid.uuid4()),
        case_id=case_id,
        entity_type=draft.entity_type,
        payload=draft.payload,
        status=PENDING,
        origin=origin,
        created_at=now,
        updated_at=now,
        document_id=document_id,
        external_ref=draft.external_ref,
        note=draft.note,
        confidence=confidence,
        locator=locator,
    )


def withdraw(candidate: Candidate, *, subject: str) -> Candidate:
    """The withdrawn copy, or a refusal.

    ForbiddenError for somebody else's candidate rather than the anti-oracle
    404: a colleague can already SEE the row through `check_proposals`, so
    there is nothing to hide — only a rule about whose retraction it is.
    ConflictError once reviewed: the row exists, the caller may see it, and
    its state refuses; a 404 would tell an honest harness its candidate
    vanished.
    """
    if candidate.origin.subject != subject:
        raise ForbiddenError("only the proposer may withdraw a candidate")
    if candidate.status != PENDING:
        raise ConflictError(
            f"candidate has already been reviewed (status: {candidate.status})"
        )
    return replace(candidate, status=WITHDRAWN, updated_at=timestamp())


def sort_key(candidate_id: str) -> str:
    return f"CANDIDATE#{candidate_id}"


def list_order(candidate: Candidate) -> tuple[str, str]:
    """Creation order, id as the tiebreak — the sort key is a random uuid, so
    neither store implementation gets this ordering for free."""
    return (candidate.created_at, candidate.id)


def candidate_item(candidate: Candidate) -> dict[str, object]:
    """The stored item shape.

    PK  CASE#<case_id>          the case's own partition — outside the case
    SK  CANDIDATE#<id>          DATA (no store invariant reads it as a case
                                record) but inside its blast radius, so
                                deleting a case's partition deletes its queue.

    Carries no GSI keys: candidates are always reached through their case.
    Optional review fields are stored only when set, so a pending row carries
    no null review columns for the review flow to misread.
    """
    item: dict[str, object] = {
        "PK": partition_key(candidate.case_id),
        "SK": sort_key(candidate.id),
        "id": candidate.id,
        "caseId": candidate.case_id,
        "entityType": candidate.entity_type,
        "payload": prune_body(candidate.payload),
        "status": candidate.status,
        "origin": {
            "channel": candidate.origin.channel,
            "clientId": candidate.origin.client_id,
            "subject": candidate.origin.subject,
        },
        "createdAt": candidate.created_at,
        "updatedAt": candidate.updated_at,
    }
    if candidate.document_id is not None:
        item["documentId"] = candidate.document_id
    if candidate.external_ref is not None:
        ref: dict[str, object] = {
            "system": candidate.external_ref.system,
            "externalId": candidate.external_ref.external_id,
        }
        if candidate.external_ref.external_url is not None:
            ref["externalUrl"] = candidate.external_ref.external_url
        item["externalRef"] = ref
    if candidate.note is not None:
        item["note"] = candidate.note
    if candidate.confidence is not None:
        item["confidence"] = candidate.confidence
    if candidate.locator is not None:
        item["locator"] = dict(candidate.locator)
    if candidate.confirmed_by is not None:
        item["confirmedBy"] = candidate.confirmed_by
    if candidate.confirmed_at is not None:
        item["confirmedAt"] = candidate.confirmed_at
    if candidate.corrected_payload is not None:
        item["correctedPayload"] = prune_body(candidate.corrected_payload)
    if candidate.resulting_record_id is not None:
        item["resultingRecordId"] = candidate.resulting_record_id
    return item


def candidate_from_item(item: Mapping[str, object]) -> Candidate:
    """Inverse of candidate_item. Raises ValidationError on an item this
    service did not write — a corrupt row fails loudly here rather than
    becoming a half-populated candidate in a review answer."""
    origin = item.get("origin")
    if not isinstance(origin, Mapping):
        raise ValidationError("stored candidate item is malformed: origin")
    external_ref: ExternalRef | None = None
    raw_ref = item.get("externalRef")
    if isinstance(raw_ref, Mapping):
        url = raw_ref.get("externalUrl")
        external_ref = ExternalRef(
            system=str(raw_ref.get("system", "")),
            external_id=str(raw_ref.get("externalId", "")),
            external_url=str(url) if url is not None else None,
        )
    payload = item.get("payload")
    corrected = item.get("correctedPayload")
    raw_confidence = item.get("confidence")
    raw_locator = item.get("locator")
    try:
        return Candidate(
            id=str(item["id"]),
            case_id=str(item["caseId"]),
            entity_type=str(item["entityType"]),
            payload=payload if isinstance(payload, Mapping) else {},
            status=str(item["status"]),
            origin=CandidateOrigin(
                channel=str(origin.get("channel", "")),
                client_id=str(origin.get("clientId", "")),
                subject=str(origin.get("subject", "")),
            ),
            created_at=str(item["createdAt"]),
            updated_at=str(item["updatedAt"]),
            document_id=_optional_str(item.get("documentId")),
            external_ref=external_ref,
            note=_optional_str(item.get("note")),
            confidence=float(raw_confidence)
            if not isinstance(raw_confidence, bool)
            and isinstance(raw_confidence, (int, float))
            else None,
            locator=raw_locator if isinstance(raw_locator, Mapping) else None,
            confirmed_by=_optional_str(item.get("confirmedBy")),
            confirmed_at=_optional_str(item.get("confirmedAt")),
            corrected_payload=corrected if isinstance(corrected, Mapping) else None,
            resulting_record_id=_optional_str(item.get("resultingRecordId")),
        )
    except KeyError as error:
        raise ValidationError(f"stored candidate item is malformed: {error}") from error


def _optional_str(value: object) -> str | None:
    return str(value) if value is not None else None


def candidate_json(candidate: Candidate) -> dict[str, object]:
    """The review queue's wire shape (8.9): everything a reviewing human
    needs to verify a record — the proposed payload, where it came from
    (origin, source document, page anchor, confidence), and the outcome once
    there is one. Distinct from `candidate_review_json`, which is the
    HARNESS-facing answer and deliberately omits the payload the harness
    already holds. Absent values are omitted rather than sent as nulls."""
    result: dict[str, object] = {
        "id": candidate.id,
        "entityType": candidate.entity_type,
        "status": candidate.status,
        "payload": prune_body(candidate.payload),
        "origin": {
            "channel": candidate.origin.channel,
            "clientId": candidate.origin.client_id,
            "subject": candidate.origin.subject,
        },
        "createdAt": candidate.created_at,
        "updatedAt": candidate.updated_at,
    }
    if candidate.document_id is not None:
        result["documentId"] = candidate.document_id
    if candidate.confidence is not None:
        result["confidence"] = candidate.confidence
    if candidate.locator is not None:
        result["locator"] = dict(candidate.locator)
    if candidate.note is not None:
        result["note"] = candidate.note
    if candidate.external_ref is not None:
        ref: dict[str, object] = {
            "system": candidate.external_ref.system,
            "externalId": candidate.external_ref.external_id,
        }
        if candidate.external_ref.external_url is not None:
            ref["externalUrl"] = candidate.external_ref.external_url
        result["externalRef"] = ref
    if candidate.confirmed_by is not None:
        result["confirmedBy"] = candidate.confirmed_by
    if candidate.confirmed_at is not None:
        result["confirmedAt"] = candidate.confirmed_at
    if candidate.corrected_payload is not None:
        result["correctedPayload"] = prune_body(candidate.corrected_payload)
    if candidate.resulting_record_id is not None:
        result["resultingRecordId"] = candidate.resulting_record_id
    return result


def candidate_review_json(candidate: Candidate) -> dict[str, object]:
    """The `check_proposals` wire shape: the review status, the human's
    corrections, and the resulting record id — the feedback signal. Absent
    values are omitted rather than sent as nulls, matching every other wire
    shape on the surface."""
    result: dict[str, object] = {
        "candidateId": candidate.id,
        "entityType": candidate.entity_type,
        "status": candidate.status,
    }
    if candidate.confirmed_by is not None:
        result["confirmedBy"] = candidate.confirmed_by
    if candidate.confirmed_at is not None:
        result["confirmedAt"] = candidate.confirmed_at
    if candidate.corrected_payload is not None:
        result["correctedPayload"] = prune_body(candidate.corrected_payload)
    if candidate.resulting_record_id is not None:
        result["resultingRecordId"] = candidate.resulting_record_id
    return result
