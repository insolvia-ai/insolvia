"""The case lifecycle's own routes (issue 14.3 / #355): the status history,
archive, soft delete and copy case.

Status moves themselves are `PATCH /v1/cases/<id>` — a status is a field of
the case, and the lifecycle rules are applied wherever the case is changed
(`insolvia_core.cases.apply_changes`). What lives here are the acts that are
not a field edit: reading the history, taking a case out of (or back into)
the working list, removing it from every read, and opening a new case from
an existing one's data.

Every route resolves the case through `CaseStore.get` first, which applies
the whole access rule — and which, since #355, answers None for a deleted
case, so nothing here (or anywhere) can reach one.
"""

from __future__ import annotations

import logging
from dataclasses import replace
from typing import Any

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access_log import record_access
from insolvia_core.case_collections import COLLECTIONS
from insolvia_core.case_copy import copy_case
from insolvia_core.case_entities import CaseEntity
from insolvia_core.cases import (
    case_json,
    deletion_refusal,
    mark_deleted,
    parse_archive,
    set_archived,
    status_change_json,
)
from insolvia_core.debtors import Debtor
from insolvia_core.errors import (
    ConflictError,
    ForbiddenError,
    NotFoundError,
    ValidationError,
)
from insolvia_core.firms import ADD_EDIT, CASES, CLIENTS, VIEW_ONLY
from insolvia_core.ports import AccessLog

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.api.routes.cases import (
    MAX_REQUEST_BYTES,
    RETAINED_ROLES,
    case_stores,
    composed_firm_store,
    stamp_first_retained,
)

logger = logging.getLogger(__name__)

blueprint = Blueprint("case_lifecycle", __name__)


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 64 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


@blueprint.get("/v1/cases/<case_id>/status-history")
@require_auth
@requires(CASES, VIEW_ONLY)
def status_history_route(case_id: str) -> ResponseReturnValue:
    """Every lifecycle move of this case, oldest first — who moved it, when,
    from what to what. Not access-logged, like the assignee list: it is a
    fact about the matter's handling, read beside the case that was."""
    store, _ = case_stores()
    if store.get(case_id, accessor=current_accessor()) is None:
        raise NotFoundError("case not found")
    return jsonify(
        {"history": [status_change_json(c) for c in store.status_history(case_id)]}
    ), 200


@blueprint.put("/v1/cases/<case_id>/archived")
@require_auth
@requires(CASES, ADD_EDIT)
def archive_case_route(case_id: str) -> ResponseReturnValue:
    """Archive a case (`{"archived": true}`) or restore it to the working
    list (`false`). Idempotent, like `PUT` should be: archiving an archived
    case keeps its first stamp.

    ANY STATUS may be archived — a closed case and an exhausted prospect are
    the usual ones, but the firm decides what is off its desk. An archived
    case is still the firm's record: it reads, lists under `?archived=true`,
    and may be edited; leaving the working list is all archiving does.
    Logged as `case.update`: it is an edit to the record, not a new verb
    (the client directory's archive is the precedent).
    """
    store, access_log = case_stores()
    archived = parse_archive(_json_body())
    accessor = current_accessor()

    existing = store.get(case_id, accessor=accessor)
    updated = (
        None
        if existing is None
        else store.update(
            set_archived(existing, archived=archived, by=accessor.subject),
            expected_status=existing.status,
        )
    )
    access_log.record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action="case.update",
            outcome="allowed" if updated is not None else "denied",
        )
    )
    if updated is None:
        raise NotFoundError("case not found")
    logger.info("case archive changed", extra={"case_id": case_id})
    return jsonify(case_json(updated)), 200


@blueprint.delete("/v1/cases/<case_id>")
@require_auth
@requires(CASES, ADD_EDIT)
def delete_case_route(case_id: str) -> ResponseReturnValue:
    """Delete a case — SOFTLY, and only one that never reached the court.

    Nothing is removed. The case is stamped deleted and from then on every
    read refuses it (`access.may_see_case`), so it, its debtors, schedules,
    documents and everything else reached through it are gone from the
    product while the rows and bytes stay where they are — the retention
    posture docs/reference/case-data-model.md writes down. There is no
    restore route in v1.

    A FIRM ADMIN'S act, on top of `cases: add_edit`: it is the one change to
    a matter that takes it from every colleague at once, including those who
    were working it, and cannot be undone from the product. A 403, not a
    404 — the caller can see the case; what they lack is their own grant.

    A FILED case is refused (409), whatever happened to it since: its
    petition is a court record. Archive it instead.
    """
    store, access_log = case_stores()
    accessor = current_accessor()

    existing = store.get(case_id, accessor=accessor)
    if existing is None:
        access_log.record(
            record_access(
                case_id=case_id,
                principal=accessor.subject,
                action="case.delete",
                outcome="denied",
            )
        )
        raise NotFoundError("case not found")
    if not accessor.user.is_admin:
        raise ForbiddenError("only a firm admin may delete a case")
    refusal = deletion_refusal(existing)
    if refusal is not None:
        raise ConflictError(refusal)

    deleted = store.update(
        mark_deleted(existing, by=accessor.subject), expected_status=existing.status
    )
    access_log.record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action="case.delete",
            outcome="allowed" if deleted is not None else "denied",
        )
    )
    if deleted is None:
        raise NotFoundError("case not found")
    logger.info("case deleted", extra={"case_id": case_id})
    return "", 204


