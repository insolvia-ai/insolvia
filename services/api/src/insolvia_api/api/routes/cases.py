from __future__ import annotations

import logging

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue

from insolvia_api.api.auth import current_principal, require_auth
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.access_log import record_access
from insolvia_api.core.cases import (
    apply_changes,
    case_json,
    create_case,
    parse_case_creation,
    parse_case_update,
    parse_list_limit,
)
from insolvia_api.core.errors import NotFoundError, ValidationError
from insolvia_api.core.ports import AccessLog, CaseStore

logger = logging.getLogger(__name__)

blueprint = Blueprint("cases", __name__)

# A case record is a handful of short fields. Anything larger is a mistake or
# an attack, and rejecting it before JSON parsing keeps both cheap.
MAX_REQUEST_BYTES = 64 * 1024


def _stores() -> tuple[CaseStore, AccessLog]:
    """The case store and its access log, or a loud failure.

    Both are Optional on ApiDependencies so the existing public-route tests can
    build one without them. In a deployed environment they are always present:
    entrypoints/api_lambda.py refuses to boot without them, exactly as it does
    for auth. Reaching this branch is a composition bug, so it raises rather
    than degrading — a case endpoint that quietly stopped recording access
    would be worse than one that stopped working.
    """
    deps = dependencies()
    if deps.case_store is None or deps.access_log is None:
        raise RuntimeError("case store and access log are not composed")
    return deps.case_store, deps.access_log


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 64 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


@blueprint.post("/v1/cases")
@require_auth
def create_case_route() -> ResponseReturnValue:
    """Open a case owned by the caller.

    The owner is taken from the verified token and is never read from the
    body, so there is no request a client can make that creates a case
    belonging to someone else.
    """
    store, access_log = _stores()
    draft = parse_case_creation(_json_body())
    principal = current_principal().subject

    case = create_case(draft, owner_principal=principal)
    store.create(case)
    access_log.record(
        record_access(case_id=case.id, principal=principal, action="case.create")
    )

    # GLBA: the case id and nothing about its contents.
    logger.info("case created", extra={"case_id": case.id})
    return jsonify(case_json(case)), 201


@blueprint.get("/v1/cases")
@require_auth
def list_cases_route() -> ResponseReturnValue:
    """The caller's cases, newest first.

    Deliberately NOT written to the access log. That table is keyed by case,
    and a list touches no case in particular; the question it exists to answer
    is "who saw this file". Recording enumeration properly wants the
    by-principal index that infra/modules/case_store defers, and a sentinel
    partition here would be a worse answer than none.
    """
    store, _ = _stores()
    limit = parse_list_limit(request.args.get("limit"))
    cursor = request.args.get("cursor") or None

    page = store.list_for_owner(current_principal().subject, limit=limit, cursor=cursor)

    body: dict[str, object] = {"cases": [case_json(case) for case in page.cases]}
    # Absent rather than null when there is no next page — the client contract
    # distinguishes the two.
    if page.next_cursor is not None:
        body["nextCursor"] = page.next_cursor
    return jsonify(body), 200


@blueprint.get("/v1/cases/<case_id>")
@require_auth
def get_case_route(case_id: str) -> ResponseReturnValue:
    """One case, if it is the caller's.

    A case owned by someone else answers 404, identically to one that does not
    exist — see core/errors.py's NotFoundError for why that is not politeness.
    The refused read IS recorded: someone walking case ids is exactly what the
    access log should show.
    """
    store, access_log = _stores()
    principal = current_principal().subject

    case = store.get(case_id, owner_principal=principal)
    access_log.record(
        record_access(
            case_id=case_id,
            principal=principal,
            action="case.read",
            outcome="allowed" if case is not None else "denied",
        )
    )
    if case is None:
        raise NotFoundError("case not found")
    return jsonify(case_json(case)), 200


@blueprint.patch("/v1/cases/<case_id>")
@require_auth
def update_case_route(case_id: str) -> ResponseReturnValue:
    """Change a case's chapter, district or status.

    Read-modify-write. The store's conditional write closes the gap between
    the two, so a case cannot be reassigned out from under the caller between
    the read and the write.
    """
    store, access_log = _stores()
    changes = parse_case_update(_json_body())
    principal = current_principal().subject

    existing = store.get(case_id, owner_principal=principal)
    updated = (
        None if existing is None else store.update(apply_changes(existing, changes))
    )

    access_log.record(
        record_access(
            case_id=case_id,
            principal=principal,
            action="case.update",
            outcome="allowed" if updated is not None else "denied",
        )
    )
    if updated is None:
        raise NotFoundError("case not found")

    logger.info("case updated", extra={"case_id": updated.id})
    return jsonify(case_json(updated)), 200
