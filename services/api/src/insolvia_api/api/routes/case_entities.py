"""The generic case collections (issue #249): creditors, claims, assets,
employments, income summaries, households, expenses, dependents, codebtors and
SOFA entries — list, add, edit, remove, per case.

ONE SET OF ROUTES FOR ALL TEN, dispatched through core/case_collections.py.
The collections differ only in their body parser, which the EntityKind
carries; separate route modules would be ten copies of the same authorisation
sequence, which is ten chances to drop the case lookup that is the only thing
between one firm's schedules and another's.

The `<collection>` URL segment is dynamic, and Werkzeug's routing puts static
rules first — so `/v1/cases/<id>/debtors` and `/v1/cases/<id>/documents` keep
matching their own modules, and this one sees only what nothing else claimed.
An unknown collection answers the plain 404 any unknown URL gets, before the
case is even looked at: which collections exist is public knowledge (they are
in the client), so there is nothing for the anti-oracle rule to protect.

POST mints the id; PUT replaces a record it already minted. There is no
upsert-by-client-id: a client cannot invent an entity id, for the same reason
document ids are server-minted — the id is the address provenance paths on
other records may use, and it must be one shape from one mint.
"""

from __future__ import annotations

import logging
from typing import Any

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.errors import NotFoundError, ValidationError
from insolvia_core.firms import ADD_EDIT, INTAKE, VIEW_ONLY

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.access import Accessor
from insolvia_api.core.access_log import record_access
from insolvia_api.core.case_collections import COLLECTIONS
from insolvia_api.core.case_entities import (
    CaseEntity,
    EntityKind,
    create_entity,
    entity_json,
    parse_entity,
    replace_entity,
)
from insolvia_api.core.ports import AccessLog, CaseEntityStore, CaseStore

logger = logging.getLogger(__name__)

blueprint = Blueprint("case_entities", __name__)

# The debtor cap, for the debtor's reasons: a record plus a provenance entry
# per populated field, and provenance is roughly as big as the data it
# describes. The largest bodies here (a claim with notice parties, a SOFA
# business connection) are still smaller than a debtor with its alias list.
MAX_REQUEST_BYTES = 256 * 1024


def _stores() -> tuple[CaseStore, CaseEntityStore, AccessLog]:
    deps = dependencies()
    if (
        deps.case_store is None
        or deps.case_entity_store is None
        or deps.access_log is None
    ):
        raise RuntimeError("case store, entity store and access log are not composed")
    return deps.case_store, deps.case_entity_store, deps.access_log


def _kind(collection: str) -> EntityKind[Any]:
    kind = COLLECTIONS.get(collection)
    if kind is None:
        # The same 404 an unknown URL gets. Which collections exist is public
        # (they are named in the client), so this hides nothing — it is just
        # not a route.
        raise NotFoundError("no such collection")
    return kind


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 256 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


def _reachable_case_or_404(accessor: Accessor, case_id: str, action: str) -> None:
    """Resolve the case first, and record the attempt either way.

    EVERY entity route goes through here, because this is the only
    authorisation check there is: `CaseEntityStore` takes no accessor and
    enforces nothing (see its Protocol for why one authorisation path beats
    two). A route that skipped this would read another firm's creditor
    schedule with no error anywhere.
    """
    case_store, _, access_log = _stores()
    case = case_store.get(case_id, accessor=accessor)
    access_log.record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action=action,
            outcome="allowed" if case is not None else "denied",
        )
    )
    if case is None:
        # Identical to a case that does not exist — core/errors.py explains why
        # distinguishing them turns this into an id oracle.
        raise NotFoundError("case not found")


def _entity_or_404(
    case_id: str, kind: EntityKind[Any], entity_id: str
) -> CaseEntity[Any]:
    """One record of an already-authorised case. The case id is half the
    store's key, so an entity id belonging to another case answers the same
    404 as one that never existed — the second half of the id-oracle
    argument."""
    _, entity_store, _ = _stores()
    entity = entity_store.get(case_id, kind, entity_id)
    if entity is None:
        raise NotFoundError("record not found")
    return entity


def _log_saved(case_id: str, kind: EntityKind[Any], entity_id: str) -> None:
    """GLBA: ids and the collection name. Never a creditor's name, an amount,
    or a field count — even the number of populated fields leaks how much of
    someone's schedule is filled in."""
    logger.info(
        "case entity saved",
        extra={"case_id": case_id, "collection": kind.collection, "id": entity_id},
    )


