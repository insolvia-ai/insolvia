"""Case and form notes (issue 14.5 / #357): `/v1/cases/<id>/notes`.

A STATIC segment, so Werkzeug matches this ahead of the generic
`/v1/cases/<id>/<collection>` dispatch (case_entities.py's own docstring
explains the ranking) — the same reason debtors and documents are their own
modules rather than entries in `case_collections.COLLECTIONS`. `core/notes.py`
says why a note needs that: server-stamped authorship and per-record edit
ownership, neither of which the generic routes can express, and no
provenance, which they always enforce.

Storage is still the generic `CaseEntityStore` (`core/notes.NOTE` is an
ordinary `EntityKind`) — only the routing and the two things above are
bespoke.
"""

from __future__ import annotations

import logging

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access import Accessor
from insolvia_core.access_log import record_access
from insolvia_core.case_entities import (
    CaseEntity,
    EntityDraft,
    create_entity,
    replace_entity,
)
from insolvia_core.errors import ForbiddenError, NotFoundError, ValidationError
from insolvia_core.firms import ADD_EDIT, NOTES, VIEW_ONLY, full_name
from insolvia_core.notes import NOTE, NoteBody, may_edit_note, note_json, stamp_author
from insolvia_core.ports import AccessLog, CaseEntityStore, CaseStore

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies

logger = logging.getLogger(__name__)

blueprint = Blueprint("notes", __name__)

# A note is one narrative box (2000 chars, `parse_note_body`'s cap) plus a
# short form-series id — far smaller than a case entity with provenance, so a
# far smaller ceiling than case_entities.py's 256 KiB.
MAX_REQUEST_BYTES = 8 * 1024


def _stores() -> tuple[CaseStore, CaseEntityStore, AccessLog]:
    deps = dependencies()
    if (
        deps.case_store is None
        or deps.case_entity_store is None
        or deps.access_log is None
    ):
        raise RuntimeError("case store, entity store and access log are not composed")
    return deps.case_store, deps.case_entity_store, deps.access_log


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 8 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


def _reachable_case_or_404(accessor: Accessor, case_id: str, action: str) -> None:
    """Resolve the case first, and record the attempt either way — the same
    single authorisation path every case-child route uses. See
    case_entities.py's own copy of this function for the full argument."""
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
        raise NotFoundError("case not found")


def _note_or_404(case_id: str, note_id: str) -> CaseEntity[NoteBody]:
    _, entity_store, _ = _stores()
    entity = entity_store.get(case_id, NOTE, note_id)
    if entity is None:
        raise NotFoundError("note not found")
    return entity


def _require_owner_or_admin(accessor: Accessor, note: CaseEntity[NoteBody]) -> None:
    """The ownership seam core/notes.py adds: a note is editable by its
    author or by a firm admin, and by nobody else — even someone who otherwise
    holds `notes: add_edit`. Refused with 403, not 404: the caller already
    knows this note exists (they can see it in the case's own listing), so
    there is nothing to hide, only something to refuse — the same reasoning
    `ForbiddenError`'s own docstring gives for a per-feature refusal."""
    if not may_edit_note(
        note, subject=accessor.subject, is_admin=accessor.user.is_admin
    ):
        logger.info("note edit refused", extra={"reason": "not_author_or_admin"})
        raise ForbiddenError("only the author or a firm admin may change this note")


@blueprint.post("/v1/cases/<case_id>/notes")
@require_auth
@requires(NOTES, ADD_EDIT)
def create_note_route(case_id: str) -> ResponseReturnValue:
    """Add one note. The author is the caller, stamped server-side — never
    read from the request body (`core/notes.stamp_author`)."""
    _, entity_store, _ = _stores()
    accessor = current_accessor()

    body = NOTE.parse_body(_json_body())
    _reachable_case_or_404(accessor, case_id, "case.update")

    stamped = stamp_author(
        body, subject=accessor.subject, author_name=full_name(accessor.user)
    )
    entity = create_entity(
        NOTE, EntityDraft(body=stamped, provenance={}), case_id=case_id
    )
    entity_store.create(entity)
    logger.info(
        "note saved",
        extra={"case_id": case_id, "collection": NOTE.collection, "id": entity.id},
    )
    return jsonify(note_json(entity)), 201


@blueprint.get("/v1/cases/<case_id>/notes")
@require_auth
@requires(NOTES, VIEW_ONLY)
def list_notes_route(case_id: str) -> ResponseReturnValue:
    """Every note on the case, newest first — the opposite of the generic
    collections (case_entities.py lists oldest first, a schedule filled top
    to bottom), because a note is a running log: the one just left is the one
    worth seeing first, on the case overview and on a form's panel alike."""
    _, entity_store, _ = _stores()
    accessor = current_accessor()

    _reachable_case_or_404(accessor, case_id, "case.read")
    entities = entity_store.list_for_case(case_id, NOTE)
    return jsonify({"notes": [note_json(e) for e in reversed(entities)]}), 200


@blueprint.put("/v1/cases/<case_id>/notes/<note_id>")
@require_auth
@requires(NOTES, ADD_EDIT)
def put_note_route(case_id: str, note_id: str) -> ResponseReturnValue:
    """Replace a note's text/form anchor. Authorship does NOT change here —
    the new body is stamped with the STORED note's author, not the editor's,
    so a firm admin fixing a colleague's note cannot relabel it as their own
    (core/notes.stamp_author)."""
    _, entity_store, _ = _stores()
    accessor = current_accessor()

    body = NOTE.parse_body(_json_body())
    _reachable_case_or_404(accessor, case_id, "case.update")

    stored = _note_or_404(case_id, note_id)
    _require_owner_or_admin(accessor, stored)

    restamped = stamp_author(
        body,
        subject=stored.body.author_subject or accessor.subject,
        author_name=stored.body.author_name or full_name(accessor.user),
    )
    entity = replace_entity(stored, EntityDraft(body=restamped, provenance={}))
    if not entity_store.put(entity):
        raise NotFoundError("note not found")
    logger.info(
        "note saved",
        extra={"case_id": case_id, "collection": NOTE.collection, "id": entity.id},
    )
    return jsonify(note_json(entity)), 200


@blueprint.delete("/v1/cases/<case_id>/notes/<note_id>")
@require_auth
@requires(NOTES, ADD_EDIT)
def delete_note_route(case_id: str, note_id: str) -> ResponseReturnValue:
    """Remove a note. Same ownership rule as the edit route: author or admin."""
    _, entity_store, _ = _stores()
    accessor = current_accessor()

    _reachable_case_or_404(accessor, case_id, "case.update")
    stored = _note_or_404(case_id, note_id)
    _require_owner_or_admin(accessor, stored)

    if not entity_store.delete(case_id, NOTE, note_id):
        raise NotFoundError("note not found")
    logger.info(
        "note deleted",
        extra={"case_id": case_id, "collection": NOTE.collection, "id": note_id},
    )
    return "", 204
