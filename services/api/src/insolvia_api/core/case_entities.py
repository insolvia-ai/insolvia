"""The machinery every case-scoped collection shares (issue #249).

docs/reference/case-data-model.md defines ten new entity types — creditors,
claims, assets, employments, income summaries, households, expenses,
dependents, codebtors and SOFA entries — and they are all the same KIND of
thing: a many-cardinality record living in its case's partition, carrying
server-stamped identity, a validated body, and a per-field provenance map.
Ten copies of core/debtors.py would be ten places for the provenance
invariants to drift, so the shape is written once here and each entity module
contributes only what is genuinely its own: the body dataclass and its parser.

What is NOT generic, on purpose:

- `debtor` stays in core/debtors.py. Its sort key is the filing role (a case
  cannot have two debtor_2s), where everything here is keyed by a server-minted
  uuid — "the third creditor" is not a position on any form.
- `document` stays in core/documents.py. A document is provenance's object
  rather than its subject and carries no provenance map at all.

IDENTITY AND ORDER. Every entity gets a uuid4 id, opaque and PII-free, exactly
as the data model's identifier rule demands. Ordering comes from `created_at`
(the model is explicit that it never comes from the id), oldest first — a
schedule is filled top to bottom, and the list should hold still while someone
works down it, unlike the documents listing where the newest upload is the one
being watched.

THE PROVENANCE INVARIANTS ARE INHERITED, NOT RESTATED. `parse_entity` runs
`require_provenance` over the parsed body, so every collection gets "every
populated field carries an entry" and "machine-supplied values must be
confirmed" without its module mentioning either. That is the seam that keeps
extraction review (8.9) a UI change: a manually typed creditor and a confirmed
extraction candidate are the same write through the same parser.
"""

from __future__ import annotations

import uuid
from collections.abc import Callable, Mapping
from dataclasses import asdict, dataclass
from typing import Generic, TypeVar

from .cases import partition_key
from .fields import prune_body, timestamp
from .provenance import (
    ProvenanceEntry,
    parse_provenance,
    provenance_json,
    require_provenance,
)

BodyT = TypeVar("BodyT")


@dataclass(frozen=True)
class EntityKind(Generic[BodyT]):
    """One collection's identity: what it is called, where its items sort, and
    how a body is validated. Defined once per entity module and registered in
    core/case_collections.py — the routes and stores never special-case a kind.

    `parse_body` validates SHAPE AND TYPE ONLY and accepts absent values
    everywhere (progressive intake); it raises FieldValidationError with every
    malformed field named. It never sees `provenance` — that is parsed and
    enforced here, so no entity module can forget it.
    """

    name: str
    # The URL segment and the JSON listing key, e.g. "creditors".
    collection: str
    # The DynamoDB sort-key namespace, e.g. "CREDITOR". Must be unique across
    # every SK in the case partition — META, ASSIGNEE, DEBTOR, DOCUMENT, and
    # each other — or one collection's listing would parse another's items.
    sk_prefix: str
    parse_body: Callable[[Mapping[str, object]], BodyT]


@dataclass(frozen=True)
class EntityDraft(Generic[BodyT]):
    """A validated body plus its provenance, before the server stamps
    identity on it."""

    body: BodyT
    provenance: Mapping[str, ProvenanceEntry]


@dataclass(frozen=True)
class CaseEntity(Generic[BodyT]):
    kind: EntityKind[BodyT]
    id: str
    case_id: str
    created_at: str
    updated_at: str
    body: BodyT
    provenance: Mapping[str, ProvenanceEntry]


def entity_body(entity: CaseEntity[BodyT] | EntityDraft[BodyT]) -> dict[str, object]:
    """The record's case data as plain nested values — what provenance paths
    address, and what gets stored. The body dataclass IS the case data, so
    unlike `debtor_body` there is nothing to strip."""
    body = asdict(entity)["body"]
    if not isinstance(body, dict):  # pragma: no cover - bodies are dataclasses
        raise TypeError("an entity body must be a dataclass")
    return body