@blueprint.post("/v1/cases/<case_id>/<collection>")
@require_auth
@requires(INTAKE, ADD_EDIT)
def create_entity_route(case_id: str, collection: str) -> ResponseReturnValue:
    """Add one record to a collection. The server mints the id and returns the
    stored record, provenance and all."""
    _, entity_store, _ = _stores()
    accessor = current_accessor()

    # Body BEFORE ownership — the same deliberate inversion as the debtor and
    # document routes: a 400 says "your JSON is wrong", not "that case
    # exists", and the access log then only records requests that actually
    # reached the case.
    kind = _kind(collection)
    draft = parse_entity(kind, _json_body())
    _reachable_case_or_404(accessor, case_id, "case.update")

    entity = create_entity(kind, draft, case_id=case_id)
    entity_store.create(entity)
    _log_saved(case_id, kind, entity.id)
    return jsonify(entity_json(entity)), 201


@blueprint.get("/v1/cases/<case_id>/<collection>")
@require_auth
@requires(INTAKE, VIEW_ONLY)
def list_entities_route(case_id: str, collection: str) -> ResponseReturnValue:
    """Every record of one collection in one case, in creation order — the
    order the rows were added, which holds still while someone works down the
    schedule."""
    _, entity_store, _ = _stores()
    accessor = current_accessor()

    kind = _kind(collection)
    _reachable_case_or_404(accessor, case_id, "case.read")
    entities = entity_store.list_for_case(case_id, kind)
    return jsonify({kind.collection: [entity_json(e) for e in entities]}), 200


@blueprint.get("/v1/cases/<case_id>/<collection>/<entity_id>")
@require_auth
@requires(INTAKE, VIEW_ONLY)
def get_entity_route(
    case_id: str, collection: str, entity_id: str
) -> ResponseReturnValue:
    accessor = current_accessor()

    kind = _kind(collection)
    _reachable_case_or_404(accessor, case_id, "case.read")
    entity = _entity_or_404(case_id, kind, entity_id)
    return jsonify(entity_json(entity)), 200


@blueprint.put("/v1/cases/<case_id>/<collection>/<entity_id>")
@require_auth
@requires(INTAKE, ADD_EDIT)
def put_entity_route(
    case_id: str, collection: str, entity_id: str
) -> ResponseReturnValue:
    """Replace one record, whole.

    PUT rather than PATCH for invariant 1's reason (see parse_debtor): "every
    populated field carries provenance" can only be checked against a complete
    record. 404 rather than upsert for a missing id: ids are server-minted, so
    an id this store has never seen is a client error, not a creation.
    """
    _, entity_store, _ = _stores()
    accessor = current_accessor()

    kind = _kind(collection)
    draft = parse_entity(kind, _json_body())
    _reachable_case_or_404(accessor, case_id, "case.update")

    stored = _entity_or_404(case_id, kind, entity_id)
    entity = replace_entity(stored, draft)
    if not entity_store.put(entity):
        # Deleted while this request was in flight. The same 404 a foreign id
        # gets, rather than resurrecting the record.
        raise NotFoundError("record not found")
    _log_saved(case_id, kind, entity.id)
    return jsonify(entity_json(entity)), 200


@blueprint.delete("/v1/cases/<case_id>/<collection>/<entity_id>")
@require_auth
@requires(INTAKE, ADD_EDIT)
def delete_entity_route(
    case_id: str, collection: str, entity_id: str
) -> ResponseReturnValue:
    """Remove one record.

    References are NOT cascaded: a claim naming a deleted creditor, a codebtor
    naming a deleted claim, keep their ids. Storage validates shape only, and
    a dangling reference is the completeness gate's to flag (9.6) — cascading
    here would silently destroy typed data because of one deletion, which is
    worse than a reference the UI can show as broken.
    """
    _, entity_store, _ = _stores()
    accessor = current_accessor()

    kind = _kind(collection)
    _reachable_case_or_404(accessor, case_id, "case.update")
    if not entity_store.delete(case_id, kind, entity_id):
        raise NotFoundError("record not found")
    logger.info(
        "case entity deleted",
        extra={"case_id": case_id, "collection": kind.collection, "id": entity_id},
    )
    # 204 rather than the deleted record, as the document delete answers.
    return "", 204
