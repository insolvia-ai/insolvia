"""The firm's reusable creditor library (issue 13.9 / #350): list, add, edit
and remove under `/v1/firm/creditors`, gated by the `creditor_library`
feature exactly as `routes/firm.py` gates its own surface.

SCOPED TO THE CALLER'S FIRM, the same way every route in `routes/firm.py` is —
there is no firm id anywhere in these URLs, for the same reason: a firm id a
client could set is a firm id somebody will eventually set to a different
one. `insolvia_core.library_creditors` owns why this is a whole-record save
(POST/PUT both take the full shape) rather than a PATCH, and why the wire is
snake_case where `routes/firm.py`'s is camelCase.

This module intentionally does not touch `case_entities.py` or its routes —
what a case does with a library pick (copying fields with `library`
provenance) is the app's job, calling the ordinary case-entity write with
provenance it builds itself; the library is only ever read from a case.
"""

from __future__ import annotations

import logging

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.errors import NotFoundError, ValidationError
from insolvia_core.firms import ADD_EDIT, CREDITOR_LIBRARY, VIEW_ONLY
from insolvia_core.library_creditors import (
    create_library_creditor,
    library_creditor_json,
    parse_library_creditor,
    replace_library_creditor,
)
from insolvia_core.ports import FirmStore

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies

logger = logging.getLogger(__name__)

blueprint = Blueprint("library_creditors", __name__)

# A name, an address, a handful of notice-party rows and a notes box — the
# same order of magnitude as a firm user record (routes/firm.py), not a
# document.
MAX_REQUEST_BYTES = 32 * 1024


def _firm_store() -> FirmStore:
    deps = dependencies()
    if deps.firm_store is None:
        raise RuntimeError("firm store is not composed")
    return deps.firm_store


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 32 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


@blueprint.get("/v1/firm/creditors")
@require_auth
@requires(CREDITOR_LIBRARY, VIEW_ONLY)
def list_library_creditors_route() -> ResponseReturnValue:
    accessor = current_accessor()
    creditors = _firm_store().list_library_creditors(accessor.firm_id)
    return jsonify({"creditors": [library_creditor_json(c) for c in creditors]}), 200


@blueprint.post("/v1/firm/creditors")
@require_auth
@requires(CREDITOR_LIBRARY, ADD_EDIT)
def add_library_creditor_route() -> ResponseReturnValue:
    accessor = current_accessor()
    draft = parse_library_creditor(_json_body())
    creditor = create_library_creditor(draft, firm_id=accessor.firm_id)
    _firm_store().create_library_creditor(creditor)

    # GLBA: no creditor name, no address. That a firm added SOMETHING to its
    # library is all a request log needs.
    logger.info("library creditor added")
    return jsonify(library_creditor_json(creditor)), 201


@blueprint.get("/v1/firm/creditors/<creditor_id>")
@require_auth
@requires(CREDITOR_LIBRARY, VIEW_ONLY)
def get_library_creditor_route(creditor_id: str) -> ResponseReturnValue:
    accessor = current_accessor()
    creditor = _firm_store().get_library_creditor(accessor.firm_id, creditor_id)
    if creditor is None:
        raise NotFoundError("library creditor not found")
    return jsonify(library_creditor_json(creditor)), 200


@blueprint.put("/v1/firm/creditors/<creditor_id>")
@require_auth
@requires(CREDITOR_LIBRARY, ADD_EDIT)
def update_library_creditor_route(creditor_id: str) -> ResponseReturnValue:
    """Replace a library creditor's whole record — see
    `insolvia_core.library_creditors` for why this is PUT rather than PATCH.

    Editing the library never touches a case that already copied from it: the
    copy carries its own values and only points back at this id in its
    provenance (the module docstring's "no live link" rule).
    """
    store = _firm_store()
    accessor = current_accessor()
    draft = parse_library_creditor(_json_body())

    existing = store.get_library_creditor(accessor.firm_id, creditor_id)
    if existing is None:
        raise NotFoundError("library creditor not found")

    updated = replace_library_creditor(existing, draft)
    written = store.update_library_creditor(updated)
    if written is None:
        # Removed between the read and the write. Same 404 as an id that was
        # never valid, rather than resurrecting the row.
        raise NotFoundError("library creditor not found")

    logger.info("library creditor updated")
    return jsonify(library_creditor_json(written)), 200


@blueprint.delete("/v1/firm/creditors/<creditor_id>")
@require_auth
@requires(CREDITOR_LIBRARY, ADD_EDIT)
def delete_library_creditor_route(creditor_id: str) -> ResponseReturnValue:
    accessor = current_accessor()
    if not _firm_store().delete_library_creditor(accessor.firm_id, creditor_id):
        raise NotFoundError("library creditor not found")

    logger.info("library creditor removed")
    return "", 204