def _refuse_uncopyable_clients(
    debtors: tuple[Debtor, ...], access_log: AccessLog
) -> None:
    """The copy is opened for the source's clients, so it obeys the rules
    opening a case does (`cases._clients_to_open_for`): Debtor 1 must name a
    client, and no linked client may be archived, merged or mid-merge. Each
    client read is access-logged as a `client.read`.

    409, not 400: the request is fine — the SOURCE is in a state that cannot
    be copied until somebody restores or re-links a client."""
    accessor = current_accessor()
    firm_store = composed_firm_store()
    if not any(
        d.filing_role == "debtor_1" and d.client_id is not None for d in debtors
    ):
        raise ConflictError(
            "This case's Debtor 1 is not linked to a client, so a copy would"
            " be a case opened for nobody. Link the client first."
        )
    for debtor in debtors:
        if debtor.client_id is None:
            continue
        client = firm_store.get_client(accessor.firm_id, debtor.client_id)
        access_log.record(
            record_access(
                client_id=debtor.client_id,
                principal=accessor.subject,
                action="client.read",
                outcome="allowed" if client is not None else "denied",
            )
        )
        refusal = (
            "This case names a client that no longer exists."
            if client is None
            else client.refusal_for_new_case()
        )
        if refusal is not None:
            raise ConflictError(refusal)


@blueprint.post("/v1/cases/<case_id>/copy")
@require_auth
@requires(CASES, ADD_EDIT)
@requires(CLIENTS, VIEW_ONLY)
def copy_case_route(case_id: str) -> ResponseReturnValue:
    """Open a NEW case from this one's data (`insolvia_core.case_copy` says
    what travels and what does not), every copied value naming this case in
    its provenance. The body is ignored — the copy is the source's data;
    changing its chapter or court afterwards is an ordinary PATCH.

    `clients >= view_only` for the reason `POST /v1/cases` needs it: the
    copy reads client records into a case the caller can then read.

    ORDER IS THE TRANSACTION'S SUBSTITUTE. A case can hold more records than
    one DynamoDB transaction can write, so the copy cannot be one write. The
    collection records and the sealed tax ids are written FIRST, under the
    new case's id, while no case record exists to reach them; the case, its
    copier's assignment and its debtors land LAST, together, in
    `CaseStore.create`'s transaction. A copy that fails part-way leaves
    unreachable rows under an id nobody holds — never a half-copied case
    somebody can open.
    """
    store, access_log = case_stores()
    deps = dependencies()
    if (
        deps.debtor_store is None
        or deps.case_entity_store is None
        or deps.tax_id_store is None
    ):
        raise RuntimeError("debtor, entity and tax-id stores are not composed")
    accessor = current_accessor()

    source = store.get(case_id, accessor=accessor)
    access_log.record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action="case.read",
            outcome="allowed" if source is not None else "denied",
        )
    )
    if source is None:
        raise NotFoundError("case not found")

    debtors = deps.debtor_store.list_for_case(source.id)
    _refuse_uncopyable_clients(debtors, access_log)
    entities: list[CaseEntity[Any]] = [
        entity
        for kind in COLLECTIONS.values()
        for entity in deps.case_entity_store.list_for_case(source.id, kind)
    ]
    copy = copy_case(
        source, created_by=accessor.subject, debtors=debtors, entities=entities
    )

    # The sealed tax ids: ciphertext copied as it is — its encryption context
    # names the firm and the ref, never the case (`tax_ids`), so it opens
    # under the copy unchanged and nothing is decrypted here. A pointer whose
    # sealed item is missing is dropped rather than copied dangling.
    copied_debtors = []
    for debtor in copy.debtors:
        if debtor.tax_id is not None:
            sealed = deps.tax_id_store.get(source.id, debtor.tax_id.ref)
            if sealed is None:
                debtor = _without_tax_id(debtor)
            else:
                deps.tax_id_store.put(copy.case.id, sealed)
        copied_debtors.append(debtor)
    for entity in copy.entities:
        deps.case_entity_store.create(entity)
    store.create(copy.case, copy.assignment, copied_debtors)
    access_log.record(
        record_access(
            case_id=copy.case.id, principal=accessor.subject, action="case.create"
        )
    )
    # Opened retained, like any case opened without a status — the stamp the
    # engagement starting makes, for clients that have none yet.
    if any(d.filing_role in RETAINED_ROLES for d in copied_debtors):
        stamp_first_retained(copy.case.id, access_log)

    logger.info(
        "case copied", extra={"case_id": copy.case.id, "source_case_id": source.id}
    )
    return jsonify(case_json(copy.case)), 201


def _without_tax_id(debtor: Debtor) -> Debtor:
    provenance = {
        path: entry for path, entry in debtor.provenance.items() if path != "tax_id"
    }
    return replace(debtor, tax_id=None, provenance=provenance)