def parse_entity(
    kind: EntityKind[BodyT],
    payload: Mapping[str, object],
    *,
    enforce_provenance: bool = True,
) -> EntityDraft[BodyT]:
    """Validate a whole entity body. Unknown keys are ignored.

    WHOLE, not partial, for the same reason parse_debtor is: invariant 1 can
    only be checked against a complete record, so the endpoints PUT the whole
    record rather than PATCHing fields.

    The body is parsed BEFORE provenance, so a request with both a malformed
    field and bad provenance reports the field first — the fixable thing.

    `enforce_provenance` is a WRITE rule, switched off on reads: a stored
    record already passed it once, and re-running it on the way out would make
    every record written under an older rule unreadable the day the rule
    tightens — failing a whole case's GET over one old row.
    """
    body = kind.parse_body(payload)
    provenance = parse_provenance(payload.get("provenance"))
    draft = EntityDraft(body=body, provenance=provenance)
    if enforce_provenance:
        # Against the record as it will be STORED rather than as it arrived:
        # the field parsers collapse whitespace-only values to None, so
        # checking the raw payload would demand provenance for fields that are
        # about to vanish.
        require_provenance(entity_body(draft), provenance)
    return draft


def create_entity(
    kind: EntityKind[BodyT], draft: EntityDraft[BodyT], *, case_id: str
) -> CaseEntity[BodyT]:
    now = timestamp()
    return CaseEntity(
        kind=kind,
        id=str(uuid.uuid4()),
        case_id=case_id,
        created_at=now,
        updated_at=now,
        body=draft.body,
        provenance=draft.provenance,
    )


def replace_entity(
    existing: CaseEntity[BodyT], draft: EntityDraft[BodyT]
) -> CaseEntity[BodyT]:
    """A new record with the draft's body, keeping the original id and
    created_at. The id is stable across saves because provenance paths on
    other records may reference it, and created_at because it is the sort."""
    return CaseEntity(
        kind=existing.kind,
        id=existing.id,
        case_id=existing.case_id,
        created_at=existing.created_at,
        updated_at=timestamp(),
        body=draft.body,
        provenance=draft.provenance,
    )


def sort_key(kind: EntityKind[BodyT], entity_id: str) -> str:
    return f"{kind.sk_prefix}#{entity_id}"


def list_order(entity: CaseEntity[BodyT]) -> tuple[str, str]:
    """Creation order, id as the tiebreak — the ONE definition, because both
    stores must agree and neither gets it from its own key order: the sort key
    is a random uuid, which orders items by coin flip."""
    return (entity.created_at, entity.id)


def entity_json(entity: CaseEntity[BodyT]) -> dict[str, object]:
    """The API representation. Absent values are omitted rather than sent as
    nulls, exactly as debtor_json's are — on a progressive intake most of a
    record is empty most of the time."""
    return {
        "id": entity.id,
        "case_id": entity.case_id,
        "created_at": entity.created_at,
        "updated_at": entity.updated_at,
        "provenance": provenance_json(entity.provenance),
        **prune_body(entity_body(entity)),
    }


def entity_item(entity: CaseEntity[BodyT]) -> dict[str, object]:
    """The stored item shape.

    PK  CASE#<case_id>        the same partition as the case root, so one
    SK  <PREFIX>#<id>         collection is one begins_with query and a whole
                              case is one partition read.

    Carries no GSI keys: like debtors and documents, these records are always
    reached through their case and never listed across cases.
    """
    return {
        "PK": partition_key(entity.case_id),
        "SK": sort_key(entity.kind, entity.id),
        "id": entity.id,
        "caseId": entity.case_id,
        "createdAt": entity.created_at,
        "updatedAt": entity.updated_at,
        "body": prune_body(entity_body(entity)),
        "provenance": provenance_json(entity.provenance),
    }


def entity_from_item(
    kind: EntityKind[BodyT], item: Mapping[str, object]
) -> CaseEntity[BodyT]:
    """Rebuild from a stored item.

    The body is re-parsed rather than trusted: an item written by an older
    revision is exactly the case where a field has since changed shape, and
    failing loudly here beats a `None` surfacing three layers up. Provenance
    is NOT re-enforced — see parse_entity.
    """
    body = item.get("body")
    draft = parse_entity(
        kind,
        {
            **(body if isinstance(body, Mapping) else {}),
            "provenance": item.get("provenance"),
        },
        enforce_provenance=False,
    )
    return CaseEntity(
        kind=kind,
        id=str(item.get("id", "")),
        case_id=str(item.get("caseId", "")),
        created_at=str(item.get("createdAt", "")),
        updated_at=str(item.get("updatedAt", "")),
        body=draft.body,
        provenance=draft.provenance,
    )
